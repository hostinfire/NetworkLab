using System.IO;
using System.IO.Pipes;
using System.Text.Json;

namespace SiscoNet.Core;

public sealed class DiscordPresenceSettings
{
    public string ApplicationId { get; set; } = "";
}

public static class DiscordPresenceSettingsStore
{
    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NorthstarNetworkLab", "settings.json");

    public static DiscordPresenceSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return new DiscordPresenceSettings();
            return JsonSerializer.Deserialize<DiscordPresenceSettings>(File.ReadAllText(SettingsPath)) ?? new DiscordPresenceSettings();
        }
        catch (JsonException) { return new DiscordPresenceSettings(); }
        catch (IOException) { return new DiscordPresenceSettings(); }
    }

    public static void Save(DiscordPresenceSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporaryPath = SettingsPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, SettingsPath, true);
    }
}

public sealed class DiscordPresenceService : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _activityChanged = new(0, 1);
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private Activity _activity = new("Network lab", "Ready");
    private string _applicationId = "";
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private bool _disposed;

    public event Action<string>? StatusChanged;

    public void Configure(string applicationId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            _applicationId = applicationId.Trim();
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            _worker = null;
            if (_applicationId.Length == 0)
            {
                PublishStatus("Discord presence off");
                return;
            }
            if (_applicationId.Length is < 17 or > 20 || !ulong.TryParse(_applicationId, out _))
                throw new ArgumentException("Enter the 17- to 20-digit numeric Discord Application ID.", nameof(applicationId));
            PublishStatus("Connecting to Discord...");
            _cancellation = new CancellationTokenSource();
            _worker = RunAsync(_applicationId, _cancellation.Token);
        }
    }

    public void Update(string details, string state)
    {
        lock (_gate)
        {
            _activity = new Activity(details, state);
            if (_activityChanged.CurrentCount == 0) _activityChanged.Release();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
        }
    }

    private async Task RunAsync(string applicationId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeClientStream? pipe = null;
            try
            {
                pipe = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                if (pipe is null)
                {
                    PublishStatus("Discord not detected; retrying");
                    await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken).ConfigureAwait(false);
                    continue;
                }

                await pipe.WriteAsync(DiscordIpcProtocol.CreateHandshake(applicationId), cancellationToken).ConfigureAwait(false);
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
                var ready = await DiscordIpcProtocol.ReadFrameAsync(pipe, cancellationToken)
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                using (var readyDocument = JsonDocument.Parse(ready))
                {
                    if (!readyDocument.RootElement.TryGetProperty("evt", out var eventName) || eventName.GetString() != "READY")
                        throw new IOException("Discord did not return a READY event.");
                }

                PublishStatus("Discord connected");
                await SendActivityAsync(pipe, cancellationToken).ConfigureAwait(false);
                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    try { await _activityChanged.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                    await SendActivityAsync(pipe, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or InvalidDataException)
            {
                PublishStatus("Discord disconnected; reconnecting");
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { pipe?.Dispose(); }
        }
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(CancellationToken cancellationToken)
    {
        for (var pipeIndex = 0; pipeIndex < 10; pipeIndex++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{pipeIndex}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(750, cancellationToken).ConfigureAwait(false);
                return pipe;
            }
            catch (TimeoutException) { pipe.Dispose(); }
            catch (IOException) { pipe.Dispose(); }
        }
        return null;
    }

    private Activity GetActivity()
    {
        lock (_gate) return _activity;
    }

    private async Task SendActivityAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        var activity = GetActivity();
        var nonce = Guid.NewGuid().ToString();
        var frame = DiscordIpcProtocol.CreateSetActivity(activity.Details, activity.State, _startedAt, nonce);
        await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        var response = await DiscordIpcProtocol.ReadFrameAsync(pipe, cancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(response);
        var root = document.RootElement;
        if (root.TryGetProperty("evt", out var eventName) && eventName.ValueKind == JsonValueKind.String && eventName.GetString() == "ERROR")
            throw new IOException("Discord rejected the Rich Presence activity.");
        if (root.TryGetProperty("nonce", out var responseNonce) && responseNonce.GetString() != nonce)
            throw new IOException("Discord returned an unexpected Rich Presence response.");
    }

    private void PublishStatus(string status) => StatusChanged?.Invoke(status);

    private sealed record Activity(string Details, string State);
}