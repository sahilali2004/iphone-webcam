# USB transport

## Mechanism

Apple devices don't expose a network interface over USB. Instead, Apple's **usbmuxd** multiplexes TCP-like connections over the USB cable. On Windows, usbmuxd is part of **Apple Mobile Device Service** (`AppleMobileDeviceService.exe`), installed by the Microsoft Store *Apple Devices* app or by iTunes. It listens on **`127.0.0.1:27015`** (verified on the dev machine).

The iOS app needs nothing USB-specific: it just listens on TCP port 47809. usbmuxd delivers host connections to that port.

```text
Windows app ──TCP──► 127.0.0.1:27015 (usbmuxd) ══USB══► iPhone :47809 (NWListener)
```

## usbmuxd client protocol (plist variant)

Header (16 bytes, little-endian): `u32 length (incl. header) · u32 version = 1 · u32 message = 8 (plist) · u32 tag`, followed by an XML plist.

| Request `MessageType` | Extra keys | Response |
|---|---|---|
| `Listen` | — | `Result {Number:0}`, then a stream of `Attached {DeviceID, Properties{ConnectionType, SerialNumber, ProductID…}}` / `Detached {DeviceID}` |
| `ListDevices` | — | `{DeviceList:[{DeviceID, Properties}]}` |
| `Connect` | `DeviceID`, `PortNumber` = port **byte-swapped to network order** (47809 → `htons`) | `Result {Number:0}` → socket is now a raw pipe to the device port. Non-zero = refused (3 = connection refused, i.e. iOS app not listening). |

Every request also carries `ClientVersionString`, `ProgName`, `kLibUSBMuxVersion = 3`.

Rules:
- Use **two** sockets: one long-lived `Listen` socket for hot-plug events, one fresh socket per `Connect`.
- Filter `Properties.ConnectionType == "USB"` — usbmuxd also reports Wi-Fi-sync devices (`"Network"`), which we handle via our own Wi-Fi path instead.
- `SerialNumber` (UDID) is used only locally to remember "this USB device = that paired iPhone"; never transmitted elsewhere.

Exact key spellings will be checked against libimobiledevice's `libusbmuxd` source in Phase 7 and captured as a test fixture.

## User-visible requirements

1. Apple Devices app (or iTunes) installed. Detection: TCP connect to `127.0.0.1:27015` fails → *"USB needs Apple Devices — install from Microsoft Store. Wi-Fi still works."*
2. iPhone unlocked and *Trust This Computer* accepted once.
3. iPhone Webcam app open in foreground. `Connect` result 3 → *"iPhone found over USB, but the iPhone Webcam app isn't open."*

## Rejected alternatives

See [architecture.md §3.2](architecture.md) — USB tethering (needs carrier hotspot), MFi/External Accessory (hardware certification), custom WinUSB driver (unsafe, replaces Apple's driver).
