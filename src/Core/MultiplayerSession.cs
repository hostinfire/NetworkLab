using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SiscoNet.Core;

public sealed record MultiplayerLock(Guid ScopeId, string OwnerId, string OwnerName);
public sealed record MultiplayerPeer(string Id, string Name);

public sealed class MultiplayerSession : IAsyncDisposable
{
    public const int DefaultPort = 47831;
    public const string GlobalScope = "00000000-0000-0000-0000-000000000000";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly SemaphoreSlim _projectGate = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<MultiplayerMessage>> _pending = new();
    private readonly Dictionary<string, PeerConnectionInfo> _peers = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, MultiplayerLock> _locks = [];
    private CancellationTokenSource _shutdown = new();
    private TcpListener? _listener;
    private Task? _acceptTask;
    private NetworkProject? _project;
    private string _localPeerId = "";
    private string _localName = "";

    public bool IsHosting { get; private set; }
    public bool IsConnected { get; private set; }
    public bool IsSharing => IsHosting || IsConnected;
    public string LocalPeerId => _localPeerId;
    public int Port { get; private set; }
    public string Status { get; private set; } = "Not connected";
    public IReadOnlyList<MultiplayerPeer> Peers { get; private set; } = [];
    public IReadOnlyList<MultiplayerLock> Locks { get; private set; } = [];

    public event Action<string>? StatusChanged;
    public event Action<NetworkProject>? ProjectReceived;
    public event Action<IReadOnlyList<MultiplayerPeer>>? PeersChanged;
    public event Action<IReadOnlyList<MultiplayerLock>>? LocksChanged;

