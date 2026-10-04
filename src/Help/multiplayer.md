# Multiplayer topology sharing

Multiplayer shares one authoritative project over a direct TCP connection. One person hosts; other peers connect to the host's reachable IP address and port.

## Begin sharing

1. Open the **Multiplayer** tab beside **Workspace**.
2. Choose **Begin sharing**, enter a display name and TCP port (default `47831`).
3. Share the host IP shown in the tab and the port with peers. Allow inbound TCP for this port through the host firewall/router as needed.

Peers must be able to route to the host address. The app does not provide a relay, account directory, encryption, authentication, or automatic NAT traversal. Use trusted networks only; do not expose a session to the public internet.

## Join a session

Choose **Connect** and enter the host IP, port, and your display name. The host sends the current project snapshot. Updates are sent to the host, validated, and rebroadcast to connected peers.

## Edit locks

Selecting a device requests an exclusive device lock. Other peers see that device dimmed and cannot select, configure, move, or delete it. Deselect the device to release the lock. Topology-wide changes (links, device creation/deletion, groups, annotations, objectives, wireless links, and project replacement) acquire an exclusive topology lock; other peers cannot edit objects while that lock is held.

Lock ownership is released when a peer disconnects. A peer that exits unexpectedly is removed when the TCP connection closes.

## Recovery controls

- **Reload project** requests a fresh authoritative snapshot from the host. On the host, it reloads the current host copy into the workspace.
- **Disconnect** leaves the shared session without closing Northstar.
- **Restart app** starts a fresh application process and closes the current one. Save first if local changes have not synced.

The multiplayer protocol currently sends project snapshots and lock messages as JSON over TCP. It is intended for classroom/LAN use and is not a secure collaboration service.