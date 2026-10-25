# Android app (Phone Webcam)

Same wire protocol, pairing and port (47809) as the iPhone app, so the Windows side is unchanged.
Built and installed from this PC with Android Studio's SDK. No cloud build, no signing account.

## Build & install (USB)

1. Device: Settings › About › tap *Build number* 7× → Developer options → enable **USB debugging**. Plug in, tap **Allow**.
2. `.\scripts\android-install.ps1`: builds, installs and launches the app.
3. On the device: **Start camera** (allow Camera permission).

## Connect

- **USB:** `.\scripts\android-usb.ps1` (runs `adb forward tcp:47809 tcp:47809`), then connect the Windows app to `127.0.0.1`. Re-run after replugging.
- **Wi-Fi:** connect the Windows app to the address shown on the device.
- First connection shows a 6-digit code on the device; type it on the PC.

## Measured (Galaxy Tab A 10.1 SM-T510, Android 11, USB, 2026-10-25)

| Setting | FPS | Capture→PC latency | Bitrate | Dropped |
|---|---|---|---|---|
| Rear 1080p | 30.0 | 84 ms | 12.8 Mbps | 0 |
| Rear 720p | 30.0 | 74 ms | 5.4 Mbps | 0 |
| Front 720p | 30.2 | 79 ms | 17.4 Mbps | 0 |

JPEG encode (YuvImage, CPU) takes ~27 ms at 1080p on this tablet, so encode of frame N+1 overlaps the send of frame N (FrameGate allows 2 in flight).

## Code map (`android/app/src/main/java/local/phonewebcam`)

| Package | Contents |
|---|---|
| `protocol/` | Wire format + pairing crypto (unit-tested against `tests/protocol-vectors`) |
| `net/` | `StreamServer` / `ClientSession` (TCP, auth, keepalive), `FrameGate`, `PairingStore` |
| `camera/` | CameraX binding (`CameraController`), YUV→JPEG (`JpegEncoder`) |
| `ui/` | `MainActivity` (screen + coordinator), viewfinder overlay |
