# Wi-Fi transport

- iPhone: `NWListener` (TCP, port 47809) with Bonjour service `_iphonewebcam._tcp` (TXT `id`, `name`, `proto`).
- Info.plist: `NSLocalNetworkUsageDescription`, `NSBonjourServices = ["_iphonewebcam._tcp"]`, `NSCameraUsageDescription`.
- PC: browses `_iphonewebcam._tcp.local` via mDNS. API to be confirmed in Phase 4 (`DnsServiceBrowse` from `windns.h`, or WinRT `DeviceWatcher` with DNS-SD association endpoints). Manual `IP:port` entry is always offered as fallback.
- PC is the client, so no inbound Windows Firewall rule is needed.
- Only private / link-local peer addresses are accepted on either side.

## Troubleshooting matrix

| Symptom | Likely cause |
|---|---|
| iPhone never appears | Different subnet, guest network, or AP/client isolation; iOS Local Network permission denied |
| Appears, connect times out | iPhone app backgrounded/locked; VPN on PC or iPhone |
| Stutters at 1080p | 2.4 GHz link or weak signal → use 720p, lower quality, USB, or H.264 (later) |
