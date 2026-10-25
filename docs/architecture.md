# Architecture

Status: **Phase 1 — accepted** (2026-10-25). Decisions below are the baseline; changes get recorded in the Decision Log at the bottom.

## 1. Summary

```text
 iPhone (Swift / SwiftUI)                         Windows 11 PC
 ─────────────────────────                        ─────────────────────────────────────────────────────
 AVCaptureSession                                 IPhoneWebcam.App  (C# / .NET / WPF, runs as user)
   └ AVCaptureVideoDataOutput (420f/NV12)           ├ DeviceManager   (USB watcher + Wi-Fi discovery)
       └ FrameEncoder (JPEG via VideoToolbox)       ├ Transport       (ITransport → UsbTransport | WifiTransport)
           └ StreamServer (NWListener, TCP)  ◄──────┤ ProtocolClient  (control + video framing)
               ▲   advertised via Bonjour           ├ VideoDecoder    (IVideoDecoder → Mjpeg | later H264)
               │                                    ├ FramePipeline   (latest-frame slot, scale, NV12)
               │   USB: usbmuxd tunnel              └ FramePublisher  ─── named pipe ───┐
               │   Wi-Fi: direct TCP on LAN                                              ▼
                                                  IPhoneWebcamSource.dll (C++, COM media source)
                                                    loaded by Windows Camera Frame Server (svchost)
                                                    registered as virtual camera via MFCreateVirtualCamera
                                                         │
                                       Chrome · Teams · Zoom · OBS · Windows Camera · Discord
```

Key properties:

- **The iPhone is always the TCP server; the PC is always the client.** This is forced by USB (usbmuxd only lets the host open connections *to* a port on the device) and we reuse it for Wi-Fi so both transports produce an identical byte stream. Everything above `ITransport` is transport-agnostic.
- **One TCP connection per session**, framed binary protocol. Control messages and video frames are distinct message types with separate headers (see [protocol.md](protocol.md)). Video is never JSON.
- **V1 codec is MJPEG** (each frame an independent JPEG). The protocol carries a codec ID so H.264 can be added without changing transports.
- **The virtual camera is a user-mode Media Foundation media source** registered with `MFCreateVirtualCamera`. No kernel driver.
- **Windows 11 (build 22000+) only** for V1. See §4.3 for why Windows 10 is deferred.

## 2. Requirements analysis — what is actually hard

| Component | Difficulty | Why |
|---|---|---|
| Virtual camera media source | **Highest** | Runs inside the Frame Server service (another process, another session, LOCAL SERVICE account). Must implement `IMFMediaSource`/`IMFMediaSourceEx`/`IMFMediaStream`/`IKsControl` correctly or apps hang/crash. Hard to debug (ETW tracing, no console). Must get frames across a session/privilege boundary. |
| USB transport | Medium | No public Apple SDK for Windows. Must speak usbmuxd's plist protocol to Apple Mobile Device Service. Well-understood (libimobiledevice, Camo, EpocCam, iVCam all do this), but undocumented by Apple. |
| Building the iOS app from Windows | **Process risk** | Xcode only runs on macOS. See §7 — needs user decision. |
| Latency / backpressure | Medium | Must drop frames rather than queue them, on both ends. |
| Wi-Fi discovery | Low | Bonjour on iOS is first-class; Windows needs mDNS browse + manual-IP fallback. |
| UI | Low | Two small utility screens. |

## 3. Research findings (verified against current docs)

### 3.1 Windows virtual camera options

| Option | Visible to | Status | Verdict |
|---|---|---|---|
| **MF virtual camera — `MFCreateVirtualCamera` + custom media source** | All MF apps (Windows Camera, Teams, Chrome/Edge, Zoom, OBS via MF/DShow bridge) — Frame Server also exposes it to DirectShow apps | Official, Windows Build 22000+ (`mfvirtualcamera.h`, `mfsensorgroup.lib`) | **Chosen** |
| DirectShow source filter (what OBS VirtualCam / old tools do) | DirectShow apps only. **Not** visible to Windows Camera app or other MF-only apps | Legacy, works on Win10 | Deferred — only option for Win10 |
| AVStream kernel driver (e.g. Microsoft `avshws` sample) | Everything | Requires kernel driver + signing (EV cert / attestation) | Rejected — unnecessary risk; MF virtual camera exists precisely to avoid this |
| Camera-profile / sensor-group tricks | — | Not designed for this | Rejected |