    public async Task StartSharingAsync(NetworkProject project, string displayName, int port = DefaultPort, CancellationToken cancellationToken = default)
    {
        ThrowIfSharing();
        ResetCancellation();
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _localPeerId = "host";
        _localName = NormalizeName(displayName);
        _project = project;
        _listener = new TcpListener(IPAddress.Any, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        IsHosting = true;
        IsConnected = true;
        SetStatus($"Sharing on port {Port}");
        _acceptTask = AcceptPeersAsync(_listener, _shutdown.Token);
        await Task.CompletedTask;
    }

    public async Task ConnectAsync(string hostAddress, int port, string displayName, CancellationToken cancellationToken = default)
    {
        ThrowIfSharing();
        ResetCancellation();
        if (!IPAddress.TryParse(hostAddress.Trim(), out var address)) throw new ArgumentException("Enter a valid IPv4 or IPv6 host address.", nameof(hostAddress));
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _localPeerId = Guid.NewGuid().ToString("N");
        _localName = NormalizeName(displayName);
        SetStatus($"Connecting to {address}:{port}...");

        var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        try { await client.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false); }
        catch { client.Dispose(); SetStatus($"Could not connect to {address}:{port}."); throw; }
        var connection = new PeerConnection(client, "host", "Host");
        connection.Start(HandleClientMessageAsync, RemovePeerAsync, _shutdown.Token);
        IsConnected = true;
        Port = port;
        lock (_peers) _peers["host"] = new PeerConnectionInfo("host", "Host", connection);
        try
        {
            await connection.SendAsync(new MultiplayerMessage
            {
                Type = "join",
                PeerId = _localPeerId,
                PeerName = _localName
            }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_peers) _peers.Remove("host");
            IsConnected = false;
            Port = 0;
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        PublishPeers();
        SetStatus($"Connected to {address}:{port}");
    }

    public bool TryAcquireLock(Guid scopeId, out string reason)
    {
        if (!IsSharing) { reason = ""; return true; }
        if (IsHosting) return TryAcquireHostLock(scopeId, out reason);

        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<MultiplayerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        var message = new MultiplayerMessage { Type = "lock-request", RequestId = requestId, ScopeId = scopeId, PeerId = _localPeerId };
        if (!SendToHost(message, out reason)) { _pending.TryRemove(requestId, out _); return false; }
        try
        {
            var response = completion.Task.Wait(TimeSpan.FromSeconds(3)) ? completion.Task.Result : null;
            if (response?.Accepted == true) { reason = ""; return true; }
            reason = response?.Error ?? "The host did not respond to the lock request.";
            return false;
        }
        finally { _pending.TryRemove(requestId, out _); }
    }

    public void ReleaseLock(Guid scopeId)
    {
        if (!IsSharing) return;
        if (IsHosting)
        {
            lock (_locks)
            {
                if (_locks.TryGetValue(scopeId, out var lease) && lease.OwnerId == _localPeerId)
                    _locks.Remove(scopeId);
            }
            PublishLocks();
            return;
        }
        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<MultiplayerMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;
        if (SendToHost(new MultiplayerMessage { Type = "lock-release", RequestId = requestId, ScopeId = scopeId, PeerId = _localPeerId }, out _))
        {
            try { completion.Task.Wait(TimeSpan.FromSeconds(3)); }
            finally { _pending.TryRemove(requestId, out _); }
        }
        else _pending.TryRemove(requestId, out _);
    }

    public bool IsLockedByOther(Guid scopeId)
    {
        if (!IsSharing) return false;
        lock (_locks)
        {
            if (_locks.TryGetValue(Guid.Empty, out var global) && global.OwnerId != _localPeerId) return true;
            return _locks.TryGetValue(scopeId, out var lease) && lease.OwnerId != _localPeerId;
        }
    }

    public string? GetLockOwner(Guid scopeId)
    {
        lock (_locks)
        {
            if (_locks.TryGetValue(scopeId, out var lease)) return lease.OwnerName;
            return _locks.TryGetValue(Guid.Empty, out var global) ? global.OwnerName : null;
        }
    }

    public void PublishProject(NetworkProject project, Guid scopeId)
    {
        if (!IsSharing) return;
        _project = project;
        var serialized = JsonSerializer.Serialize(project, JsonOptions);
        if (IsHosting)
        {
            if (!OwnsLock(scopeId, _localPeerId))
            {
                SetStatus("Update withheld: acquire the object lock before editing.");
                return;
            }
            Broadcast(new MultiplayerMessage { Type = "project", ScopeId = scopeId, Payload = serialized, PeerId = _localPeerId });
            return;
        }
        SendToHost(new MultiplayerMessage { Type = "project", ScopeId = scopeId, Payload = serialized, PeerId = _localPeerId }, out var error);
        if (!string.IsNullOrEmpty(error)) SetStatus(error);
    }

    public void ReloadFromHost()
    {
        if (!IsSharing) return;
        if (IsHosting)
        {
            if (_project is not null) ProjectReceived?.Invoke(Clone(_project));
            SetStatus("Shared project reloaded locally.");
            return;
        }
        SendToHost(new MultiplayerMessage { Type = "reload-request", PeerId = _localPeerId }, out var error);
        if (!string.IsNullOrEmpty(error)) SetStatus(error);
    }

    public async Task DisconnectAsync()
    {
        if (!IsSharing) return;
        if (!IsHosting)
            SendToHost(new MultiplayerMessage { Type = "leave", PeerId = _localPeerId }, out _);
        _shutdown.Cancel();
        _listener?.Stop();
        PeerConnection[] connections;
        lock (_peers) { connections = _peers.Values.Select(item => item.Connection).Where(item => item is not null).Cast<PeerConnection>().ToArray(); _peers.Clear(); }
        foreach (var connection in connections) await connection.DisposeAsync().ConfigureAwait(false);
        if (_acceptTask is not null)
        {
            try { await _acceptTask.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or ObjectDisposedException) { }
        }
        lock (_locks) _locks.Clear();
        _shutdown.Dispose();
        _shutdown = new CancellationTokenSource();
        IsHosting = false;
        IsConnected = false;
        Port = 0;
        Peers = [];
        Locks = [];
        PeersChanged?.Invoke(Peers);
        LocksChanged?.Invoke(Locks);
        SetStatus("Disconnected");
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task AcceptPeersAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                var connection = new PeerConnection(client, "", "Peer");
                connection.Start(HandleHostMessageAsync, RemovePeerAsync, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (SocketException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { SetStatus($"Host listener stopped: {ex.Message}"); }
    }

    private async Task HandleHostMessageAsync(PeerConnection connection, MultiplayerMessage message)
    {
        switch (message.Type)
        {
            case "join":
                connection.SetIdentity(message.PeerId, NormalizeName(message.PeerName));
                lock (_peers) _peers[message.PeerId] = new PeerConnectionInfo(message.PeerId, connection.Name, connection);
                await connection.SendAsync(new MultiplayerMessage { Type = "snapshot", Payload = JsonSerializer.Serialize(_project ?? new NetworkProject(), JsonOptions) }, _shutdown.Token).ConfigureAwait(false);
                await SendLockSnapshotAsync(connection).ConfigureAwait(false);
                PublishPeers();
                SetStatus($"{connection.Name} joined the shared topology.");
                break;
            case "lock-request":
                var accepted = TryAcquirePeerLock(connection, message.ScopeId, out var lockError);
                await connection.SendAsync(new MultiplayerMessage
                {
                    Type = "lock-response", RequestId = message.RequestId, Accepted = accepted,
                    Error = lockError, ScopeId = message.ScopeId
                }, _shutdown.Token).ConfigureAwait(false);
                if (accepted) PublishLocks();
                break;
            case "lock-release":
                ReleasePeerLock(connection, message.ScopeId);
                PublishLocks();
                await connection.SendAsync(new MultiplayerMessage { Type = "lock-release-response", RequestId = message.RequestId, Accepted = true }, _shutdown.Token).ConfigureAwait(false);
                break;
            case "project":
                await HandleProjectUpdateAsync(connection, message).ConfigureAwait(false);
                break;
            case "reload-request":
                if (_project is not null)
                    await connection.SendAsync(new MultiplayerMessage { Type = "snapshot", Payload = JsonSerializer.Serialize(_project, JsonOptions) }, _shutdown.Token).ConfigureAwait(false);
                break;
            case "leave":
                await RemovePeerAsync(connection).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleProjectUpdateAsync(PeerConnection connection, MultiplayerMessage message)
    {
        if (!OwnsLock(message.ScopeId, connection.Id))
        {
            await connection.SendAsync(new MultiplayerMessage { Type = "error", Error = "Project update rejected: this peer does not own the edit lock." }, _shutdown.Token).ConfigureAwait(false);
            return;
        }
        await _projectGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
        try
        {
            var incoming = JsonSerializer.Deserialize<NetworkProject>(message.Payload, JsonOptions)
                ?? throw new InvalidDataException("Empty project update.");
            if (_project is null || message.ScopeId == Guid.Empty) _project = incoming;
            else MergeDevice(_project, incoming, message.ScopeId);
            var complete = JsonSerializer.Serialize(_project, JsonOptions);
            ProjectReceived?.Invoke(Clone(_project));
            Broadcast(new MultiplayerMessage { Type = "snapshot", Payload = complete, ScopeId = message.ScopeId, PeerId = connection.Id });
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            await connection.SendAsync(new MultiplayerMessage { Type = "error", Error = $"Invalid shared update: {ex.Message}" }, _shutdown.Token).ConfigureAwait(false);
        }
        finally { _projectGate.Release(); }
    }

    private Task HandleClientMessageAsync(PeerConnection connection, MultiplayerMessage message)
    {
        if (message.Type == "lock-response" && _pending.TryGetValue(message.RequestId, out var completion))
        {
            completion.TrySetResult(message);
            if (message.Accepted) SetStatus($"Edit lock acquired ({FormatScope(message.ScopeId)}).");
            return Task.CompletedTask;
        }
        if (message.Type == "lock-release-response" && _pending.TryGetValue(message.RequestId, out completion))
        {
            completion.TrySetResult(message);
            return Task.CompletedTask;
        }
        switch (message.Type)
        {
            case "snapshot":
                var project = JsonSerializer.Deserialize<NetworkProject>(message.Payload, JsonOptions);
                if (project is not null) { _project = project; ProjectReceived?.Invoke(Clone(project)); SetStatus("Shared project synchronized."); }
                break;
            case "locks":
                lock (_locks)
                {
                    _locks.Clear();
                    foreach (var item in message.LockList) _locks[item.ScopeId] = item;
                    Locks = _locks.Values.ToArray();
                }
                LocksChanged?.Invoke(Locks);
                break;
            case "peers":
                Peers = message.PeerList;
                PeersChanged?.Invoke(Peers);
                break;
            case "error":
                SetStatus(message.Error);
                break;
        }
        return Task.CompletedTask;
    }

    private bool TryAcquireHostLock(Guid scopeId, out string reason)
    {
        lock (_locks)
        {
            if (!CanAcquire(scopeId, _localPeerId, out reason)) return false;
            _locks[scopeId] = new MultiplayerLock(scopeId, _localPeerId, _localName);
            Locks = _locks.Values.ToArray();
        }
        PublishLocks();
        return true;
    }

    private bool TryAcquirePeerLock(PeerConnection peer, Guid scopeId, out string reason)
    {
        lock (_locks)
        {
            if (!CanAcquire(scopeId, peer.Id, out reason)) return false;
            _locks[scopeId] = new MultiplayerLock(scopeId, peer.Id, peer.Name);
            Locks = _locks.Values.ToArray();
        }
        reason = "";
        return true;
    }

    private bool CanAcquire(Guid scopeId, string ownerId, out string reason)
    {
        if (scopeId == Guid.Empty)
        {
            var blocker = _locks.Values.FirstOrDefault(lease => lease.OwnerId != ownerId);
            if (blocker is not null) { reason = $"Topology is in use by {blocker.OwnerName}."; return false; }
            reason = "";
            return true;
        }
        if (_locks.TryGetValue(Guid.Empty, out var global) && global.OwnerId != ownerId)
        {
            reason = $"Topology is being changed by {global.OwnerName}.";
            return false;
        }
        if (_locks.TryGetValue(scopeId, out var existing) && existing.OwnerId != ownerId)
        {
            reason = $"Object is being changed by {existing.OwnerName}.";
            return false;
        }
        reason = "";
        return true;
    }

    private bool OwnsLock(Guid scopeId, string ownerId)
    {
        lock (_locks) return _locks.TryGetValue(scopeId, out var lease) && lease.OwnerId == ownerId;
    }

    private void ReleasePeerLock(PeerConnection peer, Guid scopeId)
    {
        lock (_locks)
        {
            if (_locks.TryGetValue(scopeId, out var existing) && existing.OwnerId == peer.Id)
                _locks.Remove(scopeId);
            Locks = _locks.Values.ToArray();
        }
    }

    private async Task SendLockSnapshotAsync(PeerConnection connection)
    {
        MultiplayerLock[] snapshot;
        lock (_locks) snapshot = _locks.Values.ToArray();
        await connection.SendAsync(new MultiplayerMessage { Type = "locks", LockList = snapshot }, _shutdown.Token).ConfigureAwait(false);
    }

    private void PublishLocks()
    {
        lock (_locks) Locks = _locks.Values.ToArray();
        Broadcast(new MultiplayerMessage { Type = "locks", LockList = Locks.ToArray() });
        LocksChanged?.Invoke(Locks);
    }

    private void PublishPeers()
    {
        lock (_peers)
            Peers = new[] { new MultiplayerPeer(_localPeerId, _localName) }
                .Concat(_peers.Values.Select(peer => new MultiplayerPeer(peer.Id, peer.Name))).ToArray();
        Broadcast(new MultiplayerMessage { Type = "peers", PeerList = Peers.ToArray() });
        PeersChanged?.Invoke(Peers);
    }

    private void Broadcast(MultiplayerMessage message)
    {
        PeerConnection[] peers;
        lock (_peers) peers = _peers.Values.Select(info => info.Connection).Where(connection => connection is not null).Cast<PeerConnection>().ToArray();
        foreach (var peer in peers) _ = peer.SendAsync(message, _shutdown.Token);
    }

    private bool SendToHost(MultiplayerMessage message, out string error)
    {
        PeerConnection? host;
        lock (_peers) host = _peers.TryGetValue("host", out var info) ? info.Connection : null;
        if (host is null) { error = "Not connected to a host."; return false; }
        try { host.SendAsync(message, _shutdown.Token).GetAwaiter().GetResult(); error = ""; return true; }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            error = $"Peer connection failed: {ex.Message}";
            SetStatus(error);
            return false;
        }
    }

    private async Task RemovePeerAsync(PeerConnection connection)
    {
        if (string.IsNullOrEmpty(connection.Id))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }
        lock (_peers) _peers.Remove(connection.Id);
        lock (_locks)
        {
            foreach (var scope in _locks.Where(item => item.Value.OwnerId == connection.Id).Select(item => item.Key).ToArray()) _locks.Remove(scope);
            Locks = _locks.Values.ToArray();
        }
        await connection.DisposeAsync().ConfigureAwait(false);
        if (IsHosting) { PublishPeers(); PublishLocks(); SetStatus($"{connection.Name} disconnected."); }
        else if (connection.Id == "host")
        {
            IsConnected = false;
            SetStatus("Host disconnected. Reload or reconnect to resume sharing.");
        }
    }

    private static void MergeDevice(NetworkProject target, NetworkProject source, Guid deviceId)
    {
        var deviceIndex = target.Devices.FindIndex(device => device.Id == deviceId);
        var incomingDevice = source.Devices.FirstOrDefault(device => device.Id == deviceId);
        if (deviceIndex >= 0 && incomingDevice is not null) target.Devices[deviceIndex] = incomingDevice;
        target.Links.RemoveAll(link => link.ADeviceId == deviceId || link.BDeviceId == deviceId);
        target.Links.AddRange(source.Links.Where(link => link.ADeviceId == deviceId || link.BDeviceId == deviceId));
    }

    private static string FormatScope(Guid scopeId) => scopeId == Guid.Empty ? "topology" : $"device {scopeId.ToString("N")[..8]}";
    private static string NormalizeName(string name) => string.IsNullOrWhiteSpace(name) ? Environment.UserName : name.Trim()[..Math.Min(name.Trim().Length, 32)];
    private static NetworkProject Clone(NetworkProject project) => JsonSerializer.Deserialize<NetworkProject>(JsonSerializer.Serialize(project, JsonOptions), JsonOptions) ?? new NetworkProject();
    private void SetStatus(string status) { Status = status; StatusChanged?.Invoke(status); }
    private void ThrowIfSharing() { if (IsSharing) throw new InvalidOperationException("Disconnect the current multiplayer session first."); }
    private void ResetCancellation()
    {
        if (_shutdown.IsCancellationRequested)
        {
            _shutdown.Dispose();
            _shutdown = new CancellationTokenSource();
        }
    }

    private sealed record PeerConnectionInfo(string Id, string Name, PeerConnection? Connection = null);

    private sealed class PeerConnection(TcpClient client, string id, string name) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly StreamReader _reader = new(client.GetStream());
        private readonly StreamWriter _writer = new(client.GetStream()) { AutoFlush = true };
        private int _disposed;
        private string _id = id;
        private string _name = name;
        public string Id => _id;
        public string Name => _name;

        public void SetIdentity(string peerId, string peerName) { _id = peerId; _name = peerName; }

        public void Start(Func<PeerConnection, MultiplayerMessage, Task> onMessage,
            Func<PeerConnection, Task> onClosed, CancellationToken cancellationToken) =>
            _ = ReadLoopAsync(onMessage, onClosed, cancellationToken);

        public async Task SendAsync(MultiplayerMessage message, CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var json = JsonSerializer.Serialize(message, JsonOptions);
                await _writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally { _writeGate.Release(); }
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
            client.Dispose();
            _reader.Dispose();
            _writer.Dispose();
            _writeGate.Dispose();
            return ValueTask.CompletedTask;
        }

        private async Task ReadLoopAsync(Func<PeerConnection, MultiplayerMessage, Task> onMessage,
            Func<PeerConnection, Task> onClosed, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var line = await _reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                    if (line is null) break;
                    if (line.Length > 5_000_000) throw new InvalidDataException("Multiplayer message exceeds the 5 MB limit.");
                    var message = JsonSerializer.Deserialize<MultiplayerMessage>(line, JsonOptions);
                    if (message is not null) await onMessage(this, message).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex) when (ex is IOException or SocketException or JsonException or ObjectDisposedException or InvalidDataException) { }
            finally { await onClosed(this).ConfigureAwait(false); }
        }
    }

    private sealed class MultiplayerMessage
    {
        public string Type { get; set; } = "";
        public Guid RequestId { get; set; }
        public Guid ScopeId { get; set; }
        public string PeerId { get; set; } = "";
        public string PeerName { get; set; } = "";
        public string Payload { get; set; } = "";
        public string Error { get; set; } = "";
        public bool Accepted { get; set; }
        public MultiplayerLock[] LockList { get; set; } = [];
        public MultiplayerPeer[] PeerList { get; set; } = [];
    }
}