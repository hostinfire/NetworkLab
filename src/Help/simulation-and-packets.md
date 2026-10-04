# Read a packet sequence

A request creates an ordered educational event sequence for IPv4 ARP/ICMP or IPv6 neighbor discovery/ICMPv6, VLAN-aware Ethernet forwarding, MAC learning, route lookup, and return forwarding. DHCP assigns a lease; DNS resolves configured records; HTTP/HTTPS and FTP return configured service data; TCP requests show a simplified handshake/teardown; UDP carries datagrams. ACLs can permit/deny by protocol/address/port, and source NAT rewrites event addresses for matching router interfaces.

- **Play** advances at the selected rate.
- **Pause** holds the current event.
- **Step** advances once.
- **Restart** returns to the first event in the latest sequence.

Selecting an event shows its timestamp, device, action, packet addresses, interfaces, TTL, VLAN, and a simplified OSI view.

IoT sensor changes can trigger configured actuator rules. Lab objectives compare device settings against expected values and award points.

> This is a deterministic educational event model, not a bit-accurate packet emulator. TCP sequencing, retransmission, sockets, DNS wire format, DHCP timers/relay, TLS cryptography, and protocol timers are not modeled. Events are produced from topology/configuration when an action runs; timeline controls replay those results for inspection.