Verified facts about `MFCreateVirtualCamera` ([Microsoft Learn](https://learn.microsoft.com/en-us/windows/win32/api/mfvirtualcamera/nf-mfvirtualcamera-mfcreatevirtualcamera)):

- Minimum client: **Windows Build 22000** (Windows 11). Only `MFVirtualCameraType_SoftwareCameraSource` supported.
- `MFVirtualCameraLifetime_Session`: camera disappears when the `IMFVirtualCamera` is released / `Shutdown()`. `MFVirtualCameraLifetime_System`: persists across reboots.
- `MFVirtualCameraAccess_CurrentUser` works without admin. `AllUsers` requires admin.
- Windows **appends "Windows Virtual Camera"** to the friendly name. We pass `iPhone Webcam`; apps will show something like *"iPhone Webcam (Windows Virtual Camera)"*. This cannot be suppressed via this API.
- `sourceId` is the CLSID of our COM media source.
- Subject to the camera privacy toggle (returns `E_ACCESSDENIED` if camera access is denied for desktop apps).

Verified facts from the reference implementation [smourier/VCamSample](https://github.com/smourier/VCamSample):

- The media source DLL must be **registered in HKLM** (`regsvr32` as admin, once). HKCU registration does not work because the Frame Server (LOCAL SERVICE) and Frame Server Monitor (LOCAL SYSTEM) load it.
- The DLL must live in a folder readable by those service accounts — **not under the user profile**, otherwise `IMFVirtualCamera::Start` fails with access denied. → install to `C:\Program Files\iPhoneWebcam\`.
- Known issue: in some Teams builds the virtual camera previews locally but is not sent to the far end. Track as a known risk; test explicitly in Phase 5.

### 3.2 iPhone ↔ Windows over USB

| Option | Works for a normal developer? | Notes |
|---|---|---|
| **usbmuxd via Apple Mobile Device Service (AMDS)** | **Yes** | AMDS ships with iTunes or the Microsoft Store *Apple Devices* app. Listens on `127.0.0.1:27015`. Client sends plist `Listen` (device attach/detach events) and `Connect {DeviceID, PortNumber}`; on success the socket becomes a raw TCP pipe to a port the iOS app listens on. This is what Camo/EpocCam/iVCam/libimobiledevice use. Requires the user to tap *Trust This Computer* once. |
| USB tethering (Personal Hotspot over USB) | Partially | Gives an IP link over USB, but requires a cellular plan that allows Personal Hotspot, must be toggled on, and changes PC routing. Not reliable as a primary path. |
| External Accessory / MFi | No | Requires MFi hardware certification. |
| Custom USB driver (WinUSB/libusb talking to the iPhone's usbmux interface directly) | Technically | Reimplements usbmuxd + needs pairing records; replaces Apple's driver binding. Unsafe and unnecessary. |

**Verified on this machine:** `Apple Mobile Device Service` is installed and running, `AppleMobileDeviceService.exe` is listening on `127.0.0.1:27015`.

**Dependency (user-visible):** USB mode requires *Apple Devices* (Microsoft Store) or *iTunes* to be installed. The Windows app detects port 27015 and shows a clear message if it is missing. Wi-Fi mode has no such dependency.

### 3.3 Wi-Fi

- iOS: `Network.framework` `NWListener` on TCP, advertised as Bonjour service `_iphonewebcam._tcp` with TXT record (`id`, `name`, `proto`). Requires `NSLocalNetworkUsageDescription` + `NSBonjourServices` in Info.plist (iOS 14+ local network permission).
- Windows discovery: mDNS browse. Candidate APIs: Win32 `DnsServiceBrowse` (`windns.h`) or WinRT `DeviceWatcher` with the DNS-SD association-endpoint protocol. **To be verified in Phase 4** — fallback is manual IP:port, which is always available.

### 3.4 USB vs Wi-Fi comparison

| | USB (usbmuxd) | Wi-Fi (LAN TCP) |
|---|---|---|
| Latency | Lowest, steady (USB 2.0 ≈ 30–35 MB/s usable) | +5–30 ms, jittery; spikes on congested 2.4 GHz |
| Reliability | High; immune to RF; phone charges | Depends on router, AP isolation, power save |
| Bandwidth headroom | Large — MJPEG 1080p30 (~25–60 Mbps) fits easily | MJPEG 1080p30 is borderline on weak links → H.264 later |
| Implementation | usbmuxd client (~300 lines) | Trivial TCP + discovery |
| Permissions | *Trust This Computer* on iPhone | iOS Local Network permission; Windows Firewall not involved (PC is client) |
| Dependencies | Apple Devices app or iTunes on PC | None |
| Compatibility | All iPhones w/ Lightning or USB-C | Same subnet required; fails on guest/isolated Wi-Fi |

Build order follows the spec: Wi-Fi first (simplest to debug), USB second — but because both deliver a TCP byte stream to the same port, USB is a small add-on rather than a parallel stack.

### 3.5 Codec

| | Raw NV12 | **MJPEG (V1)** | H.264 (later) |
|---|---|---|---|
| 1080p30 bitrate | ~750 Mbps | ~25–60 Mbps (q≈0.7) | ~6–12 Mbps |
| Fits USB 2.0 | No | Yes | Yes |
| Fits Wi-Fi | No | Usually (5 GHz), marginal on 2.4 GHz | Yes |
| Encode (iPhone) | none | HW JPEG via VideoToolbox, ~2–4 ms | HW, ~5–10 ms realtime mode |
| Decode (PC) | none | WIC / libjpeg-turbo, ~4–8 ms/frame CPU | MF H.264 decoder (HW), output NV12 |
| Loss / reconnect | — | Every frame independent, zero recovery logic | Needs SPS/PPS, IDR on (re)connect, decoder flush |
| Debuggability | — | Dump any frame to `.jpg` | Needs tooling |

Decision: **MJPEG for V1.** Simplest correct pipeline, no inter-frame state, ideal for the primary USB path. **H.264 is planned** (Phase 8+) for Wi-Fi bandwidth; the protocol already carries `codec` and `flags.keyframe`, and the Windows side decodes behind `IVideoDecoder`.

## 4. Component design

### 4.1 iOS app (`ios/iPhoneWebcam`)

| Module | Responsibility |
|---|---|
| `CameraManager` | `AVCaptureSession`, front/rear switch, 720p/1080p preset, 30 fps via `activeVideoMin/MaxFrameDuration`, torch, continuous AF/AE + tap-to-focus. Output `kCVPixelFormatType_420YpCbCr8BiPlanarFullRange`. `alwaysDiscardsLateVideoFrames = true`. Video orientation fixed to landscape-right on the connection (rotation later). |
| `FrameEncoder` (protocol) / `JpegEncoder` | `VTCompressionSession` with `kCMVideoCodecType_JPEG` (hardware). Future `H264Encoder` behind same protocol. |
| `StreamServer` | `NWListener` TCP port 47809 + Bonjour advert. Accepts one client. Latest-frame-wins send queue (never more than 1 frame in flight). |
| `ProtocolCodec` | Header encode/decode, control message JSON, frame headers. |
| `SessionController` | State machine: idle → listening → handshaking → authorized → streaming. Pairing. |
| UI (SwiftUI) | Preview (`AVCaptureVideoPreviewLayer` — preview only, never the frame source), controls, status. `isIdleTimerDisabled = true` while streaming. |

**iOS platform limits (not bugs):** the camera stops when the app is backgrounded or the phone locks (iOS restricts camera access to foreground apps; multitasking camera access is iPad-only / special entitlement). We show *"Paused — iPhone app in background"* on the PC and resume automatically when the app returns.

### 4.2 Windows companion app (`windows/`)

Language split:

- **C++ (`windows/VirtualCamera`)** — the COM media source DLL. Must be native: it is loaded into `svchost.exe` (Frame Server). Also exports a tiny flat C API (`vcam_register`, `vcam_start`, `vcam_stop`, `vcam_remove`) wrapping `MFCreateVirtualCamera`, so the app doesn't need COM interop for that.
- **C# / .NET (WPF) (`windows/App`, `windows/Core`)** — UI, networking, protocol, decoding, frame pipeline. WPF on .NET 9+ has the Fluent theme with `ThemeMode="System"` → follows Windows dark/light for free. Networking and JPEG decode are straightforward and safe in managed code. No React/web UI.

| Module (C#) | Responsibility |
|---|---|
| `Transport/ITransport` | `Task<Stream> ConnectAsync(DeviceEndpoint, CancellationToken)` |
| `Transport/WifiTransport` | Plain `TcpClient` to `ip:47809`, `NoDelay = true` |
| `Transport/UsbTransport` | usbmuxd client: connect `127.0.0.1:27015`, plist `Connect` to device port 47809, return the tunnelled stream |
| `DeviceManager` | usbmuxd `Listen` for attach/detach + mDNS browse; merged device list; prefers USB when both available; reconnect with backoff |
| `Protocol/` | Same framing as iOS; shared test vectors in `tests/protocol-vectors/` |
| `Video/IVideoDecoder` → `MjpegDecoder` | WIC JPEG decode → BGRA → NV12 (SIMD). Later: WIC planar YCbCr decode straight to 4:2:0 to skip the RGB round-trip; `H264Decoder` via MF. |
| `FramePipeline` | Single "latest frame" slot. Scales to the format the virtual camera consumer requested. Counts dropped frames. |
| `FramePublisher` | Named-pipe server feeding the media source (see §4.3) |
| `Stats` | fps, bitrate, drops, transport latency (see §6) |
| UI | Dashboard + Settings + manual connect |

### 4.3 Virtual camera (`windows/VirtualCamera`)

- COM in-proc server, CLSID fixed, registered in HKLM by installer (`regsvr32` once, admin).
- Implements `IMFMediaSourceEx`, `IMFGetService`, `IKsControl` (required by Frame Server, may return `ERROR_SET_NOT_FOUND`), one `IMFMediaStream2`.
- Exposed media types (V1): **NV12** and **RGB32** at **1280×720** and **1920×1080**, 30 fps. NV12 is the native format of most camera pipelines and what Chrome/Teams/Windows Camera prefer; RGB32 for compatibility. MJPEG/YUY2 output not exposed — the Frame Server and apps handle NV12; exposing compressed types would make the source more complex for no gain.
- **Frame source:** connects as client to named pipe `\\.\pipe\iPhoneWebcam.Frames` hosted by the companion app. Pipe DACL grants `LOCAL SERVICE`, `SYSTEM`, and the current user. Named pipes are not session-scoped, so this crosses the session-0 boundary without `SeCreateGlobalPrivilege` problems (which a `Global\` shared-memory section created by a normal user would hit). Cost: one ~3 MB copy per 1080p frame (~93 MB/s) — negligible locally. Shared memory is a later optimisation if profiling shows need.
- **When no frames arrive** (app closed, iPhone disconnected): the source keeps producing frames at the negotiated rate showing a generated *"iPhone Webcam — waiting for iPhone"* card. Consumers never stall or error.
- **Lifetime:** V1 uses `MFVirtualCameraLifetime_Session` + `CurrentUser` → no admin at runtime; camera exists while the companion app runs (start app minimized to tray at login). Option later: `System` lifetime so the camera is always listed.

**Why not Windows 10?** `MFCreateVirtualCamera` does not exist before build 22000. Supporting Win10 means a separate DirectShow filter (invisible to the Windows Camera app and other MF-only apps). Deferred until Win11 path is complete; architecture keeps the frame source (`named pipe → frame`) reusable by a future DShow filter.

## 5. Security & privacy

- PC connects only to private/link-local addresses (RFC1918, 169.254/16, fe80::/10, usbmux). iPhone listener rejects non-local peers.
- **Pairing:** first connection from an unknown PC → iPhone shows 6-digit code → user types it on PC. Both derive a pairing key; PC stores `pc_id` + key (DPAPI-protected), iPhone stores it in Keychain. Subsequent sessions do HMAC-SHA256 challenge/response (see protocol.md). Unknown/unpaired clients get no video.
- **Wi-Fi traffic is not encrypted in V1** (authenticated, not confidential). Acceptable for a home LAN; documented. Upgrade path: TLS on the TCP stream.
- No cloud, telemetry, analytics, accounts, recording. Logs never contain frame data or pairing keys.

## 6. Latency & performance strategy

- Both ends hold **at most one frame in flight**; newer frames replace older ones (drop counters visible in UI).
- `TCP_NODELAY` both ends.
- **Latency measurement:** PING/PONG carry iPhone monotonic time; PC estimates clock offset (NTP-style, min-RTT filter). Each frame carries capture PTS → *transport+decode latency* = `pc_now − (pts + offset)`. Glass-to-glass is measured manually (stopwatch test) in Phase 6.
- Targets: USB ≤ 60 ms, Wi-Fi ≤ 100 ms capture→virtual-camera output.
- Adaptation: if decode can't keep up, PC sends `SET_QUALITY` / `SET_RESOLUTION` downgrade (1080p→720p, JPEG quality ↓).

## 7. Toolchain & open decisions

| Need | Status on dev PC |
|---|---|
| Visual Studio 2022 Build Tools (MSVC, Windows 11 SDK ≥ 22000) | **Missing** |
| CMake | **Missing** (VS Build Tools includes one) |
| .NET SDK (9 or 10) | **Missing** (only runtime) |
| Apple Mobile Device Service | Present ✓ |
| ffmpeg | Present ✓ (test sources) |
| Xcode (macOS) for iOS build | **N/A on Windows — user decision needed** |

**User has no Mac (2026-10-25).** Chosen direction: build on a GitHub Actions macOS runner (also serves as the Swift compile check), install from Windows with Sideloadly. Free Apple ID → app expires every 7 days and must be re-signed; paid Apple Developer Program → 1-year signing or TestFlight (most plug-and-play). Pending user choice of Apple ID type.

iOS build options when no Mac is available: (a) Mac/Mac mini/borrowed Mac with Xcode, free Apple ID provisioning (7-day re-sign) or paid account; (b) GitHub Actions macOS runner builds an unsigned `.ipa`, sideloaded from Windows with Sideloadly/AltStore using a free Apple ID (7-day expiry). The iOS project is defined with **XcodeGen** (`project.yml`) so it is reproducible from text on CI.

## 8. Phase plan

| Phase | Deliverable | Exit test |
|---|---|---|
| 1 | This doc, protocol.md, repo skeleton | Review |
| 2 | iOS camera prototype (preview, switch, 720/1080, fps) | Runs on iPhone, fps counter stable at 30 |
| 3 | Windows receiver + PC test sender (ffmpeg MJPEG → our protocol) | Receiver shows test pattern, fps/latency correct; unit tests for framing |
| 4 | iPhone → Wi-Fi → Windows preview window | Live video in app window |
| 5 | Virtual camera with generated test pattern | Visible + rendering in Windows Camera, Chrome, OBS, Teams/Zoom |
| 6 | Real frames → virtual camera | iPhone video in Chrome/OBS; 30 min soak |
| 7 | UsbTransport (usbmuxd) | Same as 6 over USB; Wi-Fi still green |
| 8 | Discovery, reconnect, settings, pairing UX, installer, H.264 option | Definition of Done in spec |

## Decision Log

| # | Date | Decision |
|---|---|---|
| D1 | 2026-10-25 | Virtual camera = MF `MFCreateVirtualCamera` + COM media source. Win11 only for V1. |
| D2 | 2026-10-25 | USB via Apple Mobile Device Service usbmuxd (`127.0.0.1:27015`). No custom driver. |
| D3 | 2026-10-25 | iPhone = TCP server on port 47809; PC = client; same stream for USB and Wi-Fi. |
| D4 | 2026-10-25 | V1 codec MJPEG (VideoToolbox HW JPEG); H.264 later behind `IVideoDecoder`. |
| D5 | 2026-10-25 | Windows: C++ media source DLL + C#/.NET WPF app. App→source frames via named pipe. |
| D6 | 2026-10-25 | Tests live beside each project (`ios/…Tests`, `windows/Core.Tests`); cross-language protocol vectors in `tests/`. Protocol spec lives only in `docs/protocol.md` (no duplicate `protocol/` folder). |
| D7 | 2026-10-25 | Toolchain installed: .NET SDK 10.0.402, VS 2022 Build Tools 17.14 (VC tools, Win11 SDK 26100, CMake, ATL). |
| D8 | 2026-10-25 | Test frame sources are push-driven (await next capture) like the iPhone camera callback; polling caused a beat between 30 Hz clocks that silently lost ~17% of frames. |
| D9 | 2026-10-25 | Phase 3 decode path is WIC JPEG → BGR32 (≈4–5 ms per 1080p frame on the dev PC). NV12 conversion is added in Phase 5, where the virtual camera needs it. |
| D10 | 2026-10-25 | iOS protocol/crypto split into Swift package `IWCore` so it is testable with `swift test` (no simulator). Cross-language vectors in `tests/protocol-vectors/vectors.json`, generated from C# and independently verified against RFC 2104/5869 with Python. |
| D11 | 2026-10-25 | iOS UI locked to landscape-right, same as the stream orientation, so preview = what the PC receives. Uses `AVCaptureConnection.videoOrientation` (deprecated in iOS 17 but functional) to support iOS 16. |
| D12 | 2026-10-25 | iOS builds via GitHub Actions (macos-15, XcodeGen) → unsigned IPA → Sideloadly. See docs/ios-setup.md. |
| D13 | 2026-10-25 | Android added as a phone platform (user has no Mac; iOS sideloading blocked by Apple ID login errors). Kotlin + CameraX, classic Views, AGP 9.3 / Gradle 9.5. Same protocol; USB via `adb forward` (PC connects to 127.0.0.1, no Windows changes). iOS code kept, parked. |
| D14 | 2026-10-25 | Android JPEG encode is CPU-bound (~27 ms @1080p on SM-T510): FrameGate allows 2 frames in flight so encode overlaps send → 1080p30 with 0 drops. H.264 via MediaCodec remains the upgrade path for slower devices / Wi-Fi bandwidth. |
| D15 | 2026-10-25 | Product renamed **Phone Webcam** (Android is now the primary phone). C# namespaces/assemblies `PhoneWebcam.*`, app data `%LOCALAPPDATA%\PhoneWebcam`. Wire identifiers unchanged for compatibility: HKDF label `iphonewebcam pairkey v1`, Bonjour type `_iphonewebcam._tcp`, magic `IW`. iOS app keeps its own display name. |
| D16 | 2026-10-25 | Virtual camera implemented with plain COM (no WIL/C++/WinRT, static CRT) after VCamSample's contract. Delivery is paced by a 30 fps worker thread (immediate `RequestSample` answers produced ~390 fps). CPU sample path for now (`SetD3DManager` ignored). App starts/stops the camera via DLL exports (session lifetime, current user). Verified: MF reader 30.0 fps, Chrome 30.0 fps, DirectShow capture. |
| D17 | 2026-10-25 | Phase 6: app → virtual camera via named pipe `PhoneWebcam.Frames` (NV12, latest-frame-wins; ACL SYSTEM/LOCAL SERVICE/user, network denied). DLL scales to the negotiated size and falls back to the test card after 1 s. BT.709 Cb coefficients corrected to (-26,-86,112). |
| D18 | 2026-10-25 | Phase 7: Android USB via adb (`adb forward tcp:0 tcp:47809`, a free local port per device). adb isn't bundled (Android SDK licence); the installer reuses it or offers Google's official download. iPhone USB (usbmuxd) not implemented. |
| D19 | 2026-10-25 | Phase 8: UDP discovery probe/reply on port 47810 (simpler than Bonjour browsing on Windows; Android answers it). `WebcamController` owns the connect → pair → stream → reconnect lifecycle and remembers "webcam on" across reconnects. Dashboard follows the spec flow (auto-connect, Start/Stop Webcam). Installer is a self-elevating PowerShell script (no extra tooling); the app is published self-contained as a single compressed file. |

