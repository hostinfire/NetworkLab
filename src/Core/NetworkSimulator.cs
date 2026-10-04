namespace SiscoNet.Core;

public sealed class NetworkSimulator
{
    private NetworkProject? _project;
    private int _packetNumber;

    public PingResult Ping(NetworkProject project, DeviceModel source, string destinationAddress)
    {
        _project = project;
        SpanningTreeEngine.Recalculate(project);
        DynamicRoutingEngine.Recalculate(project);
        var events = new List<SimulationEvent>();
        var sourceInterface = source.Interfaces.FirstOrDefault(i => i.AdminUp && i.LinkUp && IpAddressing.IsValidIpv4(i.Ipv4Address));
        var destination = project.Devices.FirstOrDefault(d => d.Interfaces.Any(i => i.Ipv4Address == destinationAddress));
        if (sourceInterface is null)
            return Failure("Source has no enabled interface with a valid IPv4 address.");
        if (destination is null)
            return Failure($"No device owns IPv4 address {destinationAddress}.");
        var destinationInterface = destination.Interfaces.First(i => i.Ipv4Address == destinationAddress);
        if (!destinationInterface.AdminUp || !destinationInterface.LinkUp)
            return Failure("Destination interface is administratively or operationally down.");

        var path = FindPath(project, source, destination);
        if (path is null)
            return Failure("No active physical path connects the source and destination.");

        for (var index = 0; index < path.Count - 1; index++)
        {
            var left = FindInterfaceOnPath(project, path[index], path[index + 1]);
            var right = FindInterfaceOnPath(project, path[index + 1], path[index]);
            if (left is not null && right is not null)
            {
                var vlan = left.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                    ? right.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase) ? left.NativeVlan : right.Vlan
                    : left.Vlan;
                if (!SwitchingEngine.LinkAllowsVlan(left, right, vlan))
                    return Failure($"VLAN {vlan} is not admitted on link {path[index].Name} <-> {path[index + 1].Name}.");
                if (left.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) || right.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase))
                    return Failure($"STP blocks link {path[index].Name} <-> {path[index + 1].Name}.");
            }
        }

        var sameSubnet = IpAddressing.IsInSameSubnet(sourceInterface.Ipv4Address, destinationAddress, sourceInterface.SubnetMask);
        IReadOnlyDictionary<Guid, RouteDecision> forwardRoutes = new Dictionary<Guid, RouteDecision>();
        IReadOnlyDictionary<Guid, RouteDecision> reverseRoutes = new Dictionary<Guid, RouteDecision>();
        if (!sameSubnet)
        {
            var routeError = RoutingPathValidator.Validate(project, path, source, destination, sourceInterface, destinationInterface, out forwardRoutes);
            if (routeError is not null) return Failure(routeError);
            var reversePath = path.AsEnumerable().Reverse().ToArray();
            routeError = RoutingPathValidator.Validate(project, reversePath, destination, source, destinationInterface, sourceInterface, out reverseRoutes);
            if (routeError is not null) return Failure($"Return path: {routeError}");
        }

        var packet = $"ICMP-{++_packetNumber:0000}";
        var arpTarget = sameSubnet ? destinationAddress : source.DefaultGateway;
        Add(events, packet, source, "ARP request", $"Who has {arpTarget}? Tell {sourceInterface.Ipv4Address}.",
            sourceInterface, destinationInterface, vlan: sourceInterface.Vlan, protocol: "ARP / Ethernet", destinationMacOverride: "FF:FF:FF:FF:FF:FF");
        var sourceMac = sourceInterface.MacAddress;
        for (var index = 1; index < path.Count - 1; index++)
        {
            var device = path[index];
            var incoming = FindInterfaceOnPath(project, device, path[index - 1]);
            var outgoing = FindInterfaceOnPath(project, device, path[index + 1]);
            if (SpanningTreeEngine.IsBridge(device))
            {
                if (incoming is null || outgoing is null) return Failure($"No eligible switch path through {device.Name}.");
                var decision = SwitchingEngine.ProcessFrame(device, incoming.Name, sourceMac, destinationInterface.MacAddress, incoming.Vlan);
                if (!decision.Accepted || !decision.EgressInterfaces.Contains(outgoing.Name)) return Failure($"{device.Name}: {decision.Detail}");
                Add(events, packet, device, "MAC learning", decision.Detail, sourceInterface, destinationInterface, incoming.Name, string.Join(",", decision.EgressInterfaces), incoming.Vlan);
            }
            else if (device.Kind.Contains("Router", StringComparison.OrdinalIgnoreCase) || device.Kind.Contains("Layer 3", StringComparison.OrdinalIgnoreCase))
            {
                var route = forwardRoutes.GetValueOrDefault(device.Id);
                Add(events, packet, device, "Routing lookup",
                    route is null ? $"Forwarded toward {destinationAddress}; TTL decremented." : $"Matched {route.Source} route {route.Network}/{route.PrefixLength} via {(string.IsNullOrWhiteSpace(route.NextHop) ? "direct" : route.NextHop)}; TTL decremented.",
                    sourceInterface, destinationInterface, incoming?.Name ?? "", outgoing?.Name ?? "", outgoing?.Vlan ?? 1, 63);
            }
            else
                Add(events, packet, device, "Frame forwarding", $"Forwarded frame toward {destination.Name}.", sourceInterface, destinationInterface, incoming?.Name ?? "", outgoing?.Name ?? "", 1);
        }
        Add(events, packet, destination, "ICMP echo request", $"Echo request received from {sourceInterface.Ipv4Address}.", sourceInterface, destinationInterface, "", "", 3, 63);
        Add(events, packet, destination, "ICMP echo reply", $"Echo reply sent to {sourceInterface.Ipv4Address}.", destinationInterface, sourceInterface, "", "", 3, 64);
        for (var index = path.Count - 2; index >= 1; index--)
        {
            var device = path[index];
            var incoming = FindInterfaceOnPath(project, device, path[index + 1]);
            var outgoing = FindInterfaceOnPath(project, device, path[index - 1]);
            if (SpanningTreeEngine.IsBridge(device))
            {
                if (incoming is null || outgoing is null) return Failure($"No reply switch path through {device.Name}.");
                var decision = SwitchingEngine.ProcessFrame(device, incoming.Name, destinationInterface.MacAddress, sourceInterface.MacAddress, outgoing.Vlan);
                if (!decision.Accepted || !decision.EgressInterfaces.Contains(outgoing.Name)) return Failure($"{device.Name}: {decision.Detail}");
                Add(events, packet, device, "Reverse forwarding", $"Forwarded ICMP echo reply toward {source.Name}.",
                    destinationInterface, sourceInterface, incoming.Name, string.Join(",", decision.EgressInterfaces), outgoing.Vlan);
            }
            else if (device.Kind.Contains("Router", StringComparison.OrdinalIgnoreCase) || device.Kind.Contains("Layer 3", StringComparison.OrdinalIgnoreCase))
            {
                var route = reverseRoutes.GetValueOrDefault(device.Id);
                Add(events, packet, device, "Reverse routing lookup",
                    route is null ? $"Forwarded toward {sourceInterface.Ipv4Address}; TTL decremented." : $"Matched {route.Source} route {route.Network}/{route.PrefixLength} via {(string.IsNullOrWhiteSpace(route.NextHop) ? "direct" : route.NextHop)}; TTL decremented.",
                    destinationInterface, sourceInterface, incoming?.Name ?? "", outgoing?.Name ?? "", outgoing?.Vlan ?? sourceInterface.Vlan, 63);
            }
            else
                Add(events, packet, device, "Reverse forwarding", $"Forwarded ICMP echo reply toward {source.Name}.",
                    destinationInterface, sourceInterface, incoming?.Name ?? "", outgoing?.Name ?? "", outgoing?.Vlan ?? sourceInterface.Vlan);
        }
        Add(events, packet, source, "Ping success", $"Reply from {destinationAddress}: simulated round-trip completed.", destinationInterface, sourceInterface, "", "", 1, 64);
        return new PingResult(true, $"Reply from {destinationAddress}: bytes=32 time<1ms TTL=64 (simulated)", events);

        PingResult Failure(string message) => new(false, $"Request failed: {message}", events);
    }

    public IReadOnlyDictionary<string, string> GetMacTable(Guid deviceId) =>
        _project?.Devices.FirstOrDefault(device => device.Id == deviceId)?.LearnedMacAddresses
            .ToDictionary(entry => entry.Key, entry => $"VLAN {entry.Value.Vlan} {entry.Value.InterfaceName}") ?? new Dictionary<string, string>();

    public PingResult PingIpv6(NetworkProject project, DeviceModel source, string destinationAddress) =>
        Ipv6NetworkSimulator.Ping(project, source, destinationAddress);

    private static List<DeviceModel>? FindPath(NetworkProject project, DeviceModel source, DeviceModel destination)
    {
        var queue = new Queue<DeviceModel>();
        var previous = new Dictionary<Guid, Guid?> { [source.Id] = null };
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Id == destination.Id) break;
            foreach (var link in project.Links.Where(l => l.IsUp && (l.ADeviceId == current.Id || l.BDeviceId == current.Id)))
            {
                var from = current.Id == link.ADeviceId ? link.AInterface : link.BInterface;
                var toId = current.Id == link.ADeviceId ? link.BDeviceId : link.ADeviceId;
                var toName = current.Id == link.ADeviceId ? link.BInterface : link.AInterface;
                var fromInterface = current.Interfaces.FirstOrDefault(i => i.Name == from);
                var next = project.Devices.FirstOrDefault(d => d.Id == toId);
                var toInterface = next?.Interfaces.FirstOrDefault(i => i.Name == toName);
                if (fromInterface is null || !fromInterface.AdminUp || !fromInterface.LinkUp || next is null || toInterface is null || !toInterface.AdminUp || !toInterface.LinkUp || previous.ContainsKey(toId)) continue;
                if (fromInterface.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) || toInterface.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase)) continue;
                previous[toId] = current.Id;
                queue.Enqueue(next);
            }
        }
        if (!previous.ContainsKey(destination.Id)) return null;
        var path = new List<DeviceModel>();
        Guid? cursor = destination.Id;
        while (cursor is not null)
        {
            var device = project.Devices.First(d => d.Id == cursor.Value);
            path.Add(device);
            cursor = previous[cursor.Value];
        }
        path.Reverse();
        return path;
    }

    private static NetworkInterfaceModel? FindInterfaceOnPath(NetworkProject project, DeviceModel device, DeviceModel neighbor)
    {
        var link = project.Links.FirstOrDefault(l => l.IsUp &&
            ((l.ADeviceId == device.Id && l.BDeviceId == neighbor.Id) || (l.BDeviceId == device.Id && l.ADeviceId == neighbor.Id)));
        if (link is null) return null;
        var interfaceName = link.ADeviceId == device.Id ? link.AInterface : link.BInterface;
        return device.Interfaces.FirstOrDefault(i => i.Name == interfaceName);
    }

    private static void Add(List<SimulationEvent> events, string packet, DeviceModel device, string action, string detail,
        NetworkInterfaceModel source, NetworkInterfaceModel destination, string incoming = "", string outgoing = "", int vlan = 1, int ttl = 64,
        string protocol = "ICMP / Ethernet", string destinationMacOverride = "")
    {
        events.Add(new SimulationEvent(DateTime.Now, packet, device.Name, action, detail,
            source.Ipv4Address, destination.Ipv4Address, protocol, source.MacAddress,
            string.IsNullOrWhiteSpace(destinationMacOverride) ? destination.MacAddress : destinationMacOverride,
            incoming, outgoing, ttl, vlan));
    }
}