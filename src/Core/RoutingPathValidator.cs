namespace SiscoNet.Core;

public static class RoutingPathValidator
{
    public static string? Validate(NetworkProject project, IReadOnlyList<DeviceModel> path,
        DeviceModel source, DeviceModel destination,
        NetworkInterfaceModel sourceInterface, NetworkInterfaceModel destinationInterface,
        out IReadOnlyDictionary<Guid, RouteDecision> decisions)
    {
        var selectedRoutes = new Dictionary<Guid, RouteDecision>();
        decisions = selectedRoutes;
        if (IpAddressing.IsInSameSubnet(sourceInterface.Ipv4Address, destinationInterface.Ipv4Address, sourceInterface.SubnetMask))
            return null;

        var layer3Indices = Enumerable.Range(0, path.Count)
            .Where(index => IsLayer3(path[index]))
            .ToArray();
        if (layer3Indices.Length == 0) return "The path has no Layer 3 device to route between subnets.";

        var firstRouterIndex = layer3Indices[0];
        if (source.Id != path[firstRouterIndex].Id)
        {
            if (!IpAddressing.IsValidIpv4(source.DefaultGateway))
                return "Destination is outside the local subnet and no valid default gateway is configured.";
            var gatewayInterface = GetInterfaceToward(project, path[firstRouterIndex], path[firstRouterIndex - 1]);
            if (gatewayInterface is null || gatewayInterface.Ipv4Address != source.DefaultGateway ||
                !IpAddressing.IsInSameSubnet(sourceInterface.Ipv4Address, source.DefaultGateway, sourceInterface.SubnetMask))
                return $"Default gateway {source.DefaultGateway} is not reachable on the path to the first router.";
        }

        foreach (var index in layer3Indices.Where(index => index < path.Count - 1))
        {
            var router = path[index];
            var route = RouteEngine.Resolve(router, destinationInterface.Ipv4Address);
            if (route is null) return $"No route to {destinationInterface.Ipv4Address} on {router.Name}.";

            var outgoing = GetInterfaceToward(project, router, path[index + 1]);
            if (outgoing is null || !outgoing.Name.Equals(route.InterfaceName, StringComparison.OrdinalIgnoreCase))
                return $"{router.Name} selects {route.InterfaceName} for {destinationInterface.Ipv4Address}, but the active path leaves via {outgoing?.Name ?? "no interface"}.";

            if (route.Source != "connected")
            {
                var nextRouter = path.Skip(index + 1).Take(path.Count - index - 2)
                    .FirstOrDefault(device => IsLayer3(device) && device.Interfaces.Any(iface => iface.AdminUp && iface.Ipv4Address == route.NextHop));
                if (nextRouter is null)
                    return $"Next hop {route.NextHop} on {router.Name} is not a reachable downstream router on this path.";
            }
            else if (path.Skip(index + 1).Take(path.Count - index - 2).Any(IsLayer3))
                return $"{router.Name} has only a connected route to {destinationInterface.Ipv4Address}, but another router is required beyond its outgoing interface.";

            selectedRoutes[router.Id] = route;
        }

        return null;
    }

    public static bool IsLayer3(DeviceModel device) =>
        device.Kind.Contains("Router", StringComparison.OrdinalIgnoreCase) ||
        device.Kind.Contains("Layer 3", StringComparison.OrdinalIgnoreCase);

    private static NetworkInterfaceModel? GetInterfaceToward(NetworkProject project, DeviceModel device, DeviceModel neighbor)
    {
        var link = project.Links.FirstOrDefault(item => item.IsUp &&
            ((item.ADeviceId == device.Id && item.BDeviceId == neighbor.Id) || (item.BDeviceId == device.Id && item.ADeviceId == neighbor.Id)));
        if (link is null) return null;
        var interfaceName = link.ADeviceId == device.Id ? link.AInterface : link.BInterface;
        return device.Interfaces.FirstOrDefault(iface => iface.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase) && iface.AdminUp && iface.LinkUp);
    }
}