using SiscoNet.Core;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Markdig;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException($"FAIL: {name}");
    Console.WriteLine($"PASS: {name}");
    checks++;
}

Check(IpAddressing.IsValidIpv4("192.168.10.10"), "IPv4 validation accepts a valid address");
Check(!IpAddressing.IsValidIpv4("300.168.10.10"), "IPv4 validation rejects an invalid octet");
Check(IpAddressing.IsInSameSubnet("192.168.10.10", "192.168.10.20", "255.255.255.0"), "subnet comparison recognizes local peers");
Check(!IpAddressing.IsInSameSubnet("192.168.10.10", "192.168.11.20", "255.255.255.0"), "subnet comparison separates networks");

var handshakeFrame = DiscordIpcProtocol.CreateHandshake("123456789012345678");
Check(BinaryPrimitives.ReadInt32LittleEndian(handshakeFrame.AsSpan(0, 4)) == DiscordIpcProtocol.HandshakeOpcode,
    "Discord handshake uses the IPC handshake opcode");
Check(BinaryPrimitives.ReadInt32LittleEndian(handshakeFrame.AsSpan(4, 4)) == handshakeFrame.Length - 8,
    "Discord handshake declares the UTF-8 payload length");
using (var handshakeJson = JsonDocument.Parse(handshakeFrame.AsMemory(8)))
    Check(handshakeJson.RootElement.GetProperty("client_id").GetString() == "123456789012345678", "Discord handshake includes the configured application ID");
var activityFrame = DiscordIpcProtocol.CreateSetActivity("Project: First LAN", "Workstation-A · PC", DateTimeOffset.UnixEpoch, "test-nonce");
Check(BinaryPrimitives.ReadInt32LittleEndian(activityFrame.AsSpan(0, 4)) == DiscordIpcProtocol.FrameOpcode,
    "Discord activity uses the frame opcode");
using (var activityJson = JsonDocument.Parse(activityFrame.AsMemory(8)))
{
    var activity = activityJson.RootElement.GetProperty("args").GetProperty("activity");
    Check(activityJson.RootElement.GetProperty("cmd").GetString() == "SET_ACTIVITY", "Discord activity command is SET_ACTIVITY");
    Check(activity.GetProperty("details").GetString() == "Project: First LAN" && activity.GetProperty("state").GetString() == "Workstation-A · PC",
        "Discord activity contains current project and device context");
}
    var helpMarkdownPath = Path.Combine(AppContext.BaseDirectory, "Help", "routing-and-cli.md");
    Check(File.Exists(helpMarkdownPath), "Markdown help articles are copied beside the app output");
    var helpPipeline = new MarkdownPipelineBuilder().DisableHtml().UseAdvancedExtensions().Build();
    var helpHtml = Markdown.ToHtml(File.ReadAllText(helpMarkdownPath), helpPipeline);
    Check(helpHtml.Contains("<h1", StringComparison.Ordinal) && helpHtml.Contains("<pre><code", StringComparison.Ordinal),
        "help article renders Markdown headings and fenced CLI examples");
    Check(!Markdown.ToHtml("<script>alert(1)</script>", helpPipeline).Contains("<script", StringComparison.OrdinalIgnoreCase),
        "Markdown help renderer strips raw HTML");

var source = DeviceCatalog.Create("PC", 1, 0, 0);
source.Interfaces[0].Ipv4Address = "192.168.10.10";
source.Interfaces[0].LinkUp = true;
var accessSwitch = DeviceCatalog.Create("Switch", 1, 0, 0);
var server = DeviceCatalog.Create("Server", 1, 0, 0);
server.Interfaces[0].Ipv4Address = "192.168.10.20";
server.Interfaces[0].LinkUp = true;
var project = new NetworkProject { Devices = [source, accessSwitch, server] };
project.Links.Add(new LinkModel { ADeviceId = source.Id, AInterface = source.Interfaces[0].Name, BDeviceId = accessSwitch.Id, BInterface = accessSwitch.Interfaces[0].Name });
project.Links.Add(new LinkModel { ADeviceId = accessSwitch.Id, AInterface = accessSwitch.Interfaces[1].Name, BDeviceId = server.Id, BInterface = server.Interfaces[0].Name });
accessSwitch.Interfaces[0].LinkUp = true;
accessSwitch.Interfaces[1].LinkUp = true;

var simulator = new NetworkSimulator();
var ping = simulator.Ping(project, source, "192.168.10.20");
Check(ping.Success, "ping crosses an active switch path");
var learning = ping.Events.Single(e => e.Action == "MAC learning");
Check(learning.IncomingInterface == accessSwitch.Interfaces[0].Name && learning.OutgoingInterface == accessSwitch.Interfaces[1].Name,
    "switch event reports correct ingress and egress interfaces");
