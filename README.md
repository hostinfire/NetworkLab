# Northstar Network Lab

Northstar Network Lab is an independent Windows desktop application for building and exploring educational network topologies. The application uses C# and WPF on .NET 8. Its interface is separated from the serializable network model and deterministic simulation engines.

> **Important:** This is an educational simulator, not a packet-accurate implementation or a replacement for testing real network equipment.

## Contents

- [Features](#features)
- [Run from source](#run-from-source)
- [Quick start](#quick-start)
- [CLI](#cli)
- [Multiplayer](#multiplayer)
- [Discord Rich Presence](#discord-rich-presence)
- [Project files](#project-files)
- [Architecture](#architecture)
- [Tests](#tests)
- [Simulation scope](#simulation-scope)
- [Privacy and licensing](#privacy-and-licensing)

## Features

### Topology workspace

- Place endpoint, network, and IoT device models on a scrollable, zoomable canvas.
- Move devices with grid snapping; Ctrl-click to multi-select and move groups together.
- Duplicate and delete selected devices, assign group names, search by device name/type/IP, and add editable canvas annotations.
- Connect available interfaces and select Ethernet, fiber, or serial cable metadata.
- Configure IPv4/IPv6, interfaces, gateways, VLAN modes, wireless, routing, services, ACL, NAT, and device state in the Inspector.
- Use undo/redo, light/dark workspace themes, and resizable panels.

### Simulated network behavior

- IPv4 ICMP ping with ARP, event inspection, switch learning/forwarding, routing lookups, and return-path events.
- IPv6 ICMPv6 echo with neighbor-discovery events and connected/static IPv6 route lookup.
- Server services: DHCP address leases, DNS A-record resolution, HTTP/HTTPS content, and FTP `LIST`/`GET`.
- Simulated TCP handshake/application/teardown events and UDP datagrams.
- Connected/static IPv4 routing, deterministic educational RIP/OSPF route propagation, and directly adjacent BGP advertisements.
- VLAN-aware access/trunk admission, MAC learning and aging, known-unicast forwarding, unknown-unicast flooding, port security, EtherChannel member egress, and automatic spanning-tree loop blocking.
- ACL permit/deny checks and source NAT address translation events.
- Wireless client association using configured SSID, passphrase, and a canvas-distance range estimate.
- IoT sensor values, threshold automation rules, actuator state, and a basic status dashboard.
- Configuration objectives with score calculation and on-demand hints.

Simulation events can be played, paused, stepped, restarted, and inspected in the event and packet panels. The event model is calculated from the topology and configuration; timeline controls replay generated events rather than advancing a full live packet-processing kernel.

## Run from source

Prerequisites: Windows x64 and the .NET 8 SDK.

From the repository root:

```powershell
dotnet run --project src\src.csproj
```

The source project restores Markdig and packages the Markdown help pages automatically. A sample project is included at [examples/first-lan.nslab](examples/first-lan.nslab).

## Quick start

1. Start the application and add two PCs and a switch from the Device Library.
2. Choose **Connect**, select the first PC, and then select the switch. Repeat for the second PC.
3. Select a PC and choose **Ping**. Enter the other PC's IPv4 address.
4. Open **Simulation events** and use **Step** or **Play** to inspect the simulated path. Select an event to view packet details.
5. Use **File > Save As** to save the topology as a `.nslab` file.

The in-app guides are available from **Help > Help Center** or `F1`.

## CLI

Select a device before issuing commands. CLI changes apply to the same device model used by the Inspector. Common commands include:

```text
enable
configure terminal
interface eth0
ip address 192.168.10.10 255.255.255.0
ipv6 address 2001:db8:1::10/64
no shutdown
show interfaces
show ip route
show ipv6 route
ip route 10.0.2.0 255.255.255.0 10.0.12.2 ge0/2
router rip
router ospf
router bgp 65001
neighbor 10.0.12.2
network 10.0.2.0
ping 192.168.10.20
ping ipv6 2001:db8:1::20
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
```

Single-interface endpoints use `eth0`; multiport devices use names such as `ge0/1`. The CLI intentionally implements an educational subset rather than a vendor command language.

## Multiplayer

The **Multiplayer** tab supports direct TCP host/client sessions. **Begin sharing** starts a host on the selected port (default `47831`) and displays local IP addresses. A peer selects **Connect** and enters the host IP, port, and a display name. The host is authoritative and sends project snapshots; client changes require device or topology edit locks. Objects held by another peer are gray and read-only.

**Reload project** requests a fresh host snapshot, **Disconnect** leaves the session, and **Restart app** launches a new process if the UI needs recovery.

**Security warning:** Multiplayer has no encryption, peer authentication, relay, or automatic NAT traversal. Anyone who can reach the host port may attempt to connect. Use only on a trusted LAN or VPN, restrict inbound firewall access, and never forward the port from a public internet router. See [the multiplayer guide](src/Help/multiplayer.md).

## Discord Rich Presence

Presence is optional. Open **Tools > Discord Rich Presence**, create a Discord application, and enter its numeric Application ID. Discord Desktop must be running. Northstar sends activity text (project name, selected device, or simulation action) to the local Discord IPC client. It does not intentionally send project files or topology addresses through Rich Presence. Discord's own policies govern its handling of activity data.

## Project files

`.nslab` files are UTF-8 JSON with `formatVersion: 1`. They store project name, device configurations and positions, interfaces, links, routes, services, wireless configuration, ACL/NAT, IoT state/rules, objectives, groups, and annotations. The loader supplies defaults for fields added to existing version-1 files. Saves use a temporary file followed by replacement.

The app periodically writes a recovery project under `%LocalAppData%\NorthstarNetworkLab\autosave.nslab` and offers recovery at startup. User projects and preferences are local unless the user explicitly shares a project through multiplayer.

## Architecture

| Area | Source |
| --- | --- |
| WPF application and workspace | `src/MainWindow.xaml`, `src/MainWindow.xaml.cs` |
| Serializable device/project model and IPv4 utilities | `src/Core/NetworkModels.cs` |
| IPv4 and IPv6 ICMP simulation | `src/Core/NetworkSimulator.cs`, `src/Core/Ipv6NetworkSimulator.cs` |
| DHCP, DNS, HTTP(S), FTP, TCP/UDP events, ACL and NAT | `src/Core/NetworkServiceEngine.cs` |
| VLAN forwarding, MAC learning, security, and EtherChannel | `src/Core/SwitchingEngine.cs` |
| Automatic spanning-tree loop blocking | `src/Core/SpanningTreeEngine.cs` |
| Connected/static/dynamic route calculation | `src/Core/RouteEngine.cs`, `src/Core/DynamicRoutingEngine.cs`, `src/Core/Ipv6RouteEngine.cs`, `src/Core/RoutingPathValidator.cs` |
| Wi-Fi association, IoT rules, and objective scoring | `src/Core/WirelessEngine.cs`, `src/Core/IotEngine.cs`, `src/Core/LabEvaluationEngine.cs` |
| CLI and project persistence | `src/Core/CliSession.cs`, `src/Core/ProjectFileService.cs` |
| Peer hosting, snapshots, and edit locks | `src/Core/MultiplayerSession.cs` |
| Discord local IPC | `src/Core/DiscordIpcProtocol.cs`, `src/Core/DiscordPresenceService.cs` |
| In-app Markdown documentation | `src/Help/*.md`, `src/HelpCenterWindow.xaml` |

## Tests

Run the executable smoke suite with:

```powershell
dotnet run --project tests\Simulator.SmokeTests.csproj --configuration Release
```

The suite covers address validation, routing, service flows, switching, IPv6, wireless, IoT, objectives, Markdown rendering, Discord IPC framing, persistence, and a two-client localhost multiplayer session with lock contention and reload.

## Simulation scope and limitations

This project is an educational event simulator, not a standards-complete network stack. RIP/OSPF use deterministic route propagation; BGP supports directly adjacent educational advertisements, not full BGP sessions or transit policy. TCP handshake/teardown and TLS events are illustrative; TCP sequencing, retransmission, sockets, and TLS cryptography are not implemented. DHCP relay/timers, DNS caching and record families, NAT port allocation/collision handling, LACP negotiation, spanning-tree root election/timers, WPA cryptography/radio interference, IoT cloud services, hidden/protected lab answers, instructor roster management, and multi-user collaboration tools are outside the current scope. Cable type is saved as topology metadata.

## Privacy, license, and distribution

The app has no Provider-operated cloud service, account system, or analytics/telemetry in the current source. Projects/autosave are local by default. Optional Discord presence sends activity labels to the local Discord client. Multiplayer sends project snapshots and edits over direct, unencrypted TCP to connected peers.

The project license terms are in [LICENSE.md](LICENSE.md). They are a general-purpose template, not legal advice; obtain qualified, jurisdiction-specific review and add provider contact information before public distribution.
