namespace SiscoNet.Core;

public static class Ipv6NetworkSimulator
{
    public static PingResult Ping(NetworkProject project, DeviceModel source, string destinationAddress)
    {
        SpanningTreeEngine.Recalculate(project);
        var events = new List<SimulationEvent>();
        if (!IpAddressing.IsValidIpv6(destinationAddress)) return Failure("Invalid IPv6 destination.");
        var sourceInterface = source.Interfaces.FirstOrDefault(iface => iface.AdminUp && iface.LinkUp && IpAddressing.IsValidIpv6(iface.Ipv6Address));
        if (sourceInterface is null) return Failure("Source has no enabled IPv6 interface.");
        var destination = project.Devices.FirstOrDefault(device => device.Interfaces.Any(iface => iface.Ipv6Address.Equals(destinationAddress, StringComparison.OrdinalIgnoreCase)));
        if (destination is null) return Failure($"No device owns IPv6 address {destinationAddress}.");
        var destinationInterface = destination.Interfaces.First(iface => iface.Ipv6Address.Equals(destinationAddress, StringComparison.OrdinalIgnoreCase));
        if (!destinationInterface.AdminUp || !destinationInterface.LinkUp) return Failure("Destination IPv6 interface is down.");
        var path = FindPath(project, source, destination);
        if (path is null) return Failure("No active physical or wireless path connects the IPv6 endpoints.");

        for (var index = 0; index < path.Count - 1; index++)
        {
            var left = GetInterface(project, path[index], path[index + 1]);
            var right = GetInterface(project, path[index + 1], path[index]);
            if (left is null || right is null || !left.AdminUp || !right.AdminUp) return Failure("An interface on the IPv6 path is down.");
            var vlan = left.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase)
                ? right.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase) ? left.NativeVlan : right.Vlan : left.Vlan;
            if (!SwitchingEngine.LinkAllowsVlan(left, right, vlan)) return Failure($"VLAN {vlan} is not admitted on the IPv6 path.");
            if (left.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) || right.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase)) return Failure("STP blocks an IPv6 path link.");
        }

        var samePrefix = IpAddressing.IsInSameIpv6Prefix(sourceInterface.Ipv6Address, destinationAddress, sourceInterface.Ipv6PrefixLength);
        if (!samePrefix)
        {
            var firstRouterIndex = path.FindIndex(RoutingPathValidator.IsLayer3);
            if (firstRouterIndex < 1 || !IpAddressing.IsValidIpv6(source.DefaultIpv6Gateway)) return Failure("Remote IPv6 destination requires a configured reachable gateway.");
            var firstRouter = path[firstRouterIndex];
            var gateway = GetInterface(project, firstRouter, path[firstRouterIndex - 1]);
            if (gateway is null || !gateway.Ipv6Address.Equals(source.DefaultIpv6Gateway, StringComparison.OrdinalIgnoreCase))
                return Failure($"IPv6 gateway {source.DefaultIpv6Gateway} is not on the active path.");
            var error = ValidateRoutes(project, path, destinationAddress);
            if (error is not null) return Failure(error);
            error = ValidateRoutes(project, path.AsEnumerable().Reverse().ToArray(), sourceInterface.Ipv6Address);
            if (error is not null) return Failure($"Return path: {error}");
        }

        var packet = $"ICMPv6-{Guid.NewGuid():N}"[..13];
        Add(events, packet, source, "Neighbor solicitation", $"Resolve {(samePrefix ? destinationAddress : source.DefaultIpv6Gateway)} on-link address.",
            sourceInterface, destinationInterface, "ICMPv6 / IPv6", "33:33:FF:00:00:00", sourceInterface.Name, "", 1, 255);
        for (var index = 1; index < path.Count - 1; index++)
        {
            var device = path[index];
            var incoming = GetInterface(project, device, path[index - 1]);
            var outgoing = GetInterface(project, device, path[index + 1]);
            if (incoming is null || outgoing is null) return Failure($"No forwarding interface on {device.Name}.");
            if (RoutingPathValidator.IsLayer3(device))
                Add(events, packet, device, "IPv6 route lookup", $"Selected {Ipv6RouteEngine.Resolve(device, destinationAddress)?.Source} route for {destinationAddress}; hop limit decremented.",
                    sourceInterface, destinationInterface, "ICMPv6 / IPv6", destinationInterface.MacAddress, incoming.Name, outgoing.Name, 64);
            else if (device.Kind.Contains("Switch", StringComparison.OrdinalIgnoreCase) || device.Kind == "Hub")
            {
                var decision = SwitchingEngine.ProcessFrame(device, incoming.Name, sourceInterface.MacAddress, destinationInterface.MacAddress, incoming.Vlan, true);
                if (!decision.Accepted || !decision.EgressInterfaces.Contains(outgoing.Name)) return Failure(decision.Detail);
                Add(events, packet, device, "IPv6 frame forwarding", decision.Detail, sourceInterface, destinationInterface,
                    "ICMPv6 / IPv6", destinationInterface.MacAddress, incoming.Name, string.Join(",", decision.EgressInterfaces), incoming.Vlan);
            }
        }
        Add(events, packet, destination, "ICMPv6 echo request", $"Echo request received from {sourceInterface.Ipv6Address}.", sourceInterface, destinationInterface, "ICMPv6 / IPv6", destinationInterface.MacAddress, "", "", 64);
        Add(events, packet, destination, "ICMPv6 echo reply", $"Echo reply sent to {sourceInterface.Ipv6Address}.", destinationInterface, sourceInterface, "ICMPv6 / IPv6", sourceInterface.MacAddress, "", "", 64);
        for (var index = path.Count - 2; index >= 1; index--)
        {
            var device = path[index];
            var incoming = GetInterface(project, device, path[index + 1]);
            var outgoing = GetInterface(project, device, path[index - 1]);
            if (incoming is null || outgoing is null) return Failure($"No reverse forwarding interface on {device.Name}.");
            if (RoutingPathValidator.IsLayer3(device))
                Add(events, packet, device, "IPv6 return route", $"Selected return route for {sourceInterface.Ipv6Address}; hop limit decremented.",
                    destinationInterface, sourceInterface, "ICMPv6 / IPv6", sourceInterface.MacAddress, incoming.Name, outgoing.Name, 64);
            else if (device.Kind.Contains("Switch", StringComparison.OrdinalIgnoreCase) || device.Kind == "Hub")
            {
                var decision = SwitchingEngine.ProcessFrame(device, incoming.Name, destinationInterface.MacAddress, sourceInterface.MacAddress, outgoing.Vlan);
                if (!decision.Accepted || !decision.EgressInterfaces.Contains(outgoing.Name)) return Failure(decision.Detail);
                Add(events, packet, device, "IPv6 reply forwarding", decision.Detail, destinationInterface, sourceInterface,
                    "ICMPv6 / IPv6", sourceInterface.MacAddress, incoming.Name, string.Join(",", decision.EgressInterfaces), outgoing.Vlan);
            }
        }
        Add(events, packet, source, "IPv6 ping success", $"Reply from {destinationAddress}: simulated ICMPv6 response.", destinationInterface, sourceInterface, "ICMPv6 / IPv6", sourceInterface.MacAddress, "", "", 64);
        return new PingResult(true, $"Reply from {destinationAddress}: simulated ICMPv6 response.", events);

        PingResult Failure(string message) => new(false, $"Request failed: {message}", events);
    }

    private static string? ValidateRoutes(NetworkProject project, IReadOnlyList<DeviceModel> path, string destination)
    {
        for (var index = 0; index < path.Count - 1; index++)
        {
            var router = path[index];
            if (!RoutingPathValidator.IsLayer3(router)) continue;
            var route = Ipv6RouteEngine.Resolve(router, destination);
            var outgoing = GetInterface(project, router, path[index + 1]);
            if (route is null || outgoing is null || route.InterfaceName != outgoing.Name)
                return $"{router.Name} has no IPv6 route to {destination} through the active path.";
        }
        return null;
    }

    private static List<DeviceModel>? FindPath(NetworkProject project, DeviceModel source, DeviceModel destination)
    {
        var queue = new Queue<DeviceModel>();
        var previous = new Dictionary<Guid, Guid?> { [source.Id] = null };
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Id == destination.Id) break;
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
        if (!previous.ContainsKey(destination.Id)) return null;
        var path = new List<DeviceModel>();
        Guid? cursor = destination.Id;
        while (cursor is not null)
        {
            var device = project.Devices.First(item => item.Id == cursor.Value);
            path.Add(device);
            cursor = previous[cursor.Value];
        }
        path.Reverse();
        return path;
    }

    private static NetworkInterfaceModel? GetInterface(NetworkProject project, DeviceModel device, DeviceModel neighbor)
    {
        var link = project.Links.FirstOrDefault(item => item.IsUp &&
            ((item.ADeviceId == device.Id && item.BDeviceId == neighbor.Id) || (item.BDeviceId == device.Id && item.ADeviceId == neighbor.Id)));
        if (link is null) return null;
        return device.Interfaces.FirstOrDefault(iface => iface.Name == (link.ADeviceId == device.Id ? link.AInterface : link.BInterface));
    }

    private static void Add(List<SimulationEvent> events, string packet, DeviceModel device, string action, string detail,
        NetworkInterfaceModel source, NetworkInterfaceModel destination, string protocol, string destinationMac,
        string incoming, string outgoing, int vlan = 1, int hopLimit = 64) =>
        events.Add(new SimulationEvent(DateTime.Now, packet, device.Name, action, detail,
            source.Ipv6Address, destination.Ipv6Address, protocol, source.MacAddress, destinationMac,
            incoming, outgoing, hopLimit, vlan));
}