Check(simulator.GetMacTable(accessSwitch.Id).ContainsKey(source.Interfaces[0].MacAddress), "switch learns the source MAC address");
Check(simulator.GetMacTable(accessSwitch.Id).ContainsKey(server.Interfaces[0].MacAddress), "switch learns the reply source MAC on the reverse path");
Check(ping.Events.Any(e => e.Action == "ARP request" && e.DestinationMac == "FF:FF:FF:FF:FF:FF"), "ARP request is emitted as an Ethernet broadcast");
Check(ping.Events.Single(e => e.Action == "MAC learning").Vlan == 1, "switch event preserves ingress VLAN");

server.Interfaces[0].Vlan = 20;
Check(!simulator.Ping(project, source, "192.168.10.20").Success, "VLAN mismatch blocks forwarding across a physical link");
server.Interfaces[0].Vlan = 1;
project.Links[1].IsUp = false;
Check(!simulator.Ping(project, source, "192.168.10.20").Success, "down cable prevents path discovery");
project.Links[1].IsUp = true;

server.Interfaces[0].AdminUp = false;
Check(!simulator.Ping(project, source, "192.168.10.20").Success, "administratively down destination rejects ping");
server.Interfaces[0].AdminUp = true;

var cli = new CliSession(project, simulator);
cli.SelectDevice(source);
Check(cli.Execute("enable") == "", "CLI enters privileged mode");
Check(cli.Execute("configure terminal").Contains("Configuration mode"), "CLI enters configuration mode");
Check(cli.Execute("interface eth0").Contains("selected"), "CLI selects an interface");
Check(cli.Execute("ip address 192.168.10.15 255.255.255.0").Contains("configured"), "CLI configures IPv4 on shared model");
Check(source.Interfaces[0].Ipv4Address == "192.168.10.15", "CLI and device model share configuration state");

var routedPc = DeviceCatalog.Create("PC", 2, 0, 0);
routedPc.Interfaces[0].Ipv4Address = "10.0.0.2";
routedPc.DefaultGateway = "10.0.0.1";
routedPc.Interfaces[0].LinkUp = true;
var routerA = DeviceCatalog.Create("Router", 1, 0, 0);
routerA.Interfaces[0].Ipv4Address = "10.0.0.1";
routerA.Interfaces[0].SubnetMask = "255.255.255.0";
routerA.Interfaces[1].Ipv4Address = "10.0.12.1";
routerA.Interfaces[1].SubnetMask = "255.255.255.252";
var routerB = DeviceCatalog.Create("Router", 2, 0, 0);
routerB.Interfaces[0].Ipv4Address = "10.0.12.2";
routerB.Interfaces[0].SubnetMask = "255.255.255.252";
routerB.Interfaces[1].Ipv4Address = "10.0.2.1";
routerB.Interfaces[1].SubnetMask = "255.255.255.0";
var routedServer = DeviceCatalog.Create("Server", 2, 0, 0);
routedServer.Interfaces[0].Ipv4Address = "10.0.2.2";
routedServer.DefaultGateway = "10.0.2.1";
routedServer.Interfaces[0].LinkUp = true;
foreach (var iface in routerA.Interfaces.Take(2).Concat(routerB.Interfaces.Take(2))) iface.LinkUp = true;
var routedProject = new NetworkProject { Devices = [routedPc, routerA, routerB, routedServer] };
routedProject.Links.Add(new LinkModel { ADeviceId = routedPc.Id, AInterface = routedPc.Interfaces[0].Name, BDeviceId = routerA.Id, BInterface = routerA.Interfaces[0].Name });
routedProject.Links.Add(new LinkModel { ADeviceId = routerA.Id, AInterface = routerA.Interfaces[1].Name, BDeviceId = routerB.Id, BInterface = routerB.Interfaces[0].Name });
routedProject.Links.Add(new LinkModel { ADeviceId = routerB.Id, AInterface = routerB.Interfaces[1].Name, BDeviceId = routedServer.Id, BInterface = routedServer.Interfaces[0].Name });
var routedSimulator = new NetworkSimulator();
Check(!routedSimulator.Ping(routedProject, routedPc, "10.0.2.2").Success, "inter-subnet ping fails when a router lacks a destination route");

