# Implemented behavior and limits

## Implemented core

IPv4 subnet/mask and IPv6 prefix checks; versioned persistence; active wired/wireless path discovery; access/trunk VLAN admission; allowed VLAN filtering; automatic spanning-tree loop blocking; MAC learning, flooding, known-unicast forwarding, port security, and bundle egress; connected/static IPv4 routes; educational RIP/OSPF propagation and directly adjacent BGP advertisements; NAT and address/port ACL checks; IPv6 static routes and ICMPv6 neighbor/echo events; simulated TCP/UDP events; DHCP leases; DNS records; HTTP/HTTPS content; FTP LIST/GET; wireless association/authentication/range; IoT sensor-triggered actuator rules; scored configuration objectives; multi-selection, groups, annotations, and cable-type metadata.

## Not yet modeled

Wire-level frame/packet serialization, actual TCP sequence/acknowledgement/retransmission state, TLS cryptography, DHCP relay and timers, DNS record types/caching, OSPF hello/LSA/SPF protocol packets, RIP timers/loop prevention, BGP sessions/policies/transit AS path propagation, NAT port-allocation/collision handling, dynamic STP root election/timers, LACP negotiation, WPA cryptography, radio interference, IoT dashboards/cloud services, plugin loading, protected hidden lab answers, instructor authoring/roster management, and multi-user collaboration. Current protocol engines use deterministic educational rules; their labels do not imply standards-complete behavior.