using System.Net;

namespace SiscoNet.Core;

public sealed class NetworkProject
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Untitled topology";
    public List<DeviceModel> Devices { get; set; } = [];
    public List<LinkModel> Links { get; set; } = [];
    public List<LabObjective> Objectives { get; set; } = [];
    public List<IotAutomationRule> IotRules { get; set; } = [];
    public List<CanvasAnnotation> Annotations { get; set; } = [];
}

public sealed class CanvasAnnotation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "Note";
    public double X { get; set; }
    public double Y { get; set; }
}

public sealed class DeviceModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "PC";
    public string Name { get; set; } = "PC1";
    public double X { get; set; } = 120;
    public double Y { get; set; } = 100;
    public string DefaultGateway { get; set; } = "";
    public string DefaultIpv6Gateway { get; set; } = "";
    public List<NetworkInterfaceModel> Interfaces { get; set; } = [];
    public List<StaticRouteModel> StaticRoutes { get; set; } = [];
    public List<DynamicRouteModel> DynamicRoutes { get; set; } = [];
    public DynamicRoutingConfiguration DynamicRouting { get; set; } = new();
    public List<StaticIpv6RouteModel> StaticIpv6Routes { get; set; } = [];
    public NetworkServices Services { get; set; } = new();
    public WirelessConfiguration Wireless { get; set; } = new();
    public List<AccessControlRule> AccessRules { get; set; } = [];
    public NatConfiguration Nat { get; set; } = new();
    public Dictionary<string, MacLearningEntry> LearnedMacAddresses { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Groups { get; set; } = [];
    public string Notes { get; set; } = "";
    public string DnsServerAddress { get; set; } = "";
    public Dictionary<string, double> SensorValues { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ActuatorEnabled { get; set; }
}

public sealed class DynamicRoutingConfiguration
{
    public bool RipEnabled { get; set; }
    public bool OspfEnabled { get; set; }
    public bool BgpEnabled { get; set; }
    public int AutonomousSystem { get; set; } = 65001;
    public int OspfArea { get; set; }
    public List<string> AdvertisedNetworks { get; set; } = [];
    public List<string> BgpNeighbors { get; set; } = [];
}

public sealed class DynamicRouteModel
{
    public string Network { get; set; } = "";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string NextHop { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public int Metric { get; set; } = 1;
    public string Protocol { get; set; } = "RIP";
    public List<int> AutonomousSystemPath { get; set; } = [];
}

public sealed class StaticIpv6RouteModel
{
    public string Prefix { get; set; } = "";
    public int PrefixLength { get; set; } = 64;
    public string NextHop { get; set; } = "";
    public string InterfaceName { get; set; } = "";
}

public sealed class NetworkServices
{
    public bool DhcpEnabled { get; set; }
    public bool DnsEnabled { get; set; }
    public bool HttpEnabled { get; set; }
    public bool HttpsEnabled { get; set; }
    public bool FtpEnabled { get; set; }
    public string DhcpPoolStart { get; set; } = "192.168.10.100";
    public string DhcpPoolEnd { get; set; } = "192.168.10.199";
    public string DhcpSubnetMask { get; set; } = "255.255.255.0";
    public string DhcpGateway { get; set; } = "";
    public string DhcpDnsServer { get; set; } = "";
    public string HttpContent { get; set; } = "Northstar Network Lab service is online.";
    public Dictionary<string, string> DnsRecords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> FtpFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class WirelessConfiguration
{
    public bool Enabled { get; set; }
    public string Ssid { get; set; } = "Northstar-Lab";
    public string SecurityMode { get; set; } = "WPA2-Personal";
    public string Passphrase { get; set; } = "";
    public int Channel { get; set; } = 6;
    public int RangeMeters { get; set; } = 30;
    public List<Guid> AssociatedClients { get; set; } = [];
}

public sealed class AccessControlRule
{
    public bool Permit { get; set; } = true;
    public string Protocol { get; set; } = "ip";
    public string SourceNetwork { get; set; } = "0.0.0.0";
    public string SourceMask { get; set; } = "0.0.0.0";
    public string DestinationNetwork { get; set; } = "0.0.0.0";
    public string DestinationMask { get; set; } = "0.0.0.0";
    public int? DestinationPort { get; set; }
}

public sealed class NatConfiguration
{
    public bool Enabled { get; set; }
    public string InsideInterface { get; set; } = "";
    public string OutsideInterface { get; set; } = "";
    public string InsideNetwork { get; set; } = "";
    public string InsideMask { get; set; } = "255.255.255.0";
    public string OutsideAddress { get; set; } = "";
}

public sealed class IotAutomationRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SensorDeviceId { get; set; }
    public string Property { get; set; } = "temperature";
    public string Comparison { get; set; } = ">";
    public double Threshold { get; set; } = 30;
    public Guid TargetDeviceId { get; set; }
    public string Action { get; set; } = "on";
    public bool Enabled { get; set; } = true;
    public bool LastTriggered { get; set; }
    public DateTime? LastTriggeredAt { get; set; }
}

public sealed class LabObjective
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "Configure endpoint addressing";
    public string Description { get; set; } = "Set the required IPv4 settings.";
    public string DeviceName { get; set; } = "PC1";
    public string InterfaceName { get; set; } = "eth0";
    public string ExpectedIpv4Address { get; set; } = "";
    public string ExpectedSubnetMask { get; set; } = "";
    public string ExpectedGateway { get; set; } = "";
    public int Points { get; set; } = 10;
    public string Hint { get; set; } = "";
}

public sealed class StaticRouteModel
{
    public string Network { get; set; } = "0.0.0.0";
    public string SubnetMask { get; set; } = "0.0.0.0";
    public string NextHop { get; set; } = "";
    public string InterfaceName { get; set; } = "";
    public int Metric { get; set; } = 1;
}

public sealed class NetworkInterfaceModel
{
    public string Name { get; set; } = "eth0";
    public string Medium { get; set; } = "Ethernet";
    public string MacAddress { get; set; } = "02:00:00:00:00:01";
    public string Ipv4Address { get; set; } = "";
    public string SubnetMask { get; set; } = "255.255.255.0";
    public string Ipv6Address { get; set; } = "";
    public int Ipv6PrefixLength { get; set; } = 64;
    public int Vlan { get; set; } = 1;
    public string PortMode { get; set; } = "access";
    public int NativeVlan { get; set; } = 1;
    public string AllowedVlans { get; set; } = "1-4094";
    public bool PortSecurityEnabled { get; set; }
    public int MaximumMacAddresses { get; set; } = 1;
    public string StpState { get; set; } = "forwarding";
    public bool StpAutomatic { get; set; } = true;
    public string BundleGroup { get; set; } = "";
    public bool AdminUp { get; set; } = true;
    public bool LinkUp { get; set; }
    public int SpeedMbps { get; set; } = 1000;
    public string Duplex { get; set; } = "Full";
}

public sealed class LinkModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ADeviceId { get; set; }
    public string AInterface { get; set; } = "eth0";
    public Guid BDeviceId { get; set; }
    public string BInterface { get; set; } = "eth0";
    public bool IsUp { get; set; } = true;
    public string CableType { get; set; } = "Ethernet straight-through";
}

