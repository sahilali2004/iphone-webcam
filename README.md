# iPhone Webcam

Personal-use utility: use an iPhone camera as a real Windows webcam (listed as **iPhone Webcam** in Chrome, Teams, Zoom, OBS, Windows Camera…), over USB or Wi-Fi. Everything stays local — no cloud, accounts, telemetry or recording.

> Status: **Phase 4 (iPhone → Wi-Fi → Windows) code complete, awaiting first iOS build + on-device test.** The Windows receiver is tested end to end against a fake iPhone. The iPhone app is written but has not been compiled yet. It is built on GitHub Actions; see [docs/ios-setup.md](docs/ios-setup.md). Virtual camera: Phase 5.

## Requirements

| | |
|---|---|
| PC | Windows 11 (build 22000+), x64 |
| iPhone | Recent iOS, iPhone Webcam app (built from `ios/`) |
| USB mode | *Apple Devices* app (Microsoft Store) or iTunes — provides Apple Mobile Device Service |
| Wi-Fi mode | PC and iPhone on the same local network |

## Repository layout

```text
docs/                    architecture, protocol, usb, wifi, virtual-camera
ios/project.yml          XcodeGen spec for the iPhone app
ios/iPhoneWebcam/        Swift/SwiftUI app: Camera/, Streaming/, App/
ios/IWCore/              Swift package: protocol + crypto (swift test)
windows/
  VirtualCamera/         C++ COM media source DLL (loaded by Windows Frame Server)
  Core/                  C# library: transports, protocol, decoding, frame pipeline
  Core.Tests/            C# unit tests
  App/                   C# WPF companion app (UI)
  Tools/TestSender/      PC-side fake iPhone for Phase 3 testing
  installer/             installer (Phase 8)
tests/protocol-vectors/  binary fixtures shared by Swift and C# tests
scripts/                 dev scripts (register DLL, run test sender, …)
```

## Docs

- [Architecture & decisions](docs/architecture.md)
- [Wire protocol](docs/protocol.md)
- [USB](docs/usb.md) · [Wi-Fi](docs/wifi.md) · [Virtual camera](docs/virtual-camera.md)
- [Building & installing the iPhone app without a Mac](docs/ios-setup.md)

## Development (Windows)

Prerequisites: Windows 11, [.NET SDK 10](https://dotnet.microsoft.com/download), Visual Studio 2022 Build Tools with the C++ workload (needed from Phase 5 on). Optional: `ffmpeg` on PATH for the realistic test source.

```powershell
cd windows
dotnet build IPhoneWebcam.slnx
dotnet test Core.Tests/Core.Tests.csproj           # unit + loopback end-to-end tests
```

Try the receiver without an iPhone:

```powershell
.\scripts\run-test-sender.ps1            # fake iPhone on 127.0.0.1:47809 (add -Ffmpeg for ffmpeg testsrc2)
dotnet run --project windows/App          # click Connect, type the pairing code the sender prints
```

Logs: `%LOCALAPPDATA%\iPhoneWebcam\logs\app.log`. Pairings (DPAPI-protected): `%LOCALAPPDATA%\iPhoneWebcam\pairings.json`.

Stability soak (default off): `$env:IPHONEWEBCAM_SOAK_SECONDS=300; dotnet test windows/Core.Tests --filter Soak`.

Installation, usage (Chrome / Zoom / OBS) and troubleshooting sections will be filled in as phases land.
