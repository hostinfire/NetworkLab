# Device configuration

Select a device to open its **Inspector**. Choose an interface, then edit its name, IPv4 address, subnet mask, VLAN, administrative state, and the device default gateway. **Apply configuration** validates IPv4 syntax, subnet-mask syntax, and VLAN range.

A physical link is operational only while the link exists and both interfaces are enabled. Interface state shows MAC address, speed, duplex, and current up/down state. Multiport switches and routers expose `ge0/1`-style interface names; endpoints use `eth0`.

The Inspector also configures IPv6 addresses, interface access/trunk mode, allowed/native VLANs, STP automatic/forwarding/blocking state, port-security limits, EtherChannel group labels, RIP/OSPF/BGP educational settings, wireless SSID/security/range, ACL rules, and source NAT. Route changes affect the shared simulator model.

The library includes endpoint, network, and IoT device categories. Laptop, smartphone, tablet, wireless router, and access-point types have a wireless interface. IoT sensors and actuators expose state to automation rules. Server DHCP, DNS, HTTP, HTTPS, and FTP services are enabled and edited in the **Server Services** section of the Inspector.