var routerCli = new CliSession(routedProject, routedSimulator);
routerCli.SelectDevice(routerA);
routerCli.Execute("enable");
routerCli.Execute("configure terminal");
Check(routerCli.Execute("ip route 10.0.2.0 255.255.255.0 10.0.12.2 ge0/2").Contains("configured"), "CLI installs a static route with next hop and egress interface");
var secondRouterCli = new CliSession(routedProject, routedSimulator);
secondRouterCli.SelectDevice(routerB);
secondRouterCli.Execute("enable");
secondRouterCli.Execute("configure terminal");
Check(secondRouterCli.Execute("ip route 10.0.0.0 255.255.255.0 10.0.12.1 ge0/1").Contains("configured"), "CLI installs the return static route");
Check(routerCli.Execute("show ip route").Contains("S  10.0.2.0/24"), "route-table display exposes configured static routes");
var routedPing = routedSimulator.Ping(routedProject, routedPc, "10.0.2.2");
Check(routedPing.Success, "reciprocal static routes allow a two-router ping");
Check(routedPing.Events.Any(item => item.Action == "Routing lookup" && item.Detail.Contains("10.0.12.2")), "routing event reports the selected next hop");
Check(RouteEngine.Resolve(routerA, "10.0.2.9")?.Network == "10.0.2.0", "route lookup selects the matching destination prefix");
Check(!RouteEngine.TryCreateStaticRoute("10.0.3.0", "255.0.255.0", "10.0.12.2", "ge0/2", out _),
    "static route validation rejects non-contiguous subnet masks");

routerA.StaticRoutes.Clear();
routerB.StaticRoutes.Clear();
var ripCliA = new CliSession(routedProject, routedSimulator);
ripCliA.SelectDevice(routerA);
ripCliA.Execute("enable");
ripCliA.Execute("configure terminal");
Check(ripCliA.Execute("router rip").Contains("RIP"), "CLI enables RIP routing mode");
var ripCliB = new CliSession(routedProject, routedSimulator);
ripCliB.SelectDevice(routerB);
ripCliB.Execute("enable");
ripCliB.Execute("configure terminal");
ripCliB.Execute("router rip");
DynamicRoutingEngine.Recalculate(routedProject);
Check(RouteEngine.Resolve(routerA, "10.0.2.2")?.Source == "RIP", "RIP learns a remote connected subnet");
var ripPing = routedSimulator.Ping(routedProject, routedPc, "10.0.2.2");
Check(ripPing.Success, $"RIP-learned forward and return routes carry a ping: {ripPing.Summary}");

routerA.DynamicRouting.RipEnabled = false;
routerB.DynamicRouting.RipEnabled = false;
routerA.DynamicRouting.OspfEnabled = true;
routerB.DynamicRouting.OspfEnabled = true;
DynamicRoutingEngine.Recalculate(routedProject);
Check(RouteEngine.Resolve(routerA, "10.0.2.2")?.Source == "OSPF", "OSPF learns a remote connected subnet");
Check(routedSimulator.Ping(routedProject, routedPc, "10.0.2.2").Success, "OSPF-learned forward and return routes carry a ping");

routerA.DynamicRouting.OspfEnabled = false;
routerB.DynamicRouting.OspfEnabled = false;
routerA.DynamicRouting.BgpEnabled = true;
routerB.DynamicRouting.BgpEnabled = true;
routerA.DynamicRouting.BgpNeighbors = ["10.0.12.2"];
routerB.DynamicRouting.BgpNeighbors = ["10.0.12.1"];
DynamicRoutingEngine.Recalculate(routedProject);
Check(RouteEngine.Resolve(routerA, "10.0.2.2")?.Source == "BGP", "configured adjacent BGP peer advertises its connected network");
Check(routedSimulator.Ping(routedProject, routedPc, "10.0.2.2").Success, "direct-peer BGP routes carry a ping in both directions");
routerA.StaticRoutes.Add(new StaticRouteModel { Network = "10.0.2.0", SubnetMask = "255.255.255.0", NextHop = "10.0.12.2", InterfaceName = "ge0/2" });
routerA.StaticIpv6Routes.Add(new StaticIpv6RouteModel { Prefix = "2001:db8:2::", PrefixLength = 64, NextHop = "2001:db8:12::2", InterfaceName = "ge0/2" });
routerA.Groups.Add("Core");
routerA.Notes = "Transit router for lab";
routedProject.Annotations.Add(new CanvasAnnotation { Text = "WAN transit", X = 420, Y = 260 });
routerA.Nat = new NatConfiguration
{
    Enabled = true, InsideInterface = "ge0/1", OutsideInterface = "ge0/2", InsideNetwork = "10.0.0.0",
    InsideMask = "255.255.255.0", OutsideAddress = "10.0.12.1"
};
var natRequest = new NetworkServiceEngine(routedProject).SendTcp(routedPc, "10.0.2.2", 8080, "NAT test");
Check(natRequest.Success && natRequest.Events.Any(item => item.Action == "NAT translation" && item.Detail.Contains("to 10.0.12.1:", StringComparison.Ordinal)),
    "configured NAT rule emits an inside-to-outside translation event");
