# Northstar Network Lab

Northstar Network Lab is an independent Windows desktop workspace for learning network topology and device configuration. It is implemented in C# with WPF and separates the topology/simulation model from the desktop view.

## Build and run

Prerequisite: .NET 8 SDK on Windows.

```powershell
dotnet run --project src\src.csproj
```

## Build the Windows installer

See [INSTALL.md](INSTALL.md) for step-by-step install, first-run, uninstall, multiplayer firewall, and build instructions. Build the self-contained x64 publish and MSI with the .NET 8 SDK:

```powershell
powershell -ExecutionPolicy Bypass -File installer\Build-Installer.ps1
```

The installer is created at `installer\bin\x64\Release\NorthstarNetworkLab-Setup.msi`. It opens an interactive, branded wizard with the detailed `LICENSE.md` terms, a destination-folder page with **Browse**, install confirmation, progress, and completion screens. It adds a Start Menu shortcut. The self-contained app payload is in `artifacts\publish`; users do not need to install the .NET runtime separately. The installer is currently unsigned, so Windows may show the usual publisher warning. For public distribution, sign the MSI and application binaries with a trusted code-signing certificate.

Run the core smoke checks with:

```powershell
dotnet run --project tests\Simulator.SmokeTests.csproj -c Release
```

## Current capabilities

- Place endpoints, network devices, and IoT devices; Ctrl-click for multi-selection; move, duplicate, delete, search, and snap devices to the grid.
- Group selected devices, add/edit/delete canvas annotations, and create links with selectable Ethernet/fiber/serial cable metadata using available interfaces.
- Configure IPv4/IPv6, VLAN access/trunk allow-lists, native VLAN, port security, STP state, EtherChannel grouping, static/dynamic routing, wireless radios, ACLs, NAT, and server services from the Inspector.
- Simulate IPv4 ICMP and IPv6 ICMPv6, DHCP leases, DNS A lookups, HTTP/HTTPS content, FTP LIST/GET, TCP handshake/teardown events, and UDP datagrams.
- Route with connected/static IPv4 routes, educational RIP/OSPF propagation, and directly adjacent BGP advertisements. Longest-prefix choice, gateways, return routes, and egress interfaces are checked.
- Switch with access/trunk VLAN allow-lists, dynamic MAC learning/aging, unknown-unicast flooding, known-unicast forwarding, automatic spanning-tree loop blocking, port-security limits, and EtherChannel member egress.
- Associate wireless clients with an SSID using a passphrase/range check; run IoT sensor threshold rules and control actuators; create and score configuration objectives with on-demand hints.
- Drive configuration and traffic from the CLI using the same device/service model as the Inspector.
- Undo/redo edits, zoom, fit, toggle theme, resize panels, use simulation playback, and recover periodic autosave.
- Save and reopen versioned `.nslab` or JSON files.
- Host or join a direct TCP shared topology from the **Multiplayer** top tab; snapshots sync through an authoritative host and device/global edit locks prevent simultaneous changes.
- Connect Discord Rich Presence over Discord Desktop's local IPC pipe using an Application ID configured in **Tools > Discord Rich Presence**.
- Open the tabbed in-app Help Center from **Help > Help Center** or `F1`.

This is an educational event simulator, not a packet-accurate implementation. TCP sequence/retransmission state, TLS cryptography, DHCP relay/timers, DNS caching/record families, OSPF protocol packets/timers, RIP loop prevention/timers, full BGP sessions/policies/transit AS propagation, NAT port allocation/collisions, LACP negotiation, spanning-tree root election/timers, WPA cryptography/radio interference, IoT cloud services, protected hidden lab answers, instructor roster management, and collaboration remain out of scope. Dynamic protocols use deterministic topology calculations; cable types are recorded as topology metadata. Discord presence requires Discord Desktop and a numeric Developer Portal Application ID. Its local IPC connection does not upload topology data.
Multiplayer is direct TCP peer hosting for trusted LAN/classroom networks. Hosts need a reachable IP/port and possibly an inbound firewall rule; the protocol has no encryption, user authentication, relay, or NAT traversal. See **Help > Help Center > Multiplayer** before using it outside a trusted network.

## First topology

Add two PCs and a switch from the device library. Choose **Connect**, click the first PC, then the switch; repeat for the second PC. The endpoint addresses are prefilled in `192.168.10.0/24`. Select one PC and choose **Ping**, then enter the other PC's address. The **Simulation events** and **Packet inspector** tabs show the generated event path.

An example topology is included at `examples/first-lan.nslab` and can be opened from **File > Open**.

