namespace SiscoNet.Core;

public sealed class CliSession(NetworkProject project, NetworkSimulator simulator, NetworkServiceEngine? services = null)
{
    private readonly NetworkServiceEngine _services = services ?? new NetworkServiceEngine(project);
    private DeviceModel? _device;
    private NetworkInterfaceModel? _interface;
    private bool _privileged;
    private bool _configuring;
    private string _routingProtocol = "";

    public string Prompt => _device is null ? "lab> " :
        _configuring ? (_interface is null ? $"{_device.Name}(config)# " : $"{_device.Name}({_interface.Name})# ") :
        _privileged ? $"{_device.Name}# " : $"{_device.Name}> ";

    public void SelectDevice(DeviceModel? device)
    {
        _device = device;
        _interface = null;
        _privileged = false;
        _configuring = false;
        _routingProtocol = "";
    }

    public string Execute(string input, Action<PingResult>? onPing = null, Action<ServiceResult>? onService = null)
    {
        var command = input.Trim();
        if (command.Length == 0) return "";
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var lower = command.ToLowerInvariant();
        if (_device is null) return "Select a device in the topology before entering device commands.";

        if (lower == "enable") { _privileged = true; return ""; }
        if (lower is "configure terminal" or "conf t")
        {
            if (!_privileged) return "% Enter privileged mode first.";
            _configuring = true;
            return "Configuration mode entered.";
        }
        if (lower is "exit" or "end") { _interface = null; _configuring = false; return ""; }
        if (lower.StartsWith("router ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Enter configuration mode first.";
            _routingProtocol = parts.Length >= 2 ? parts[1].ToLowerInvariant() : "";
            if (_routingProtocol == "rip") _device.DynamicRouting.RipEnabled = true;
            else if (_routingProtocol == "ospf") _device.DynamicRouting.OspfEnabled = true;
            else if (_routingProtocol == "bgp")
            {
                _device.DynamicRouting.BgpEnabled = true;
                if (parts.Length >= 3 && int.TryParse(parts[2], out var autonomousSystem) && autonomousSystem > 0)
                    _device.DynamicRouting.AutonomousSystem = autonomousSystem;
            }
            else return "% Usage: router rip | router ospf | router bgp <autonomous-system>";
            return $"{_routingProtocol.ToUpperInvariant()} configuration entered.";
        }
        if (lower.StartsWith("network ", StringComparison.Ordinal))
        {
            if (!_configuring || _routingProtocol is not ("rip" or "ospf" or "bgp")) return "% Enter a routing-protocol configuration context first.";
            var network = parts.Length > 1 ? parts[1] : "";
            if (!IpAddressing.IsValidIpv4(network)) return "% Usage: network <IPv4-network>";
            if (!_device.DynamicRouting.AdvertisedNetworks.Contains(network, StringComparer.OrdinalIgnoreCase))
                _device.DynamicRouting.AdvertisedNetworks.Add(network);
            if (_routingProtocol == "ospf" && parts.Length >= 4 && parts[2].Equals("area", StringComparison.OrdinalIgnoreCase) && int.TryParse(parts[3], out var area))
                _device.DynamicRouting.OspfArea = area;
            return $"Advertising {network} into {_routingProtocol.ToUpperInvariant()}.";
        }
        if (lower.StartsWith("neighbor ", StringComparison.Ordinal))
        {
            if (_routingProtocol != "bgp") return "% Neighbor commands are available in BGP configuration mode.";
            if (parts.Length < 2 || !IpAddressing.IsValidIpv4(parts[1])) return "% Usage: neighbor <IPv4-address> [remote-as <AS-number>]";
            if (!_device.DynamicRouting.BgpNeighbors.Contains(parts[1], StringComparer.OrdinalIgnoreCase))
                _device.DynamicRouting.BgpNeighbors.Add(parts[1]);
            return $"BGP neighbor {parts[1]} added. (Directly-connected educational peering.)";
        }
        if (lower.StartsWith("hostname ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Hostname can only be changed in configuration mode.";
            _device.Name = command[9..].Trim();
            return $"Hostname set to {_device.Name}.";
        }
        if (lower.StartsWith("interface ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Enter configuration mode first.";
            var name = command[10..].Trim();
            _interface = _device.Interfaces.FirstOrDefault(i => i.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return _interface is null ? $"% Interface {name} not found." : $"Interface {_interface.Name} selected.";
        }
        if (lower.StartsWith("ip address ", StringComparison.Ordinal))
        {
            if (!_configuring || _interface is null) return "% Select an interface in configuration mode first.";
            if (parts.Length != 4 || !IpAddressing.IsValidIpv4(parts[2]) || !IpAddressing.IsValidSubnetMask(parts[3]))
                return "% Usage: ip address <IPv4-address> <subnet-mask>";
            _interface.Ipv4Address = parts[2];
            _interface.SubnetMask = parts[3];
            return $"IPv4 address configured on {_interface.Name}.";
        }
        if (lower.StartsWith("ipv6 address ", StringComparison.Ordinal))
        {
            if (!_configuring || _interface is null) return "% Select an interface in configuration mode first.";
            var addressParts = parts.Length == 3 ? parts[2].Split('/', StringSplitOptions.TrimEntries) : [];
            if (addressParts.Length != 2 || !IpAddressing.IsValidIpv6(addressParts[0]) || !int.TryParse(addressParts[1], out var prefix) || prefix is < 0 or > 128)
                return "% Usage: ipv6 address <IPv6-address>/<prefix-length>";
            _interface.Ipv6Address = addressParts[0];
            _interface.Ipv6PrefixLength = prefix;
            return $"IPv6 address configured on {_interface.Name}.";
        }
        if (lower.StartsWith("ipv6 route ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Enter configuration mode first.";
            if (parts.Length is < 4 or > 5) return "% Usage: ipv6 route <prefix>/<length> <next-hop> <interface>";
            var prefixParts = parts[2].Split('/', StringSplitOptions.TrimEntries);
            if (prefixParts.Length != 2 || !IpAddressing.IsValidIpv6(prefixParts[0]) || !IpAddressing.IsValidIpv6(parts[3]) ||
                !int.TryParse(prefixParts[1], out var prefixLength) || prefixLength is < 0 or > 128)
                return "% Invalid IPv6 route prefix or next-hop address.";
            var interfaceName = parts.Length == 5 ? parts[4] : _device.Interfaces.FirstOrDefault(iface => iface.AdminUp && iface.LinkUp &&
                IpAddressing.IsValidIpv6(iface.Ipv6Address) && IpAddressing.IsInSameIpv6Prefix(parts[3], iface.Ipv6Address, iface.Ipv6PrefixLength))?.Name ?? "";
            if (interfaceName.Length == 0 || !_device.Interfaces.Any(iface => iface.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase)))
                return "% Next hop must resolve through a configured interface.";
            _device.StaticIpv6Routes.RemoveAll(route => route.PrefixLength == prefixLength && Ipv6RouteEngine.NetworkPrefix(route.Prefix, route.PrefixLength) == Ipv6RouteEngine.NetworkPrefix(prefixParts[0], prefixLength));
            _device.StaticIpv6Routes.Add(new StaticIpv6RouteModel { Prefix = prefixParts[0], PrefixLength = prefixLength, NextHop = parts[3], InterfaceName = interfaceName });
            return $"IPv6 static route {Ipv6RouteEngine.NetworkPrefix(prefixParts[0], prefixLength)}/{prefixLength} configured.";
        }
        if (lower.StartsWith("ip default-gateway ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Enter configuration mode first.";
            if (parts.Length != 3 || !IpAddressing.IsValidIpv4(parts[2])) return "% Usage: ip default-gateway <IPv4-address>";
            _device.DefaultGateway = parts[2];
            return $"Default gateway set to {_device.DefaultGateway}.";
        }
        if (lower.StartsWith("ip route ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Enter configuration mode first.";
            if (parts.Length is < 5 or > 6) return "% Usage: ip route <network> <mask> <next-hop> [interface]";
            var interfaceName = parts.Length == 6 ? parts[5] : "";
            if (interfaceName.Length > 0 && !_device.Interfaces.Any(i => i.Name.Equals(interfaceName, StringComparison.OrdinalIgnoreCase)))
                return $"% Interface {interfaceName} not found.";
            if (!RouteEngine.TryCreateStaticRoute(parts[2], parts[3], parts[4], interfaceName, out var route))
                return "% Invalid route. Check the destination network, contiguous subnet mask, and next-hop IPv4 address.";
            _device.StaticRoutes.RemoveAll(existing => existing.Network == route.Network && existing.SubnetMask == route.SubnetMask);
            _device.StaticRoutes.Add(route);
            return $"Static route {route.Network}/{IpAddressing.GetPrefixLength(route.SubnetMask)} via {route.NextHop} configured.";
        }
        if (lower.StartsWith("no ip route ", StringComparison.Ordinal))
        {
            if (!_configuring) return "% Enter configuration mode first.";
            if (parts.Length < 5) return "% Usage: no ip route <network> <mask> <next-hop>";
            if (!IpAddressing.IsValidIpv4(parts[2]) || !IpAddressing.IsValidSubnetMask(parts[3])) return "% Invalid destination network or subnet mask.";
            var network = IpAddressing.GetNetworkAddress(parts[2], parts[3]);
            var removed = _device.StaticRoutes.RemoveAll(route => route.Network == network && route.SubnetMask == parts[3]);
            return removed == 0 ? "% Matching static route not found." : $"Removed static route {network}/{IpAddressing.GetPrefixLength(parts[3])}.";
        }
        if (lower == "shutdown" || lower == "no shutdown")
        {
            if (!_configuring || _interface is null) return "% Select an interface in configuration mode first.";
            _interface.AdminUp = lower == "no shutdown";
            return $"Interface {_interface.Name} is {(_interface.AdminUp ? "enabled" : "disabled")}.";
        }
        if (lower.StartsWith("ping ", StringComparison.Ordinal))
        {
            if (!IpAddressing.IsValidIpv4(parts.Last())) return "% Invalid IPv4 destination.";
            var result = simulator.Ping(project, _device, parts.Last());
            onPing?.Invoke(result);
            return result.Summary;
        }
        if (lower.StartsWith("ping ipv6 ", StringComparison.Ordinal))
        {
            if (!IpAddressing.IsValidIpv6(parts.Last())) return "% Invalid IPv6 destination.";
            var result = simulator.PingIpv6(project, _device, parts.Last());
            onPing?.Invoke(result);
            return result.Summary;
        }
        if (lower == "dhcp")
        {
            var result = _services.RequestDhcp(_device);
            onService?.Invoke(result);
            return result.Summary;
        }
        if (lower.StartsWith("nslookup ", StringComparison.Ordinal))
        {
            var result = _services.ResolveDns(_device, parts[^1]);
            onService?.Invoke(result);
            return result.Summary;
        }
        if (lower.StartsWith("http get ", StringComparison.Ordinal) || lower.StartsWith("https get ", StringComparison.Ordinal))
        {
            var secure = lower.StartsWith("https ", StringComparison.Ordinal);
            var result = _services.RequestHttp(_device, parts[^1], secure);
            onService?.Invoke(result);
            return result.Summary;
        }
        if (lower.StartsWith("ftp list ", StringComparison.Ordinal))
        {
            var result = _services.RequestFtp(_device, parts[^1]);
            onService?.Invoke(result);
            return result.Summary;
        }
        if (lower.StartsWith("ftp get ", StringComparison.Ordinal) && parts.Length >= 4)
        {
            var result = _services.RequestFtp(_device, parts[2], "GET", string.Join(" ", parts.Skip(3)));
            onService?.Invoke(result);
            return result.Summary;
        }
        if ((lower.StartsWith("tcp send ", StringComparison.Ordinal) || lower.StartsWith("udp send ", StringComparison.Ordinal)) && parts.Length >= 5)
        {
            if (!int.TryParse(parts[3], out var destinationPort)) return "% Destination port must be an integer.";
            var payload = string.Join(" ", parts.Skip(4));
            var result = lower.StartsWith("tcp ", StringComparison.Ordinal)
                ? _services.SendTcp(_device, parts[2], destinationPort, payload)
                : _services.SendUdp(_device, parts[2], destinationPort, payload);
            onService?.Invoke(result);
            return result.Summary;
        }
        if (lower is "show interfaces" or "show ip interface")
            return string.Join(Environment.NewLine, _device.Interfaces.Select(i =>
                $"{i.Name,-10} {(i.AdminUp && i.LinkUp ? "up/up" : "down/down"),-10} IPv4 {Display(i.Ipv4Address)}/{Display(i.SubnetMask)} VLAN {i.Vlan}"));
        if (lower == "show running-config")
            return FormatRunningConfig();
        if (lower == "show mac-address-table")
        {
            var table = _device.LearnedMacAddresses;
            return table.Count == 0 ? "No learned entries." : string.Join(Environment.NewLine,
                table.Select(entry => $"VLAN {entry.Value.Vlan}  {entry.Key}  {entry.Value.InterfaceName}"));
        }
        if (lower == "show ip route")
        {
            DynamicRoutingEngine.Recalculate(project);
            var routes = RouteEngine.GetTable(_device)
                .Select(route => $"{(route.Source == "connected" ? "C" : route.Source == "static" ? "S" : route.Source[0])}  {route.Network}/{route.PrefixLength} {(string.IsNullOrWhiteSpace(route.NextHop) ? "direct" : $"via {route.NextHop}")}  {route.InterfaceName} [metric {route.Metric}]").ToList();
            if (!string.IsNullOrWhiteSpace(_device.DefaultGateway)) routes.Add($"S* 0.0.0.0/0 via {_device.DefaultGateway} (host default gateway)");
            return routes.Count == 0 ? "No routes configured." : string.Join(Environment.NewLine, routes);
        }
        if (lower == "show ipv6 route")
        {
            var routes = _device.Interfaces.Where(iface => iface.AdminUp && iface.LinkUp && IpAddressing.IsValidIpv6(iface.Ipv6Address))
                .Select(iface => $"C  {Ipv6RouteEngine.NetworkPrefix(iface.Ipv6Address, iface.Ipv6PrefixLength)}/{iface.Ipv6PrefixLength} via {iface.Name}")
                .Concat(_device.StaticIpv6Routes.Select(route => $"S  {Ipv6RouteEngine.NetworkPrefix(route.Prefix, route.PrefixLength)}/{route.PrefixLength} via {route.NextHop} {route.InterfaceName}")).ToArray();
            return routes.Length == 0 ? "No IPv6 routes configured." : string.Join(Environment.NewLine, routes);
        }
        if (lower is "show arp" or "arp -a") return "ARP entries are generated during simulated traffic; run ping to populate the event log.";
        if (lower == "show vlan") return string.Join(Environment.NewLine, _device.Interfaces.Select(i => $"VLAN {i.Vlan}  {i.Name}"));
        if (lower == "show neighbors")
            return string.Join(Environment.NewLine, project.Links.Where(l => l.ADeviceId == _device.Id || l.BDeviceId == _device.Id)
                .Select(l => project.Devices.FirstOrDefault(d => d.Id == (l.ADeviceId == _device.Id ? l.BDeviceId : l.ADeviceId))?.Name ?? "(missing device)"));

        return $"% Invalid command: {command}. Try enable, configure terminal, interface, show, or ping.";
    }

    private static string Display(string value) => string.IsNullOrWhiteSpace(value) ? "unassigned" : value;

    private string FormatRunningConfig()
    {
        var lines = new List<string> { $"hostname {_device!.Name}" };
        foreach (var iface in _device.Interfaces)
        {
            lines.Add($"interface {iface.Name}");
            lines.Add(string.IsNullOrWhiteSpace(iface.Ipv4Address) ? " no ip address" : $" ip address {iface.Ipv4Address} {iface.SubnetMask}");
            lines.Add(string.IsNullOrWhiteSpace(iface.Ipv6Address) ? " no ipv6 address" : $" ipv6 address {iface.Ipv6Address}/{iface.Ipv6PrefixLength}");
            lines.Add($" switchport mode {iface.PortMode}");
            lines.Add($" switchport vlan {iface.Vlan}");
            if (!iface.AdminUp) lines.Add(" shutdown");
        }
        lines.AddRange(_device.StaticRoutes.Select(route => $"ip route {route.Network} {route.SubnetMask} {route.NextHop}{(string.IsNullOrWhiteSpace(route.InterfaceName) ? "" : $" {route.InterfaceName}")}"));
        lines.AddRange(_device.StaticIpv6Routes.Select(route => $"ipv6 route {Ipv6RouteEngine.NetworkPrefix(route.Prefix, route.PrefixLength)}/{route.PrefixLength} {route.NextHop} {route.InterfaceName}"));
        if (_device.DynamicRouting.RipEnabled) lines.Add("router rip");
        if (_device.DynamicRouting.OspfEnabled) lines.Add($"router ospf area {_device.DynamicRouting.OspfArea}");
        if (_device.DynamicRouting.BgpEnabled)
        {
            lines.Add($"router bgp {_device.DynamicRouting.AutonomousSystem}");
            lines.AddRange(_device.DynamicRouting.BgpNeighbors.Select(neighbor => $" neighbor {neighbor}"));
        }
        return string.Join(Environment.NewLine, lines);
    }
}