Check(natRequest.Events.Any(item => item.Action == "Transport segment received" && item.SourceIp == "10.0.12.1"),
    "NAT public source address is carried to the remote server");
Check(natRequest.Events.Any(item => item.Action == "NAT reverse translation" && item.DestinationIp == "10.0.0.2"),
    "NAT response path translates the public destination back to the inside host");

var projectPath = Path.Combine(Path.GetTempPath(), $"northstar-{Guid.NewGuid():N}.nslab");
try
{
    ProjectFileService.Save(project, projectPath);
    var loaded = ProjectFileService.Load(projectPath);
    Check(loaded.Devices.Count == 3 && loaded.Links.Count == 2, "project round-trip preserves devices and links");
    Check(loaded.Devices[0].Interfaces[0].Ipv4Address == "192.168.10.15", "project round-trip preserves configuration");
    var example = ProjectFileService.Load(Path.Combine(AppContext.BaseDirectory, "examples", "first-lan.nslab"));
    Check(example.Devices.Count == 3 && example.Links.Count == 2, "bundled example topology loads through the project service");
    ProjectFileService.Save(routedProject, projectPath);
    var loadedRoutes = ProjectFileService.Load(projectPath).Devices.Single(device => device.Id == routerA.Id).StaticRoutes;
    Check(loadedRoutes.Count == 1 && loadedRoutes[0].NextHop == "10.0.12.2", "project round-trip preserves static routes");
    var loadedProject = ProjectFileService.Load(projectPath);
    var loadedRouter = loadedProject.Devices.Single(device => device.Id == routerA.Id);
    Check(loadedRouter.StaticIpv6Routes.Count == 1 && loadedRouter.Groups.Contains("Core") && loadedProject.Annotations.Single().Text == "WAN transit",
        "project round-trip preserves IPv6 routes, device groups, and annotations");
    Check(loadedRouter.Nat.Enabled && loadedRouter.DynamicRouting.BgpEnabled, "project round-trip preserves NAT and dynamic-routing settings");
}
finally
{
    if (File.Exists(projectPath)) File.Delete(projectPath);
}

    var serviceClient = DeviceCatalog.Create("PC", 40, 0, 0);
    serviceClient.Interfaces[0].Ipv4Address = "192.168.50.10";
    serviceClient.Interfaces[0].LinkUp = true;
    serviceClient.DnsServerAddress = "192.168.50.20";
    var serviceSwitch = DeviceCatalog.Create("Switch", 40, 0, 0);
    var serviceServer = DeviceCatalog.Create("Server", 40, 0, 0);
    serviceServer.Interfaces[0].Ipv4Address = "192.168.50.20";
    serviceServer.Interfaces[0].LinkUp = true;
    serviceServer.Services.HttpEnabled = true;
    serviceServer.Services.HttpsEnabled = true;
    serviceServer.Services.DnsEnabled = true;
    serviceServer.Services.FtpEnabled = true;
    serviceServer.Services.HttpContent = "Service lab is online.";
    serviceServer.Services.DnsRecords["lab.example"] = "192.168.50.20";
    serviceServer.Services.FtpFiles["readme.txt"] = "FTP sample file";
    serviceServer.Services.DhcpEnabled = true;
    serviceServer.Services.DhcpPoolStart = "192.168.50.100";
    serviceServer.Services.DhcpPoolEnd = "192.168.50.101";
    serviceServer.Services.DhcpSubnetMask = "255.255.255.0";
    serviceServer.Services.DhcpGateway = "192.168.50.1";
    serviceServer.Services.DhcpDnsServer = "192.168.50.20";
    var serviceProject = new NetworkProject { Devices = [serviceClient, serviceSwitch, serviceServer] };
    serviceClient.Interfaces[0].Vlan = 1;
    serviceSwitch.Interfaces[0].LinkUp = true;
    serviceSwitch.Interfaces[1].LinkUp = true;
    serviceProject.Links.Add(new LinkModel { ADeviceId = serviceClient.Id, AInterface = "eth0", BDeviceId = serviceSwitch.Id, BInterface = "ge0/1" });
    serviceProject.Links.Add(new LinkModel { ADeviceId = serviceSwitch.Id, AInterface = "ge0/2", BDeviceId = serviceServer.Id, BInterface = "ge0/1" });
    var applicationEngine = new NetworkServiceEngine(serviceProject);
    var httpResult = applicationEngine.RequestHttp(serviceClient, "192.168.50.20");
    Check(httpResult.Success && httpResult.Data == "Service lab is online.", "HTTP service returns configured server content");
    Check(httpResult.Events.Any(item => item.Action == "TCP SYN") && httpResult.Events.Any(item => item.Action == "TCP SYN-ACK") && httpResult.Events.Any(item => item.Action == "TCP FIN/ACK"),
        "HTTP request emits TCP handshake and teardown events");
    Check(httpResult.Events.Any(item => item.Transport == "TCP" && item.DestinationPort == 80), "HTTP events carry TCP port metadata");
    var httpsResult = applicationEngine.RequestHttp(serviceClient, "192.168.50.20", secure: true);
    Check(httpsResult.Success && httpsResult.Events.Any(item => item.Action == "TLS handshake"), "HTTPS service emits an educational TLS session event");
    var dnsResult = applicationEngine.ResolveDns(serviceClient, "lab.example");
    Check(dnsResult.Success && dnsResult.Data == "192.168.50.20" && dnsResult.Events.Any(item => item.Transport == "UDP"), "DNS resolves configured records over UDP");
    var ftpResult = applicationEngine.RequestFtp(serviceClient, "192.168.50.20");
    Check(ftpResult.Success && ftpResult.Data.Contains("readme.txt", StringComparison.Ordinal), "FTP lists configured server files over TCP");

    var dhcpClient = DeviceCatalog.Create("Laptop", 41, 0, 0);
    dhcpClient.Interfaces[0].LinkUp = true;
    serviceProject.Devices.Add(dhcpClient);
    serviceSwitch.Interfaces[2].LinkUp = true;
    serviceProject.Links.Add(new LinkModel { ADeviceId = dhcpClient.Id, AInterface = "eth0", BDeviceId = serviceSwitch.Id, BInterface = "ge0/3" });
    var dhcpResult = applicationEngine.RequestDhcp(dhcpClient);
    Check(dhcpResult.Success && dhcpClient.Interfaces[0].Ipv4Address == "192.168.50.100", "DHCP assigns an address to an unconfigured endpoint");
    Check(dhcpClient.DefaultGateway == "192.168.50.1" && dhcpClient.DnsServerAddress == "192.168.50.20", "DHCP installs gateway and DNS options");

    serviceSwitch.AccessRules.Add(new AccessControlRule
    {
        Permit = false, Protocol = "TCP", SourceNetwork = "0.0.0.0", SourceMask = "0.0.0.0",
        DestinationNetwork = "192.168.50.20", DestinationMask = "255.255.255.255", DestinationPort = 80
    });
    Check(!applicationEngine.RequestHttp(serviceClient, "192.168.50.20").Success, "ACL denies matching HTTP traffic");
    serviceSwitch.AccessRules.Clear();
    var securedPort = serviceSwitch.Interfaces[0];
    securedPort.PortSecurityEnabled = true;
    securedPort.MaximumMacAddresses = 1;
    serviceSwitch.LearnedMacAddresses["02:AA:AA:AA:AA:AA"] = new MacLearningEntry { InterfaceName = "ge0/1", Vlan = 1 };
    var trustedClientMac = serviceClient.Interfaces[0].MacAddress;
    serviceClient.Interfaces[0].MacAddress = "02:BB:BB:BB:BB:BB";
    Check(!applicationEngine.SendUdp(serviceClient, "192.168.50.20", 9999, "blocked source").Success,
        "port security rejects a new source after the configured MAC limit");
    serviceClient.Interfaces[0].MacAddress = trustedClientMac;
    serviceSwitch.LearnedMacAddresses.Clear();

    var switchFixture = DeviceCatalog.Create("Switch", 50, 0, 0);
    foreach (var port in switchFixture.Interfaces) port.LinkUp = true;
    switchFixture.Interfaces[0].Vlan = 10;
    switchFixture.Interfaces[1].Vlan = 10;
    switchFixture.Interfaces[2].Vlan = 20;
    var floodDecision = SwitchingEngine.ProcessFrame(switchFixture, "ge0/1", "02:10:00:00:00:01", "02:10:00:00:00:FF", 10);
    Check(floodDecision.Accepted && floodDecision.EgressInterfaces.SequenceEqual(["ge0/2"]), "unknown unicast floods only within the access VLAN");
    SwitchingEngine.ProcessFrame(switchFixture, "ge0/2", "02:10:00:00:00:FF", "02:10:00:00:00:01", 10);
    var unicastDecision = SwitchingEngine.ProcessFrame(switchFixture, "ge0/1", "02:10:00:00:00:01", "02:10:00:00:00:FF", 10);
    Check(unicastDecision.EgressInterfaces.SequenceEqual(["ge0/2"]), "known MAC is forwarded to its learned port");
    var trunk = switchFixture.Interfaces[0];
    trunk.PortMode = "trunk";
    trunk.AllowedVlans = "10,20";
    Check(SwitchingEngine.AllowsVlan(trunk, 20) && !SwitchingEngine.AllowsVlan(trunk, 30), "trunk interface enforces its allowed VLAN list");
    switchFixture.Interfaces[1].StpState = "blocking";
    Check(!SwitchingEngine.ProcessFrame(switchFixture, "ge0/1", "02:10:00:00:00:01", "02:10:00:00:00:FF", 10).Accepted,
        "STP-blocked ingress does not forward frames");

    var bundleFixture = DeviceCatalog.Create("Switch", 51, 0, 0);
    foreach (var port in bundleFixture.Interfaces) port.LinkUp = true;
    bundleFixture.Interfaces[1].BundleGroup = "core-a";
    bundleFixture.Interfaces[2].BundleGroup = "core-a";
    bundleFixture.LearnedMacAddresses["02:CC:CC:CC:CC:CC"] = new MacLearningEntry { InterfaceName = "ge0/2", Vlan = 1 };
    var bundleDecision = SwitchingEngine.ProcessFrame(bundleFixture, "ge0/1", "02:10:00:00:00:01", "02:CC:CC:CC:CC:CC", 1);
    Check(bundleDecision.EgressInterfaces.Count == 2 && bundleDecision.EgressInterfaces.Contains("ge0/3"), "EtherChannel forwarding selects all active bundle members");

    var loopSwitches = Enumerable.Range(1, 3).Select(index => DeviceCatalog.Create("Switch", 80 + index, 0, 0)).ToArray();
    var loopProject = new NetworkProject { Devices = loopSwitches.ToList() };
    void AddSwitchLink(DeviceModel first, int firstPort, DeviceModel second, int secondPort)
    {
        first.Interfaces[firstPort].LinkUp = true;
        second.Interfaces[secondPort].LinkUp = true;
        loopProject.Links.Add(new LinkModel
        {
            ADeviceId = first.Id, AInterface = first.Interfaces[firstPort].Name,
            BDeviceId = second.Id, BInterface = second.Interfaces[secondPort].Name
        });
    }
    AddSwitchLink(loopSwitches[0], 0, loopSwitches[1], 0);
    AddSwitchLink(loopSwitches[1], 1, loopSwitches[2], 0);
    AddSwitchLink(loopSwitches[2], 1, loopSwitches[0], 1);
    SpanningTreeEngine.Recalculate(loopProject);
    Check(loopSwitches.SelectMany(device => device.Interfaces).Count(iface => iface.StpState == "blocking") == 2,
        "automatic STP blocks both ends of a redundant switch link in a triangle");

    serviceClient.Interfaces[0].Ipv6Address = "2001:db8:50::10";
    serviceClient.Interfaces[0].Ipv6PrefixLength = 64;
    serviceServer.Interfaces[0].Ipv6Address = "2001:db8:50::20";
    serviceServer.Interfaces[0].Ipv6PrefixLength = 64;
    var ipv6Ping = simulator.PingIpv6(serviceProject, serviceClient, "2001:db8:50::20");
    Check(ipv6Ping.Success && ipv6Ping.Events.Any(item => item.Action == "Neighbor solicitation"), "IPv6 ping emits neighbor discovery and ICMPv6 events");
    Check(ipv6Ping.Events.All(item => item.Protocol.Contains("IPv6", StringComparison.OrdinalIgnoreCase)), "IPv6 packet events preserve IPv6 protocol metadata");

    var wirelessClient = DeviceCatalog.Create("Laptop", 60, 0, 0);
    wirelessClient.Interfaces.Single(iface => iface.Medium == "Wireless").Ipv4Address = "192.168.60.10";
    var accessPoint = DeviceCatalog.Create("Access Point", 60, 100, 0);
    accessPoint.Wireless.Enabled = true;
    accessPoint.Wireless.Ssid = "Lab-WiFi";
    accessPoint.Wireless.Passphrase = "northstar-pass";
    accessPoint.Wireless.RangeMeters = 30;
    var wifiServer = DeviceCatalog.Create("Server", 60, 180, 0);
    wifiServer.Interfaces[0].Ipv4Address = "192.168.60.20";
    wifiServer.Interfaces[0].LinkUp = true;
    wifiServer.Services.HttpEnabled = true;
    wifiServer.Services.HttpContent = "Wireless path is live.";
    var wifiProject = new NetworkProject { Devices = [wirelessClient, accessPoint, wifiServer] };
    var rejectedAssociation = WirelessEngine.Associate(wifiProject, wirelessClient, accessPoint, "wrong-password");
    Check(!rejectedAssociation.Success, "wireless authentication rejects an incorrect passphrase");
    var association = WirelessEngine.Associate(wifiProject, wirelessClient, accessPoint, "northstar-pass");
    Check(association.Success && association.Link?.CableType.Contains("Wireless", StringComparison.Ordinal) == true, "wireless client associates within range using its configured passphrase");
    var accessPointEthernet = accessPoint.Interfaces.First(iface => iface.Medium == "Ethernet");
    var wifiServerEthernet = wifiServer.Interfaces.First(iface => iface.Medium == "Ethernet");
    accessPointEthernet.LinkUp = true;
    wifiProject.Links.Add(new LinkModel { ADeviceId = accessPoint.Id, AInterface = accessPointEthernet.Name, BDeviceId = wifiServer.Id, BInterface = wifiServerEthernet.Name });
    var wifiHttp = new NetworkServiceEngine(wifiProject).RequestHttp(wirelessClient, "192.168.60.20");
    Check(wifiHttp.Success, $"wireless association carries simulated HTTP traffic to a wired server: {wifiHttp.Summary}");

    var temperatureSensor = DeviceCatalog.Create("Temperature Sensor", 70, 0, 0);
    var smartFan = DeviceCatalog.Create("Fan", 70, 0, 0);
    var iotProject = new NetworkProject { Devices = [temperatureSensor, smartFan] };
    iotProject.IotRules.Add(new IotAutomationRule
    {
        SensorDeviceId = temperatureSensor.Id, Property = "temperature", Comparison = ">", Threshold = 30,
        TargetDeviceId = smartFan.Id, Action = "on"
    });
    var iotActions = IotEngine.SetSensorValue(iotProject, temperatureSensor.Id, "temperature", 33.5);
    Check(iotActions.Single().Applied && smartFan.ActuatorEnabled, "IoT temperature rule activates a fan above its threshold");
    Check(temperatureSensor.SensorValues["temperature"] == 33.5, "IoT sensor telemetry persists in the shared device model");

    serviceClient.Interfaces[0].Ipv4Address = "192.168.50.25";
    serviceClient.Interfaces[0].SubnetMask = "255.255.255.0";
    serviceClient.DefaultGateway = "";
    var labObjective = new LabObjective
    {
        Title = "Address the workstation", DeviceName = serviceClient.Name, InterfaceName = "eth0",
        ExpectedIpv4Address = "192.168.50.25", ExpectedSubnetMask = "255.255.255.0", Points = 20, Hint = "Check the assigned address and mask."
    };
    serviceProject.Objectives.Add(labObjective);
    var labResult = LabEvaluationEngine.Evaluate(serviceProject);
    Check(labResult.Score == 20 && labResult.Objectives.Single().Complete, "learning objective awards points for a matching configuration");
    Check(LabEvaluationEngine.GetHint(serviceProject, labObjective.Id) == labObjective.Hint, "learning objective hints are available on request");

    var cliServices = new NetworkServiceEngine(serviceProject);
    var serviceCli = new CliSession(serviceProject, simulator, cliServices);
    serviceCli.SelectDevice(serviceClient);
    var cliDnsEvents = false;
    Check(serviceCli.Execute("nslookup lab.example", onService: result => cliDnsEvents = result.Success).Contains("resolves to") && cliDnsEvents,
        "CLI DNS query executes through the same application service engine");
    var cliHttpEvents = false;
    Check(serviceCli.Execute("http get 192.168.50.20", onService: result => cliHttpEvents = result.Success).Contains("completed") && cliHttpEvents,
        "CLI HTTP command produces a simulated TCP request");
    var ipv6Cli = new CliSession(serviceProject, simulator);
    ipv6Cli.SelectDevice(serviceClient);
    ipv6Cli.Execute("enable");
    ipv6Cli.Execute("configure terminal");
    ipv6Cli.Execute("interface eth0");
    Check(ipv6Cli.Execute("ipv6 address 2001:db8:50::25/64").Contains("configured"), "CLI configures IPv6 address and prefix on an interface");
    Check(ipv6Cli.Execute("show ipv6 route").Contains("2001:db8:50::/64"), "CLI displays connected IPv6 routes");
    Check(ipv6Cli.Execute("ipv6 route 2001:db8:60::/64 2001:db8:50::1 eth0").Contains("configured"), "CLI installs a static IPv6 route");
    Check(ipv6Cli.Execute("show ipv6 route").Contains("2001:db8:60::/64"), "CLI displays static IPv6 routes");

    var portProbe = new TcpListener(IPAddress.Loopback, 0);
    portProbe.Start();
    var multiplayerPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
    portProbe.Stop();
    var sharedHostProject = new NetworkProject
    {
        Name = "Shared Lab",
        Devices = [DeviceCatalog.Create("PC", 90, 100, 100)]
    };
    var hostSession = new MultiplayerSession();
    var firstClient = new MultiplayerSession();
    var secondClient = new MultiplayerSession();
    var firstSnapshot = new TaskCompletionSource<NetworkProject>(TaskCreationOptions.RunContinuationsAsynchronously);
    var hostUpdate = new TaskCompletionSource<NetworkProject>(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondSnapshot = new TaskCompletionSource<NetworkProject>(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondReload = new TaskCompletionSource<NetworkProject>(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondSnapshotCount = 0;
    firstClient.ProjectReceived += project => firstSnapshot.TrySetResult(project);
    hostSession.ProjectReceived += project =>
    {
        if (project.Devices.FirstOrDefault()?.Name == "Peer-Renamed-PC") hostUpdate.TrySetResult(project);
    };
    secondClient.ProjectReceived += project =>
    {
        if (Interlocked.Increment(ref secondSnapshotCount) == 1) secondSnapshot.TrySetResult(project);
        else secondReload.TrySetResult(project);
    };
    await hostSession.StartSharingAsync(sharedHostProject, "Host", multiplayerPort);
    await firstClient.ConnectAsync("127.0.0.1", multiplayerPort, "Alice");
    var firstSharedCopy = await firstSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(firstSharedCopy.Name == "Shared Lab" && firstSharedCopy.Devices.Count == 1, "P2P client receives the host topology snapshot");
    var sharedDeviceId = firstSharedCopy.Devices[0].Id;
    Check(firstClient.TryAcquireLock(sharedDeviceId, out _), "P2P peer acquires an available device edit lock");
    Check(!hostSession.TryAcquireLock(sharedDeviceId, out var lockedReason) && lockedReason.Contains("Alice", StringComparison.Ordinal),
        "host rejects an edit lock while a peer is using the device");
    firstSharedCopy.Devices[0].Name = "Peer-Renamed-PC";
    firstClient.PublishProject(firstSharedCopy, sharedDeviceId);
    var updatedHostCopy = await hostUpdate.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(updatedHostCopy.Devices[0].Name == "Peer-Renamed-PC", "host accepts and merges a device update from its lock owner");
    await secondClient.ConnectAsync("127.0.0.1", multiplayerPort, "Bob");
    await secondSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!secondClient.TryAcquireLock(sharedDeviceId, out var secondLockReason) && secondLockReason.Contains("Alice", StringComparison.Ordinal),
        "second peer cannot acquire an object already locked by another peer");
    firstClient.ReleaseLock(sharedDeviceId);
    Check(secondClient.TryAcquireLock(sharedDeviceId, out _), "device lock becomes available after its owner releases it");
    secondClient.ReleaseLock(sharedDeviceId);
    Check(firstClient.TryAcquireLock(Guid.Empty, out _), "peer can acquire the exclusive topology lock when no other peer is editing");
    Check(!secondClient.TryAcquireLock(sharedDeviceId, out var globalLockReason) && globalLockReason.Contains("Alice", StringComparison.Ordinal),
        "global topology lock blocks device edits by other peers");
    firstClient.ReleaseLock(Guid.Empty);
    Check(hostSession.TryAcquireLock(sharedDeviceId, out _), "device locks become available after the topology lock is released");
    hostSession.ReleaseLock(sharedDeviceId);
    secondClient.ReloadFromHost();
    var reloaded = await secondReload.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(reloaded.Devices[0].Name == "Peer-Renamed-PC", "client reload receives the latest authoritative host snapshot");
    await firstClient.DisconnectAsync();
    await secondClient.DisconnectAsync();
    await hostSession.DisconnectAsync();
    Check(!firstClient.IsSharing && !secondClient.IsSharing && !hostSession.IsSharing, "P2P peers and host disconnect cleanly");

Console.WriteLine($"{checks} smoke checks passed.");