Open **Tools > Discord Rich Presence** to enter a numeric Application ID from the [Discord Developer Portal](https://discord.com/developers/applications). Keep Discord Desktop running; connection status appears at the right side of the workspace status bar. Open **Help > Help Center** or press `F1` for the in-app guides.

## Multiplayer

Open the **Multiplayer** tab beside **Workspace**. **Begin sharing** starts a TCP host (default port `47831`) and displays local host IP addresses. A peer chooses **Connect** and enters the host IP and port. The host is authoritative; project snapshots synchronize through it. Device selection takes an exclusive device lease, and topology-wide changes take an exclusive global lease. Objects locked by another peer appear gray and the Inspector is read-only.

Use **Reload project** to request the host snapshot, **Disconnect** to leave without closing the app, or **Restart app** to recover a stuck UI. The host needs an inbound TCP firewall rule for its selected port, and peers need a routable address. This is an unauthenticated, unencrypted direct connection with no relay/NAT traversal; use only on a trusted LAN or VPN.

## CLI reference

Select a device before entering commands. The prompt supports:

```text
enable
configure terminal
interface eth0
ip address 192.168.10.10 255.255.255.0
shutdown
no shutdown
hostname Edge
show interfaces
show ip interface
show ip route
ip route 10.0.2.0 255.255.255.0 10.0.12.2 ge0/2
no ip route 10.0.2.0 255.255.255.0 10.0.12.2
ipv6 address 2001:db8:1::10/64
ipv6 route 2001:db8:2::/64 2001:db8:1::1 ge0/1
ping ipv6 2001:db8:1::20
router rip
router ospf
router bgp 65001
neighbor 10.0.12.2
network 10.0.2.0
dhcp
nslookup lab.example
http get 192.168.10.20
https get 192.168.10.20
ftp list 192.168.10.20
ftp get 192.168.10.20 readme.txt
tcp send 192.168.10.20 8080 hello
udp send 192.168.10.20 9000 hello
show mac-address-table
show vlan
show running-config
show neighbors
ping 192.168.10.20
```

Interface names are displayed by the inspector (single-port endpoints use `eth0`; multiport devices use `ge0/1`, `ge0/2`, and so on). Invalid input returns a CLI error.

## Project format

Project files are UTF-8 JSON objects with `formatVersion: 1`, `name`, `devices`, `links`, objectives, IoT rules, and annotations. Device records include stable IDs, positions, groups, IPv4/IPv6 gateway, interfaces, static/dynamic route configuration, service records, wireless settings, ACL/NAT settings, IoT telemetry/actuator state, and learned MAC state. Interfaces include IPv4/IPv6 prefixes, medium, VLAN/access/trunk state, STP, port security, and bundle membership. Links include interface endpoints, cable metadata, and operational state. Saving uses a temporary file followed by replacement; older version-1 files receive defaults for newly introduced fields.

## Keyboard shortcuts

`Ctrl+N` new, `Ctrl+O` open, `Ctrl+S` save, `Ctrl+Z` undo, `Ctrl+Y` redo, `Ctrl+D` duplicate selected, `Ctrl+P` IPv4 ping, `Ctrl`+click multi-select, `Delete` remove selected, `Escape` cancel placement/connection, `F1` Help Center, `Space` pause/resume simulation.

## Architecture

- `src/Core/NetworkModels.cs`: serializable topology and device, service, wireless, IoT, policy, routing, and activity models.
- `src/Core/NetworkSimulator.cs` and `src/Core/Ipv6NetworkSimulator.cs`: IPv4 and ICMPv6 path/event simulation.
- `src/Core/NetworkServiceEngine.cs`: DHCP/DNS/HTTP/HTTPS/FTP and TCP/UDP event flows, ACL/NAT checks, and service content.
- `src/Core/SwitchingEngine.cs` and `src/Core/SpanningTreeEngine.cs`: VLAN-aware forwarding, MAC learning/security, bundles, and automatic loop blocking.
- `src/Core/RouteEngine.cs`, `src/Core/DynamicRoutingEngine.cs`, `src/Core/Ipv6RouteEngine.cs`, and `src/Core/RoutingPathValidator.cs`: connected/static/dynamic route lookup and path validation.
- `src/Core/WirelessEngine.cs`, `src/Core/IotEngine.cs`, and `src/Core/LabEvaluationEngine.cs`: wireless association, IoT automation, and objective scoring.
- `src/Core/CliSession.cs`: command parsing and traffic requests against shared device state.
- `src/Core/DiscordIpcProtocol.cs` and `src/Core/DiscordPresenceService.cs`: Discord local IPC framing, reconnect, and activity updates.
- `src/Core/MultiplayerSession.cs`: direct TCP host/client sessions, authoritative snapshots, peer lists, lock leases, and reload/disconnect handling.
- `src/Help/*.md`: editable Markdown articles rendered in Help Center tabs.
- `src/DiscordPresenceSettingsWindow.xaml`: Discord Application ID settings UI.
- `src/HelpCenterWindow.xaml`: tabbed in-app operating guides.
- `src/Core/ProjectFileService.cs`: versioned project persistence.
- `src/MainWindow.xaml` and `src/MainWindow.xaml.cs`: WPF workspace and interaction wiring.
- `tests/`: executable smoke checks for Markdown, IPC frames, services, routing, switching, wireless, IoT, persistence, and activities.