public sealed record SimulationEvent(
    DateTime Time,
    string Packet,
    string Device,
    string Action,
    string Detail,
    string SourceIp,
    string DestinationIp,
    string Protocol,
    string SourceMac,
    string DestinationMac,
    string IncomingInterface,
    string OutgoingInterface,
    int Ttl,
    int Vlan,
    int SourcePort = 0,
    int DestinationPort = 0,
    string Transport = "",
    string ApplicationData = "");

public sealed record PingResult(bool Success, string Summary, IReadOnlyList<SimulationEvent> Events);

public static class DeviceCatalog
{
    public static readonly string[] Kinds =
    [
        "PC", "Laptop", "Server", "Smartphone", "Tablet", "Printer", "IP Phone",
        "Hub", "Switch", "Layer 3 Switch", "Router", "Wireless Router", "Access Point",
        "Firewall", "Modem", "Network Device", "Smart Light", "Fan", "Door",
        "Motion Sensor", "Temperature Sensor", "Camera", "Smart Appliance", "IoT Gateway",
        "Microcontroller", "Single-board Computer"
    ];

    public static DeviceModel Create(string kind, int index, double x, double y)
    {
        var networkName = kind.Replace(" ", "");
        var portCount = kind switch
        {
            "Switch" or "Layer 3 Switch" or "Hub" => 8,
            "Router" or "Firewall" or "Wireless Router" => 4,
            "Server" => 2,
            _ => 1
        };
        var prefix = kind switch
        {
            "Switch" => "SW", "Layer 3 Switch" => "L3SW", "Router" => "R",
            "Server" => "SRV", "Access Point" => "AP", "Firewall" => "FW",
            _ => networkName.ToUpperInvariant()
        };
        var device = new DeviceModel
        {
            Kind = kind,
            Name = $"{prefix}{index}",
            X = x,
            Y = y
        };
        for (var port = 0; port < portCount; port++)
        {
            var number = Interlocked.Increment(ref _macCounter);
            device.Interfaces.Add(new NetworkInterfaceModel
            {
                Name = portCount == 1 ? "eth0" : $"ge0/{port + 1}",
                MacAddress = $"02:4E:53:{(number >> 16) & 255:X2}:{(number >> 8) & 255:X2}:{number & 255:X2}"
            });
        }
        if (kind is "Laptop" or "Smartphone" or "Tablet" or "Wireless Router" or "Access Point")
        {
            var number = Interlocked.Increment(ref _macCounter);
            device.Interfaces.Add(new NetworkInterfaceModel
            {
                Name = "wlan0",
                Medium = "Wireless",
                MacAddress = $"02:4E:53:{(number >> 16) & 255:X2}:{(number >> 8) & 255:X2}:{number & 255:X2}"
            });
        }
        return device;
    }

