namespace SiscoNet.Core;

public sealed record WirelessResult(bool Success, string Message, LinkModel? Link);

public static class WirelessEngine
{
    public static WirelessResult Associate(NetworkProject project, DeviceModel client, DeviceModel accessPoint, string passphrase)
    {
        var radio = accessPoint.Interfaces.FirstOrDefault(iface => iface.Medium == "Wireless");
        var clientRadio = client.Interfaces.FirstOrDefault(iface => iface.Medium == "Wireless");
        if (radio is null || clientRadio is null) return new WirelessResult(false, "Both devices need a wireless interface.", null);
        if (!accessPoint.Wireless.Enabled) return new WirelessResult(false, $"Wireless radio is disabled on {accessPoint.Name}.", null);
        if (project.Links.Any(link => link.IsUp && (link.ADeviceId == client.Id && link.AInterface == clientRadio.Name || link.BDeviceId == client.Id && link.BInterface == clientRadio.Name)))
            return new WirelessResult(false, $"{client.Name} is already associated or linked on {clientRadio.Name}.", null);

        var distanceMeters = Math.Sqrt(Math.Pow(client.X - accessPoint.X, 2) + Math.Pow(client.Y - accessPoint.Y, 2)) / 10;
        if (distanceMeters > accessPoint.Wireless.RangeMeters)
            return new WirelessResult(false, $"Client is outside the {accessPoint.Wireless.RangeMeters} m radio range ({distanceMeters:0.0} m away).", null);
        if (!accessPoint.Wireless.SecurityMode.Equals("Open", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(passphrase, accessPoint.Wireless.Passphrase, StringComparison.Ordinal))
            return new WirelessResult(false, "Wireless authentication failed.", null);

        clientRadio.Vlan = radio.Vlan;
        clientRadio.AdminUp = true;
        clientRadio.LinkUp = true;
        radio.LinkUp = true;
        accessPoint.Wireless.AssociatedClients.Add(client.Id);
        var link = new LinkModel
        {
            ADeviceId = client.Id,
            AInterface = clientRadio.Name,
            BDeviceId = accessPoint.Id,
            BInterface = radio.Name,
            IsUp = true,
            CableType = $"Wireless 802.11 ({accessPoint.Wireless.Ssid})"
        };
        project.Links.Add(link);
        return new WirelessResult(true, $"{client.Name} associated to {accessPoint.Wireless.Ssid} ({distanceMeters:0.0} m).", link);
    }

    public static bool Disconnect(NetworkProject project, DeviceModel client, DeviceModel accessPoint)
    {
        var removed = project.Links.RemoveAll(link => link.CableType.StartsWith("Wireless", StringComparison.OrdinalIgnoreCase) &&
            ((link.ADeviceId == client.Id && link.BDeviceId == accessPoint.Id) || (link.BDeviceId == client.Id && link.ADeviceId == accessPoint.Id))) > 0;
        if (!removed) return false;
        accessPoint.Wireless.AssociatedClients.Remove(client.Id);
        if (accessPoint.Wireless.AssociatedClients.Count == 0)
            foreach (var radio in accessPoint.Interfaces.Where(iface => iface.Medium == "Wireless")) radio.LinkUp = false;
        foreach (var iface in client.Interfaces.Where(iface => iface.Medium == "Wireless")) iface.LinkUp = false;
        return true;
    }
}