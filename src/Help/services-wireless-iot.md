# Services, wireless, and IoT

## Server services

Select a device and configure **Server Services** in the Inspector. Enable DHCP, DNS, HTTP, HTTPS, or FTP. Set DHCP pool endpoints/mask/gateway/DNS; enter DNS records as `name=IPv4` lines; set HTTP response text; and define FTP files as `filename=contents` lines. Apply settings before sending client requests.

From **Simulation**, request a DHCP lease, resolve a DNS name, request HTTP/HTTPS, list FTP files, or send TCP/UDP data. Devices must have an active topology path. DHCP discovery is local-link only; relay is not available.

## Wireless

Enable a radio on an Access Point or Wireless Router in the Inspector, set its SSID, security mode, passphrase, and range. Select a Laptop, Smartphone, or Tablet and choose **Tools > Associate selected client to Wi-Fi**; enter the AP device name and passphrase. Position distance is interpreted as 10 canvas units per meter. The association creates a wireless topology link, so the normal services can use that path. This is an educational credential/range check, not 802.11 radio or WPA cryptography.

## IoT automation

Select a temperature or motion sensor and choose **Tools > Set IoT sensor value**. Add a rule with **Tools > Add IoT automation rule** by selecting a sensor/property, comparison, threshold, actuator, and on/off action. A matching reading updates the actuator. Select a Fan, Smart Light, Door, or Smart Appliance and choose **Toggle IoT actuator** to change it directly. **IoT dashboard** lists current sensor values, actuator states, and rules.

## Lab objectives

Select a device/interface and use **Tools > Create lab objective** to define expected IPv4 address, mask, and gateway. **Evaluate lab** displays completion and score. **Show objective hint** reveals the configured hint on request; evaluation does not automatically expose it.