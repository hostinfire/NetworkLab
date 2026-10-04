namespace SiscoNet.Core;

public sealed record ServiceResult(bool Success, string Summary, string Data, IReadOnlyList<SimulationEvent> Events);

public sealed class NetworkServiceEngine(NetworkProject project)
{
    private readonly Dictionary<Guid, string> _leases = [];
    private readonly Dictionary<(Guid Device, string Address), string> _natTranslations = [];
    private int _packetId;
    private int _ephemeralPort = 49151;

    public ServiceResult RequestHttp(DeviceModel client, string destinationAddress, bool secure = false)
    {
        var serviceName = secure ? "HTTPS" : "HTTP";
        var port = secure ? 443 : 80;
        var server = FindDevice(destinationAddress);
        if (server is null) return Failure($"No device owns IPv4 address {destinationAddress}.");
        var enabled = secure ? server.Services.HttpsEnabled : server.Services.HttpEnabled;
        if (!enabled) return Failure($"{serviceName} is disabled on {server.Name}.");
        var body = server.Services.HttpContent;
        return RequestTcp(client, server, serviceName, port, "GET /", body);
    }

    public ServiceResult RequestFtp(DeviceModel client, string destinationAddress, string operation = "LIST", string fileName = "")
    {
        var server = FindDevice(destinationAddress);
        if (server is null) return Failure($"No device owns IPv4 address {destinationAddress}.");
        if (!server.Services.FtpEnabled) return Failure($"FTP is disabled on {server.Name}.");
        string response;
        if (operation.Equals("LIST", StringComparison.OrdinalIgnoreCase))
            response = string.Join(Environment.NewLine, server.Services.FtpFiles.Keys.OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        else if (operation.Equals("GET", StringComparison.OrdinalIgnoreCase) && server.Services.FtpFiles.TryGetValue(fileName, out var contents))
            response = contents;
        else
            return Failure("FTP supports LIST or GET <file> for configured server files.");
        return RequestTcp(client, server, "FTP", 21, operation.ToUpperInvariant(), response);
    }

    public ServiceResult ResolveDns(DeviceModel client, string hostName)
    {
        if (!IpAddressing.IsValidIpv4(client.DnsServerAddress)) return Failure("No DNS server is configured on the client.");
        var dnsServer = FindDevice(client.DnsServerAddress);
        if (dnsServer is null || !dnsServer.Services.DnsEnabled) return Failure("The configured DNS server is unreachable or DNS is disabled.");
        if (!dnsServer.Services.DnsRecords.TryGetValue(hostName, out var address))
        {
            var miss = RequestUdp(client, dnsServer, "DNS", 53000, 53, $"A? {hostName}", "NXDOMAIN");
            return new ServiceResult(false, $"DNS: {hostName} not found.", "NXDOMAIN", miss.Events);
        }
        var result = RequestUdp(client, dnsServer, "DNS", 53000, 53, $"A? {hostName}", address);
        return result with { Summary = result.Success ? $"DNS: {hostName} resolves to {address}." : result.Summary, Data = address };
    }

    public ServiceResult RequestDhcp(DeviceModel client)
    {
        var clientInterface = client.Interfaces.FirstOrDefault(iface => iface.AdminUp && iface.LinkUp);
        if (clientInterface is null) return Failure("DHCP client has no active network interface.");
        var server = project.Devices.Where(device => device.Services.DhcpEnabled && device.Interfaces.Any(iface => iface.AdminUp && iface.LinkUp && IpAddressing.IsValidIpv4(iface.Ipv4Address)))
            .OrderBy(device => FindPath(client, device)?.Count ?? int.MaxValue)
            .FirstOrDefault(device => IsLayer2Reachable(client, device));
        if (server is null) return Failure("No DHCP server is reachable on the client's local network (DHCP relay is not configured).");
        if (!IpAddressing.IsValidIpv4(server.Services.DhcpPoolStart) || !IpAddressing.IsValidIpv4(server.Services.DhcpPoolEnd) ||
            !IpAddressing.IsValidSubnetMask(server.Services.DhcpSubnetMask)) return Failure("The DHCP pool configuration is invalid.");

        var lease = _leases.TryGetValue(client.Id, out var existing) ? existing : AllocateAddress(server, client);
        if (lease is null) return Failure("The DHCP address pool is exhausted.");
        _leases[client.Id] = lease;
        var events = BuildPathEvents(client, server, "DHCP", 68, 67, "DHCPDISCOVER / DHCPREQUEST", "DHCPOFFER / DHCPACK");
        if (events is null) return Failure("No active Layer 2 path reaches the DHCP server.");

        clientInterface.Ipv4Address = lease;
        clientInterface.SubnetMask = server.Services.DhcpSubnetMask;
        client.DefaultGateway = server.Services.DhcpGateway;
        client.DnsServerAddress = server.Services.DhcpDnsServer;
        var addressEvent = Event($"DHCP-{++_packetId:0000}", server, "DHCPACK", $"Lease {lease} / {server.Services.DhcpSubnetMask}; gateway {client.DefaultGateway}; DNS {client.DnsServerAddress}.",
            "DHCP", server.Interfaces.First(iface => iface.AdminUp && iface.LinkUp && IpAddressing.IsValidIpv4(iface.Ipv4Address)), clientInterface, 67, 68, "UDP", lease);
        events.Add(addressEvent);
        return new ServiceResult(true, $"DHCP lease acquired: {lease}.", lease, events);
    }

    public ServiceResult SendUdp(DeviceModel client, string destinationAddress, int destinationPort, string data)
    {
        var server = FindDevice(destinationAddress);
        if (server is null) return Failure($"No device owns IPv4 address {destinationAddress}.");
        return RequestUdp(client, server, "UDP datagram", NextPort(), destinationPort, data, "UDP datagram accepted.");
    }

    public ServiceResult SendTcp(DeviceModel client, string destinationAddress, int destinationPort, string data)
    {
        var server = FindDevice(destinationAddress);
        if (server is null) return Failure($"No device owns IPv4 address {destinationAddress}.");
        return RequestTcp(client, server, "TCP", destinationPort, data, "TCP application data acknowledged.");
    }

    private ServiceResult RequestTcp(DeviceModel client, DeviceModel server, string application, int port, string request, string response)
    {
        if (port is < 1 or > 65535) return Failure("Destination port must be between 1 and 65535.");
        var sourcePort = NextPort();
        var events = BuildPathEvents(client, server, application, sourcePort, port, request, response);
        if (events is null) return Failure($"No permitted, routed network path reaches {server.Name}.");
        var serverInterface = server.Interfaces.FirstOrDefault(i => i.AdminUp && i.LinkUp && IpAddressing.IsValidIpv4(i.Ipv4Address));
        var clientInterface = client.Interfaces.FirstOrDefault(i => i.AdminUp && i.LinkUp && IpAddressing.IsValidIpv4(i.Ipv4Address));
        if (serverInterface is null || clientInterface is null) return Failure("Client or server has no active IPv4 interface.");
        var packet = events[0].Packet;
        events.Insert(1, Event(packet, client, "TCP SYN", $"Open connection {sourcePort} -> {port}.", application,
            clientInterface, serverInterface, sourcePort, port, "TCP", "SYN"));
        events.Add(Event(packet, server, "TCP SYN-ACK", "Server accepted the connection and acknowledged the client sequence.", application,
            serverInterface, clientInterface, port, sourcePort, "TCP", "SYN, ACK"));
        events.Add(Event(packet, client, "TCP ACK", "Client completed the three-way handshake.", application,
            clientInterface, serverInterface, sourcePort, port, "TCP", "ACK"));
        if (application.Equals("HTTPS", StringComparison.OrdinalIgnoreCase))
            events.Add(Event(packet, client, "TLS handshake", "Educational TLS session established; cryptographic records are not modeled.", application,
                clientInterface, serverInterface, sourcePort, port, "TLS over TCP", "ClientHello / ServerHello"));
        events.Add(Event(packet, server, "TCP application response", response, application,
            serverInterface, clientInterface, port, sourcePort, "TCP", response));
        events.Add(Event(packet, client, "TCP FIN/ACK", "Connection closed after the application response.", application,
            clientInterface, serverInterface, sourcePort, port, "TCP", "FIN, ACK"));
        return new ServiceResult(true, $"{application} request to {server.Name}:{port} completed.", response, events);
    }

    private ServiceResult RequestUdp(DeviceModel client, DeviceModel server, string application, int sourcePort, int destinationPort, string request, string response)
    {
        var events = BuildPathEvents(client, server, application, sourcePort, destinationPort, request, response);
        if (events is null) return Failure($"No permitted, routed network path reaches {server.Name}.");
        events.Add(Event($"UDP-{_packetId:0000}", server, "UDP application response", response, application,
            server.Interfaces.First(i => i.AdminUp && i.LinkUp && IpAddressing.IsValidIpv4(i.Ipv4Address)),
            client.Interfaces.First(i => i.AdminUp && i.LinkUp), destinationPort, sourcePort, "UDP", request));
        return new ServiceResult(true, $"{application} request to {server.Name}:{destinationPort} completed.", response, events);
    }

    private List<SimulationEvent>? BuildPathEvents(DeviceModel client, DeviceModel server, string application,
        int sourcePort, int destinationPort, string request, string response)
    {
        SpanningTreeEngine.Recalculate(project);
        DynamicRoutingEngine.Recalculate(project);
        var clientInterface = client.Interfaces.FirstOrDefault(i => i.AdminUp && i.LinkUp &&
            (application.StartsWith("DHCP", StringComparison.OrdinalIgnoreCase) || IpAddressing.IsValidIpv4(i.Ipv4Address)));
        var serverInterface = server.Interfaces.FirstOrDefault(i => i.AdminUp && i.LinkUp && IpAddressing.IsValidIpv4(i.Ipv4Address));
        if (clientInterface is null || serverInterface is null) return null;
        var path = FindPath(client, server);
        if (path is null) return null;
        for (var index = 0; index < path.Count - 1; index++)
        {
            var left = GetInterface(path[index], path[index + 1]);
            var right = GetInterface(path[index + 1], path[index]);
            if (left is null || right is null || !left.AdminUp || !right.AdminUp) return null;
            var segmentVlan = left.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                ? right.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase) ? left.NativeVlan : right.Vlan
                : left.Vlan;
            if (!SwitchingEngine.LinkAllowsVlan(left, right, segmentVlan)) return null;
            if (left.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) || right.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase)) return null;
        }
        var sameSubnet = IpAddressing.IsValidIpv4(clientInterface.Ipv4Address) &&
            IpAddressing.IsInSameSubnet(clientInterface.Ipv4Address, serverInterface.Ipv4Address, clientInterface.SubnetMask);
        if (!sameSubnet)
        {
            if (!application.StartsWith("DHCP", StringComparison.OrdinalIgnoreCase) &&
                (RoutingPathValidator.Validate(project, path, client, server, clientInterface, serverInterface, out _) is not null ||
                 RoutingPathValidator.Validate(project, path.AsEnumerable().Reverse().ToArray(), server, client, serverInterface, clientInterface, out _) is not null)) return null;
        }
        if (!IsPermitted(path, clientInterface.Ipv4Address, serverInterface.Ipv4Address, application, destinationPort)) return null;

        var packet = $"{application.ToUpperInvariant()}-{++_packetId:0000}";
        var events = new List<SimulationEvent>
        {
            Event(packet, client, $"{application} request", request, application, clientInterface, serverInterface, sourcePort, destinationPort, TransportFor(application), request)
        };
        var translatedSourceAddress = clientInterface.Ipv4Address;
        for (var index = 1; index < path.Count - 1; index++)
        {
            var device = path[index];
            var ingress = GetInterface(device, path[index - 1]);
            var egress = GetInterface(device, path[index + 1]);
            if (SpanningTreeEngine.IsBridge(device))
            {
                if (ingress is null || egress is null) return null;
                var decision = SwitchingEngine.ProcessFrame(device, ingress.Name, clientInterface.MacAddress,
                    serverInterface.MacAddress, ingress.Vlan);
                if (!decision.Accepted || !decision.EgressInterfaces.Contains(egress.Name)) return null;
                events.Add(Event(packet, device, "Switch forwarding", decision.Detail, application, clientInterface, serverInterface,
                    sourcePort, destinationPort, TransportFor(application), request, ingress.Name, string.Join(",", decision.EgressInterfaces), ingress.Vlan));
            }
            else if (RoutingPathValidator.IsLayer3(device))
            {
                var route = RouteEngine.Resolve(device, serverInterface.Ipv4Address);
                if (route is null || egress is null || !route.InterfaceName.Equals(egress.Name, StringComparison.OrdinalIgnoreCase)) return null;
                var translated = ApplyNat(device, clientInterface.Ipv4Address, serverInterface.Ipv4Address, packet, application, sourcePort, destinationPort, events, clientInterface, serverInterface, ingress, egress);
                if (!string.IsNullOrWhiteSpace(translated)) translatedSourceAddress = translated;
                events.Add(Event(packet, device, "Route and forward", $"Selected {route.Network}/{route.PrefixLength} via {(string.IsNullOrEmpty(route.NextHop) ? "direct" : route.NextHop)}.",
                    application, clientInterface, serverInterface, sourcePort, destinationPort, TransportFor(application), request,
                    ingress?.Name ?? "", egress.Name, egress.Vlan, sourceAddressOverride: translatedSourceAddress));
            }
            else
                events.Add(Event(packet, device, "Ethernet forwarding", "Frame forwarded toward destination.", application,
                    clientInterface, serverInterface, sourcePort, destinationPort, TransportFor(application), request,
                    GetInterface(device, path[index - 1])?.Name ?? "", GetInterface(device, path[index + 1])?.Name ?? "", ingress?.Vlan ?? clientInterface.Vlan));
        }
        var lastHop = path.Count > 1 ? GetInterface(server, path[^2]) : serverInterface;
        if (lastHop is null) return null;
        events.Add(Event(packet, server, "Transport segment received", $"{TransportFor(application)} destination port {destinationPort}; application request delivered.",
            application, clientInterface, serverInterface, sourcePort, destinationPort, TransportFor(application), request,
            lastHop.Name, serverInterface.Name, lastHop.Vlan, sourceAddressOverride: translatedSourceAddress));
        for (var index = path.Count - 2; index >= 1; index--)
        {
            var device = path[index];
            var incoming = GetInterface(device, path[index + 1]);
            var outgoing = GetInterface(device, path[index - 1]);
            if (incoming is null || outgoing is null) return null;
            if (RoutingPathValidator.IsLayer3(device))
            {
                var route = RouteEngine.Resolve(device, clientInterface.Ipv4Address);
                if (route is null || route.InterfaceName != outgoing.Name) return null;
                var translatedDestination = _natTranslations.TryGetValue((device.Id, clientInterface.Ipv4Address), out var outsideAddress)
                    ? clientInterface.Ipv4Address : translatedSourceAddress;
                var isReverseNat = _natTranslations.TryGetValue((device.Id, clientInterface.Ipv4Address), out outsideAddress);
                events.Add(Event(packet, device, isReverseNat ? "NAT reverse translation" : "Reply route and forward",
                    isReverseNat ? $"Translated response destination {outsideAddress} back to {clientInterface.Ipv4Address}." : $"Selected return route {route.Network}/{route.PrefixLength}.",
                    application, serverInterface, clientInterface, destinationPort, sourcePort, TransportFor(application), response,
                    incoming.Name, outgoing.Name, outgoing.Vlan, destinationAddressOverride: translatedDestination));
            }
            else if (SpanningTreeEngine.IsBridge(device))
            {
                var decision = SwitchingEngine.ProcessFrame(device, incoming.Name, serverInterface.MacAddress,
                    clientInterface.MacAddress, outgoing.Vlan);
                if (!decision.Accepted || !decision.EgressInterfaces.Contains(outgoing.Name)) return null;
                events.Add(Event(packet, device, "Reply switch forwarding", decision.Detail, application, serverInterface, clientInterface,
                    destinationPort, sourcePort, TransportFor(application), response, incoming.Name, string.Join(",", decision.EgressInterfaces), outgoing.Vlan));
            }
        }
        return events;
    }

    private bool IsPermitted(IReadOnlyList<DeviceModel> path, string sourceIp, string destinationIp, string protocol, int destinationPort)
    {
        foreach (var device in path)
        {
            foreach (var rule in device.AccessRules)
            {
                var protocolMatches = rule.Protocol.Equals("ip", StringComparison.OrdinalIgnoreCase) ||
                    rule.Protocol.Equals(protocol, StringComparison.OrdinalIgnoreCase) ||
                    rule.Protocol.Equals(TransportFor(protocol), StringComparison.OrdinalIgnoreCase);
                var sourceMatches = IpAddressing.IsInSameSubnet(sourceIp, rule.SourceNetwork, rule.SourceMask);
                var destinationMatches = IpAddressing.IsInSameSubnet(destinationIp, rule.DestinationNetwork, rule.DestinationMask);
                var portMatches = rule.DestinationPort is null || rule.DestinationPort == destinationPort;
                if (protocolMatches && sourceMatches && destinationMatches && portMatches) return rule.Permit;
            }
        }
        return true;
    }

    private string ApplyNat(DeviceModel router, string insideAddress, string remoteAddress, string packet, string protocol,
        int sourcePort, int destinationPort, List<SimulationEvent> events, NetworkInterfaceModel source,
        NetworkInterfaceModel destination, NetworkInterfaceModel? ingress, NetworkInterfaceModel egress)
    {
        var nat = router.Nat;
        if (!nat.Enabled || !nat.InsideInterface.Equals(ingress?.Name, StringComparison.OrdinalIgnoreCase) ||
            !nat.OutsideInterface.Equals(egress.Name, StringComparison.OrdinalIgnoreCase) || !IpAddressing.IsValidIpv4(nat.OutsideAddress) ||
            !IpAddressing.IsValidSubnetMask(nat.InsideMask) || !IpAddressing.IsInSameSubnet(insideAddress, nat.InsideNetwork, nat.InsideMask)) return "";
        _natTranslations[(router.Id, insideAddress)] = nat.OutsideAddress;
        events.Add(Event(packet, router, "NAT translation", $"Translated {insideAddress}:{sourcePort} to {nat.OutsideAddress}:{sourcePort} for {remoteAddress}:{destinationPort}.",
            protocol, source, destination, sourcePort, destinationPort, TransportFor(protocol), insideAddress,
            ingress?.Name ?? "", egress.Name, egress.Vlan, sourceAddressOverride: nat.OutsideAddress));
        return nat.OutsideAddress;
    }

    private string? AllocateAddress(DeviceModel server, DeviceModel client)
    {
        var start = ToUInt(server.Services.DhcpPoolStart);
        var end = ToUInt(server.Services.DhcpPoolEnd);
        for (var address = start; address <= end; address++)
        {
            var candidate = FromUInt(address);
            if (project.Devices.SelectMany(device => device.Interfaces).Any(iface => iface.Ipv4Address == candidate)) continue;
            return candidate;
        }
        return null;
    }

    private DeviceModel? FindDevice(string address) => project.Devices.FirstOrDefault(device =>
        device.Interfaces.Any(iface => iface.Ipv4Address == address && iface.AdminUp && iface.LinkUp));

    private List<DeviceModel>? FindPath(DeviceModel source, DeviceModel target)
    {
        var queue = new Queue<DeviceModel>();
        var previous = new Dictionary<Guid, Guid?> { [source.Id] = null };
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Id == target.Id) break;
            foreach (var link in project.Links.Where(link => link.IsUp && (link.ADeviceId == current.Id || link.BDeviceId == current.Id)))
            {
                var fromName = current.Id == link.ADeviceId ? link.AInterface : link.BInterface;
                var nextId = current.Id == link.ADeviceId ? link.BDeviceId : link.ADeviceId;
                var nextName = current.Id == link.ADeviceId ? link.BInterface : link.AInterface;
                var next = project.Devices.FirstOrDefault(device => device.Id == nextId);
                if (current.Interfaces.FirstOrDefault(iface => iface.Name == fromName) is not { AdminUp: true, LinkUp: true } ||
                    next?.Interfaces.FirstOrDefault(iface => iface.Name == nextName) is not { AdminUp: true, LinkUp: true } || previous.ContainsKey(nextId)) continue;
                if (current.Interfaces.First(iface => iface.Name == fromName).StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) ||
                    next!.Interfaces.First(iface => iface.Name == nextName).StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase)) continue;
                previous[nextId] = current.Id;
                queue.Enqueue(next);
            }
        }
        if (!previous.ContainsKey(target.Id)) return null;
        var path = new List<DeviceModel>();
        Guid? cursor = target.Id;
        while (cursor is not null)
        {
            var device = project.Devices.First(item => item.Id == cursor.Value);
            path.Add(device);
            cursor = previous[cursor.Value];
        }
        path.Reverse();
        return path;
    }

    private NetworkInterfaceModel? GetInterface(DeviceModel device, DeviceModel neighbor)
    {
        var link = project.Links.FirstOrDefault(item => item.IsUp &&
            ((item.ADeviceId == device.Id && item.BDeviceId == neighbor.Id) || (item.BDeviceId == device.Id && item.ADeviceId == neighbor.Id)));
        if (link is null) return null;
        var name = link.ADeviceId == device.Id ? link.AInterface : link.BInterface;
        return device.Interfaces.FirstOrDefault(iface => iface.Name == name);
    }

    private bool IsLayer2Reachable(DeviceModel source, DeviceModel target)
    {
        var path = FindPath(source, target);
        if (path is null || path.Skip(1).SkipLast(1).Any(RoutingPathValidator.IsLayer3)) return false;
        return path.Zip(path.Skip(1), (left, right) => (GetInterface(left, right), GetInterface(right, left)))
            .All(pair => pair.Item1 is not null && pair.Item2 is not null && pair.Item1.Vlan == pair.Item2.Vlan);
    }

    private SimulationEvent Event(string packet, DeviceModel device, string action, string detail, string application,
        NetworkInterfaceModel source, NetworkInterfaceModel destination, int sourcePort, int destinationPort, string transport,
        string data, string incoming = "", string outgoing = "", int vlan = 1, string sourceAddressOverride = "", string destinationAddressOverride = "") =>
        new(DateTime.Now, packet, device.Name, action, detail,
            string.IsNullOrWhiteSpace(sourceAddressOverride) ? source.Ipv4Address : sourceAddressOverride,
            string.IsNullOrWhiteSpace(destinationAddressOverride) ? destination.Ipv4Address : destinationAddressOverride,
            $"{transport} / {application}", source.MacAddress, destination.MacAddress, incoming, outgoing, 64, vlan,
            sourcePort, destinationPort, transport, data.Length <= 512 ? data : data[..512]);

    private static string TransportFor(string application) => application.Equals("DNS", StringComparison.OrdinalIgnoreCase) ||
        application.StartsWith("DHCP", StringComparison.OrdinalIgnoreCase) || application.Equals("UDP datagram", StringComparison.OrdinalIgnoreCase)
            ? "UDP" : "TCP";

    private static uint ToUInt(string address) => BitConverter.ToUInt32(System.Net.IPAddress.Parse(address).GetAddressBytes().Reverse().ToArray());
    private static string FromUInt(uint address) => new System.Net.IPAddress(BitConverter.GetBytes(address).Reverse().ToArray()).ToString();
    private int NextPort() => Interlocked.Increment(ref _ephemeralPort) is var port && port > 65535 ? _ephemeralPort = 49152 : port;
    private static ServiceResult Failure(string message) => new(false, $"Request failed: {message}", "", []);
}