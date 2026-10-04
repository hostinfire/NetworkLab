namespace SiscoNet.Core;

public sealed record SwitchingDecision(bool Accepted, string Detail, IReadOnlyList<string> EgressInterfaces, string? LearnedInterface);

public static class SwitchingEngine
{
    public static SwitchingDecision ProcessFrame(DeviceModel device, string ingressName, string sourceMac,
        string destinationMac, int vlan, bool isBroadcast = false)
    {
        foreach (var expired in device.LearnedMacAddresses.Where(pair => DateTime.UtcNow - pair.Value.LearnedAt > TimeSpan.FromMinutes(5)).Select(pair => pair.Key).ToArray())
            device.LearnedMacAddresses.Remove(expired);
        var ingress = device.Interfaces.FirstOrDefault(iface => iface.Name.Equals(ingressName, StringComparison.OrdinalIgnoreCase));
        if (ingress is null || !ingress.AdminUp || !ingress.LinkUp)
            return new SwitchingDecision(false, "Ingress interface is down or missing.", [], null);
        if (ingress.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase))
            return new SwitchingDecision(false, $"STP blocks ingress {ingress.Name}.", [], null);
        if (!AllowsVlan(ingress, vlan))
            return new SwitchingDecision(false, $"VLAN {vlan} is not admitted on {ingress.Name}.", [], null);

        var previouslyLearned = device.LearnedMacAddresses.TryGetValue(sourceMac, out var existing);
        if (ingress.PortSecurityEnabled && (!previouslyLearned || existing!.InterfaceName != ingress.Name) &&
            device.LearnedMacAddresses.Values.Count(entry => entry.InterfaceName == ingress.Name) >= ingress.MaximumMacAddresses)
            return new SwitchingDecision(false, $"Port security limit exceeded on {ingress.Name}.", [], null);

        device.LearnedMacAddresses[sourceMac] = new MacLearningEntry
        {
            InterfaceName = ingress.Name,
            Vlan = vlan,
            LearnedAt = DateTime.UtcNow
        };

        string[] candidatePorts;
        if (!isBroadcast && device.LearnedMacAddresses.TryGetValue(destinationMac, out var destination) && destination.Vlan == vlan)
            candidatePorts = [destination.InterfaceName];
        else
            candidatePorts = device.Interfaces.Where(iface => iface.Name != ingress.Name).Select(iface => iface.Name).ToArray();

        var egress = candidatePorts
            .Select(name => device.Interfaces.FirstOrDefault(iface => iface.Name == name))
            .Where(iface => iface is not null && iface.AdminUp && iface.LinkUp && !iface.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase) && AllowsVlan(iface, vlan))
            .Cast<NetworkInterfaceModel>()
            .ToArray();

        egress = egress.SelectMany(port => port.BundleGroup.Length > 0
                    ? device.Interfaces.Where(member => member.BundleGroup == port.BundleGroup && member.AdminUp && member.LinkUp && AllowsVlan(member, vlan))
                    : [port])
                .DistinctBy(port => port.Name).ToArray();

        var knownUnicast = !isBroadcast && device.LearnedMacAddresses.TryGetValue(destinationMac, out destination) && destination.Vlan == vlan;
        var detail = knownUnicast
            ? $"Learned {sourceMac} on {ingress.Name}; forwarded known unicast on VLAN {vlan}."
            : $"Learned {sourceMac} on {ingress.Name}; flooded eligible VLAN {vlan} ports.";
        return new SwitchingDecision(egress.Length > 0, egress.Length > 0 ? detail : $"{detail} No eligible egress ports.", egress.Select(port => port.Name).ToArray(), ingress.Name);
    }

    public static bool AllowsVlan(NetworkInterfaceModel port, int vlan)
    {
        if (port.PortMode.Equals("access", StringComparison.OrdinalIgnoreCase)) return port.Vlan == vlan;
        if (!port.PortMode.Equals("trunk", StringComparison.OrdinalIgnoreCase)) return false;
        return ParseAllowedVlans(port.AllowedVlans).Contains(vlan);
    }

    public static bool LinkAllowsVlan(NetworkInterfaceModel left, NetworkInterfaceModel right, int vlan) =>
        AllowsVlan(left, vlan) && AllowsVlan(right, vlan);

    private static HashSet<int> ParseAllowedVlans(string list)
    {
        var allowed = new HashSet<int>();
        foreach (var part in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1 && int.TryParse(range[0], out var vlan) && vlan is >= 1 and <= 4094) allowed.Add(vlan);
            else if (range.Length == 2 && int.TryParse(range[0], out var start) && int.TryParse(range[1], out var end) && start is >= 1 and <= 4094 && end >= start && end <= 4094)
                for (var current = start; current <= end; current++) allowed.Add(current);
        }
        return allowed;
    }
}