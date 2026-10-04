using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SiscoNet.Core;

public static class ProjectFileService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(NetworkProject project, string path)
    {
        project.FormatVersion = 1;
        var json = JsonSerializer.Serialize(project, Options);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, true);
    }

    public static NetworkProject Load(string path)
    {
        var project = JsonSerializer.Deserialize<NetworkProject>(File.ReadAllText(path), Options)
            ?? throw new InvalidDataException("The project file is empty or invalid.");
        if (project.FormatVersion != 1)
            throw new InvalidDataException($"Project format version {project.FormatVersion} is not supported.");
        project.Devices ??= [];
        project.Links ??= [];
        foreach (var device in project.Devices)
        {
            device.Interfaces ??= [];
            device.StaticRoutes ??= [];
            device.DynamicRoutes ??= [];
            device.DynamicRouting ??= new DynamicRoutingConfiguration();
            device.DynamicRouting.AdvertisedNetworks ??= [];
            device.DynamicRouting.BgpNeighbors ??= [];
            device.StaticIpv6Routes ??= [];
            device.Services ??= new NetworkServices();
            device.Services.DnsRecords ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            device.Services.FtpFiles ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            device.Wireless ??= new WirelessConfiguration();
            device.Wireless.AssociatedClients ??= [];
            device.AccessRules ??= [];
            device.Nat ??= new NatConfiguration();
            device.LearnedMacAddresses ??= new Dictionary<string, MacLearningEntry>(StringComparer.OrdinalIgnoreCase);
            device.Groups ??= [];
            device.SensorValues ??= new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        }
        project.Objectives ??= [];
        project.IotRules ??= [];
        project.Annotations ??= [];
        return project;
    }
}