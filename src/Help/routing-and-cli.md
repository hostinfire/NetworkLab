# Configure a device from the CLI

Select a device before using the **CLI** tab. Commands update the same device and interface objects used by the Inspector.

## Configure an interface

```text
enable
configure terminal
interface ge0/1
ip address 10.0.0.1 255.255.255.0
no shutdown
```

Use `exit` to leave interface or configuration mode.

## Static routes

Add a route with `ip route <network> <mask> <next-hop> [interface]`:

```text
ip route 10.0.2.0 255.255.255.0 10.0.12.2 ge0/2
```

Remove one with `no ip route <network> <mask> <next-hop>`. Routes are selected by longest matching prefix; connected interfaces are included automatically.

## Diagnostics

Use `show ip route`, `show interfaces`, `show running-config`, `show vlan`, `show mac-address-table`, and `show neighbors`.

Cross-subnet ping requires a valid endpoint gateway, a route on each router in both directions, and route egress interfaces matching the physical path.

## Dynamic routing

Configure `router rip` or `router ospf`, then optionally use `network <IPv4-network>` to filter advertised connected networks. Route learning is recalculated when traffic runs and is intended for small educational topologies.

Directly connected BGP peers can be enabled with `router bgp <AS-number>` and `neighbor <peer-IPv4>`. BGP currently advertises directly connected peer networks only; it does not model full sessions, path selection, policies, timers, or multi-hop propagation.

```text
router rip
network 10.0.0.0
router ospf
network 10.0.0.0
router bgp 65001
neighbor 10.0.12.2
```

## IPv6

Configure `ipv6 address 2001:db8:1::10/64` under an interface. Add an IPv6 static route with `ipv6 route 2001:db8:2::/64 2001:db8:1::1 ge0/1`, inspect with `show ipv6 route`, and test with `ping ipv6 2001:db8:2::20`.

## Application service commands

```text
dhcp
nslookup lab.example
http get 192.168.10.20
https get 192.168.10.20
ftp list 192.168.10.20
ftp get 192.168.10.20 readme.txt
tcp send 192.168.10.20 8080 hello
udp send 192.168.10.20 9000 hello
```

These commands use the same server configurations and service engine as the Simulation menu.