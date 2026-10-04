namespace SiscoNet.Core;

public sealed record RouteDecision(
    string Network,
    string SubnetMask,
    string NextHop,
    string InterfaceName,
    int Metric,
    string Source,
    int PrefixLength);

public static class RouteEngine
{
    public static IReadOnlyList<RouteDecision> GetTable(DeviceModel device)
    {
        var routes = new List<RouteDecision>();
        foreach (var iface in device.Interfaces.Where(i => i.AdminUp && i.LinkUp && IpAddressing.IsValidIpv4(i.Ipv4Address) && IpAddressing.IsValidSubnetMask(i.SubnetMask)))
        {
            routes.Add(new RouteDecision(
                IpAddressing.GetNetworkAddress(iface.Ipv4Address, iface.SubnetMask), iface.SubnetMask,
                "", iface.Name, 0, "connected", IpAddressing.GetPrefixLength(iface.SubnetMask)));
        }
        routes.AddRange(device.StaticRoutes
            .Where(route => IpAddressing.IsValidIpv4(route.Network) && IpAddressing.IsValidSubnetMask(route.SubnetMask) && IpAddressing.IsValidIpv4(route.NextHop))
            .Select(route => new RouteDecision(
                IpAddressing.GetNetworkAddress(route.Network, route.SubnetMask), route.SubnetMask,
                route.NextHop, route.InterfaceName, route.Metric, "static", IpAddressing.GetPrefixLength(route.SubnetMask))));
        routes.AddRange(device.DynamicRoutes
            .Where(route => IpAddressing.IsValidIpv4(route.Network) && IpAddressing.IsValidSubnetMask(route.SubnetMask) && IpAddressing.IsValidIpv4(route.NextHop))
            .Select(route => new RouteDecision(
                IpAddressing.GetNetworkAddress(route.Network, route.SubnetMask), route.SubnetMask,
                route.NextHop, route.InterfaceName, route.Metric, route.Protocol, IpAddressing.GetPrefixLength(route.SubnetMask))));
        return routes.OrderByDescending(route => route.PrefixLength)
            .ThenBy(route => route.Source == "connected" ? 0 : route.Source == "static" ? 1 : route.Source == "OSPF" ? 2 : route.Source == "RIP" ? 3 : 4)
            .ThenBy(route => route.Metric).ToArray();
    }

    public static RouteDecision? Resolve(DeviceModel device, string destinationAddress)
    {
        if (!IpAddressing.IsValidIpv4(destinationAddress)) return null;
        var route = GetTable(device).FirstOrDefault(candidate => IpAddressing.IsInSameSubnet(destinationAddress, candidate.Network, candidate.SubnetMask));
        if (route is null || route.Source != "static" || !string.IsNullOrWhiteSpace(route.InterfaceName)) return route;
        var connected = GetTable(device).FirstOrDefault(candidate => candidate.Source == "connected" &&
            IpAddressing.IsInSameSubnet(route.NextHop, candidate.Network, candidate.SubnetMask));
        return connected is null ? route : route with { InterfaceName = connected.InterfaceName };
    }

    public static bool TryCreateStaticRoute(string network, string mask, string nextHop, string interfaceName, out StaticRouteModel route)
    {
        route = new StaticRouteModel();
        if (!IpAddressing.IsValidIpv4(network) || !IpAddressing.IsValidSubnetMask(mask) || !IpAddressing.IsValidIpv4(nextHop)) return false;
        route = new StaticRouteModel
        {
            Network = IpAddressing.GetNetworkAddress(network, mask),
            SubnetMask = mask,
            NextHop = nextHop,
            InterfaceName = interfaceName,
            Metric = 1
        };
        return true;
    }
}