    private static int _macCounter = 1;
}

public static class IpAddressing
{
    public static bool IsValidIpv4(string address) => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;

    public static bool IsValidSubnetMask(string mask)
    {
        if (!IPAddress.TryParse(mask, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var bits = string.Concat(ip.GetAddressBytes().Select(value => Convert.ToString(value, 2).PadLeft(8, '0')));
        return !bits.Contains("01", StringComparison.Ordinal);
    }

    public static bool IsValidIpv6(string address) => IPAddress.TryParse(address, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;

    public static bool IsInSameIpv6Prefix(string first, string second, int prefixLength)
    {
        if (!IPAddress.TryParse(first, out var firstIp) || !IPAddress.TryParse(second, out var secondIp) ||
            firstIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 || secondIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6 ||
            prefixLength is < 0 or > 128) return false;
        var firstBytes = firstIp.GetAddressBytes();
        var secondBytes = secondIp.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;
        if (!firstBytes.Take(fullBytes).SequenceEqual(secondBytes.Take(fullBytes))) return false;
        if (remainingBits == 0) return true;
        var mask = (byte)(0xFF << (8 - remainingBits));
        return (firstBytes[fullBytes] & mask) == (secondBytes[fullBytes] & mask);
    }

    public static int GetPrefixLength(string mask)
    {
        if (!IsValidSubnetMask(mask)) return -1;
        return IPAddress.Parse(mask).GetAddressBytes().Sum(value => System.Numerics.BitOperations.PopCount((uint)value));
    }

    public static string GetNetworkAddress(string address, string mask)
    {
        if (!IPAddress.TryParse(address, out var parsedAddress) || !IPAddress.TryParse(mask, out var parsedMask) ||
            parsedAddress.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || parsedMask.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return "";
        var addressBytes = parsedAddress.GetAddressBytes();
        var maskBytes = parsedMask.GetAddressBytes();
        return new IPAddress(addressBytes.Zip(maskBytes, (addressByte, maskByte) => (byte)(addressByte & maskByte)).ToArray()).ToString();
    }

    public static bool IsInSameSubnet(string first, string second, string mask)
    {
        if (!IPAddress.TryParse(first, out var firstIp) || !IPAddress.TryParse(second, out var secondIp) ||
            !IPAddress.TryParse(mask, out var maskIp) || firstIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
            secondIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork || maskIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            return false;

        var a = firstIp.GetAddressBytes();
        var b = secondIp.GetAddressBytes();
        var m = maskIp.GetAddressBytes();
        return Enumerable.Range(0, 4).All(i => (a[i] & m[i]) == (b[i] & m[i]));
    }
}

public sealed class MacLearningEntry
{
    public string InterfaceName { get; set; } = "";
    public int Vlan { get; set; } = 1;
    public DateTime LearnedAt { get; set; } = DateTime.UtcNow;
}