namespace SiscoNet.Core;

public static class SpanningTreeEngine
{
    public static void Recalculate(NetworkProject project)
    {
        foreach (var iface in project.Devices.SelectMany(device => device.Interfaces).Where(iface => iface.StpAutomatic))
            iface.StpState = "forwarding";

        var bridges = project.Devices.Where(IsBridge).OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var bridgeIds = bridges.Select(device => device.Id).ToHashSet();
        var bridgeLinks = project.Links.Where(link => link.IsUp && bridgeIds.Contains(link.ADeviceId) && bridgeIds.Contains(link.BDeviceId))
            .Where(link => PortCanParticipate(project, link, link.ADeviceId) && PortCanParticipate(project, link, link.BDeviceId)).ToArray();
        var treeLinks = new HashSet<Guid>();
        var visited = new HashSet<Guid>();

        foreach (var root in bridges)
        {
            if (!visited.Add(root.Id)) continue;
            var queue = new Queue<DeviceModel>();
            queue.Enqueue(root);
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                foreach (var link in bridgeLinks.Where(link => link.ADeviceId == current.Id || link.BDeviceId == current.Id)
                             .OrderBy(link => link.Id))
                {
                    var nextId = link.ADeviceId == current.Id ? link.BDeviceId : link.ADeviceId;
                    if (!visited.Add(nextId)) continue;
                    treeLinks.Add(link.Id);
                    if (GetInterface(current, link)?.StpAutomatic == true) GetInterface(current, link)!.StpState = "forwarding";
                    var next = bridges.First(device => device.Id == nextId);
                    if (GetInterface(next, link)?.StpAutomatic == true) GetInterface(next, link)!.StpState = "forwarding";
                    queue.Enqueue(next);
                }
            }
        }

        foreach (var link in bridgeLinks.Where(link => !treeLinks.Contains(link.Id)))
        {
            var first = project.Devices.First(device => device.Id == link.ADeviceId);
            var second = project.Devices.First(device => device.Id == link.BDeviceId);
            if (GetInterface(first, link) is { StpAutomatic: true } firstPort) firstPort.StpState = "blocking";
            if (GetInterface(second, link) is { StpAutomatic: true } secondPort) secondPort.StpState = "blocking";
        }
    }

    public static bool IsBridge(DeviceModel device) => device.Kind.Contains("Switch", StringComparison.OrdinalIgnoreCase) ||
        device.Kind is "Hub" or "Access Point";

    private static NetworkInterfaceModel? GetInterface(DeviceModel device, LinkModel link)
    {
        var name = link.ADeviceId == device.Id ? link.AInterface : link.BInterface;
        return device.Interfaces.FirstOrDefault(iface => iface.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static bool PortCanParticipate(NetworkProject project, LinkModel link, Guid deviceId)
    {
        var device = project.Devices.First(candidate => candidate.Id == deviceId);
        var iface = GetInterface(device, link);
        return iface is not null && iface.AdminUp && iface.LinkUp &&
            (iface.StpAutomatic || !iface.StpState.Equals("blocking", StringComparison.OrdinalIgnoreCase));
    }
}