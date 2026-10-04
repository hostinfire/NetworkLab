using System.Net;

namespace SiscoNet.Core;

public sealed record Ipv6RouteDecision(string Prefix, int PrefixLength, string NextHop, string InterfaceName, string Source);

public static class Ipv6RouteEngine
{
    public static Ipv6RouteDecision? Resolve(DeviceModel device, string destination)
    {
        if (!IpAddressing.IsValidIpv6(destination)) return null;
        var connected = device.Interfaces.Where(iface => iface.AdminUp && iface.LinkUp && IpAddressing.IsValidIpv6(iface.Ipv6Address))
            .Where(iface => IpAddressing.IsInSameIpv6Prefix(destination, iface.Ipv6Address, iface.Ipv6PrefixLength))
            .Select(iface => new Ipv6RouteDecision(NetworkPrefix(iface.Ipv6Address, iface.Ipv6PrefixLength), iface.Ipv6PrefixLength, "", iface.Name, "connected"));
        var statics = device.StaticIpv6Routes.Where(route => IpAddressing.IsValidIpv6(route.Prefix) && IpAddressing.IsValidIpv6(route.NextHop) &&
                route.PrefixLength is >= 0 and <= 128 && IpAddressing.IsInSameIpv6Prefix(destination, route.Prefix, route.PrefixLength))
            .Select(route => new Ipv6RouteDecision(NetworkPrefix(route.Prefix, route.PrefixLength), route.PrefixLength, route.NextHop, route.InterfaceName, "static"));
        return connected.Concat(statics).OrderByDescending(route => route.PrefixLength).ThenBy(route => route.Source == "connected" ? 0 : 1).FirstOrDefault();
    }

    public static string NetworkPrefix(string address, int prefixLength)
    {
        if (!IPAddress.TryParse(address, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || prefixLength is < 0 or > 128) return "";
        var bytes = parsed.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainder = prefixLength % 8;
        if (remainder > 0) bytes[fullBytes] &= (byte)(0xff << (8 - remainder));
        for (var index = fullBytes + (remainder > 0 ? 1 : 0); index < bytes.Length; index++) bytes[index] = 0;
        return new IPAddress(bytes).ToString();
    }
}