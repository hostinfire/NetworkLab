# Build your first LAN

1. Choose a **PC**, **switch**, and **server** from the Device Library. Click the canvas to place each device. End devices receive example IPv4 addresses in `192.168.10.0/24`.
2. Select a device and choose **Connect**. Click the other endpoint to create a physical link. Repeat to connect both endpoints to the switch.
3. Select the source PC and choose **Ping**. Enter the server address. The result appears in the status bar and as an ordered event sequence.
4. Open **Simulation events**. Use **Step** to inspect one event at a time, or **Play** to follow the sequence. Select an event to inspect its packet fields.

## Example project

Open [`examples/first-lan.nslab`](../../examples/first-lan.nslab) from **File > Open** for a ready-to-run topology.