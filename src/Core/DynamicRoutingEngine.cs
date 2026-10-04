namespace SiscoNet.Core;

public static class DynamicRoutingEngine
{
    public static void Recalculate(NetworkProject project)
    {
        var routers = project.Devices.Where(RoutingPathValidator.IsLayer3).ToArray();
        foreach (var router in routers) router.DynamicRoutes.Clear();
        var neighbors = routers.ToDictionary(router => router.Id, router => GetDirectNeighbors(project, router, routers));

        foreach (var source in routers)
        {
            var paths = FindRouterPaths(project, source, neighbors);
            foreach (var target in routers.Where(router => router.Id != source.Id))
            {
                if (!paths.TryGetValue(target.Id, out var path) || path.Nodes.Count < 2) continue;
                var firstHop = path.Nodes[1];
                var sourceInterface = GetInterfaceBetween(project, source, firstHop);
                var nextHop = GetInterfaceBetween(project, firstHop, source)?.Ipv4Address ?? "";
                if (sourceInterface is null || !IpAddressing.IsValidIpv4(nextHop)) continue;

                var commonPath = path.Nodes.All(router => router.DynamicRouting.RipEnabled);
                if (source.DynamicRouting.RipEnabled && commonPath && path.Cost <= 15)
                    AddAdvertisedNetworks(source, target, nextHop, sourceInterface.Name, path.Cost, "RIP");

                commonPath = path.Nodes.All(router => router.DynamicRouting.OspfEnabled);
                if (source.DynamicRouting.OspfEnabled && commonPath)
                    AddAdvertisedNetworks(source, target, nextHop, sourceInterface.Name, path.Cost, "OSPF");
            }

            if (!source.DynamicRouting.BgpEnabled) continue;
            foreach (var neighbor in neighbors[source.Id])
            {
                var neighborIp = GetInterfaceBetween(project, neighbor, source)?.Ipv4Address ?? "";
                if (!neighbor.DynamicRouting.BgpEnabled || !source.DynamicRouting.BgpNeighbors.Contains(neighborIp, StringComparer.OrdinalIgnoreCase)) continue;
                if (!GetAdvertisedNetworks(neighbor).Any()) continue;
                var localInterface = GetInterfaceBetween(project, source, neighbor);
                var learnedInterface = GetInterfaceBetween(project, neighbor, source);
                if (localInterface is null || learnedInterface is null) continue;
                foreach (var network in GetAdvertisedNetworks(neighbor))
                {
                    source.DynamicRoutes.Add(new DynamicRouteModel
                    {
                        Network = network.Network,
                        SubnetMask = network.Mask,
                        NextHop = learnedInterface.Ipv4Address,
                        InterfaceName = localInterface.Name,
                        Metric = 1,
                        Protocol = "BGP",
                        AutonomousSystemPath = [neighbor.DynamicRouting.AutonomousSystem]
                    });
                }
            }
        }
    }

    private static void AddAdvertisedNetworks(DeviceModel source, DeviceModel target, string nextHop,
        string interfaceName, int metric, string protocol)
    {
        foreach (var network in GetAdvertisedNetworks(target))
            if (!source.Interfaces.Any(iface => IpAddressing.GetNetworkAddress(iface.Ipv4Address, iface.SubnetMask) == network.Network && iface.SubnetMask == network.Mask))
                source.DynamicRoutes.Add(new DynamicRouteModel
                {
                    Network = network.Network,
                    SubnetMask = network.Mask,
                    NextHop = nextHop,
                    InterfaceName = interfaceName,
                    Metric = metric,
                    Protocol = protocol
                });
    }

    private static IEnumerable<(string Network, string Mask)> GetAdvertisedNetworks(DeviceModel router)
    {
        var connected = router.Interfaces.Where(iface => iface.AdminUp && iface.LinkUp &&
                IpAddressing.IsValidIpv4(iface.Ipv4Address) && IpAddressing.IsValidSubnetMask(iface.SubnetMask))
            .Select(iface => (Network: IpAddressing.GetNetworkAddress(iface.Ipv4Address, iface.SubnetMask), Mask: iface.SubnetMask));
        if (router.DynamicRouting.AdvertisedNetworks.Count == 0) return connected.Distinct();
        return connected.Where(network => router.DynamicRouting.AdvertisedNetworks.Any(advertisement =>
            advertisement.Equals(network.Network, StringComparison.OrdinalIgnoreCase) ||
            advertisement.Equals($"{network.Network}/{IpAddressing.GetPrefixLength(network.Mask)}", StringComparison.OrdinalIgnoreCase))).Distinct();
    }

    private static Dictionary<Guid, RouterPath> FindRouterPaths(NetworkProject project, DeviceModel source,
        IReadOnlyDictionary<Guid, List<DeviceModel>> neighbors)
    {
        var result = new Dictionary<Guid, RouterPath> { [source.Id] = new RouterPath([source], 0) };
        var queue = new Queue<DeviceModel>();
        queue.Enqueue(source);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var currentPath = result[current.Id];
            foreach (var next in neighbors[current.Id])
            {
                if (result.ContainsKey(next.Id)) continue;
                result[next.Id] = new RouterPath([.. currentPath.Nodes, next], currentPath.Cost + 1);
                queue.Enqueue(next);
            }
        }
        return result;
    }

    private static List<DeviceModel> GetDirectNeighbors(NetworkProject project, DeviceModel router, IReadOnlyList<DeviceModel> routers)
    {
        var result = new List<DeviceModel>();
        foreach (var link in project.Links.Where(link => link.IsUp && (link.ADeviceId == router.Id || link.BDeviceId == router.Id)))
        {
            var neighborId = link.ADeviceId == router.Id ? link.BDeviceId : link.ADeviceId;
            var neighbor = routers.FirstOrDefault(candidate => candidate.Id == neighborId);
            if (neighbor is null || GetInterfaceBetween(project, router, neighbor) is not { AdminUp: true, LinkUp: true } ||
                GetInterfaceBetween(project, neighbor, router) is not { AdminUp: true, LinkUp: true }) continue;
            if (!result.Any(candidate => candidate.Id == neighbor.Id)) result.Add(neighbor);
        }
        return result;
    }

    private static NetworkInterfaceModel? GetInterfaceBetween(NetworkProject project, DeviceModel device, DeviceModel neighbor)
    {
        var link = project.Links.FirstOrDefault(item => item.IsUp &&
            ((item.ADeviceId == device.Id && item.BDeviceId == neighbor.Id) || (item.BDeviceId == device.Id && item.ADeviceId == neighbor.Id)));
        if (link is null) return null;
        var name = link.ADeviceId == device.Id ? link.AInterface : link.BInterface;
        return device.Interfaces.FirstOrDefault(iface => iface.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record RouterPath(IReadOnlyList<DeviceModel> Nodes, int Cost);
}