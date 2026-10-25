# Phone Webcam

Personal-use utility: use an Android phone/tablet (or iPhone) camera as a real Windows webcam (listed as **Phone Webcam** in Chrome, Teams, Zoom, OBS, Windows Camera…), over USB or Wi-Fi. Everything stays local — no cloud, accounts, telemetry or recording.

> Status: **Phase 5 complete.** A Galaxy Tab streams 1080p30 to the Windows app over USB, and Windows apps can select **Phone Webcam** as a camera (verified in Chrome and DirectShow/OBS path). For now the camera shows a test card; Phase 6 routes the phone's video into it.

## Requirements

| | |
|---|---|
| PC | Windows 11 (build 22000+), x64 |
| Phone | Android 8+ with the Phone Webcam app (`android/`), or iPhone with the iOS app (`ios/`, parked) |
| USB mode | Android: adb (Android SDK platform-tools). iPhone: *Apple Devices* app or iTunes |
| Wi-Fi mode | PC and phone on the same local network |

## Repository layout

```text
docs/                    architecture, protocol, usb, wifi, virtual-camera
ios/project.yml          XcodeGen spec for the iPhone app
ios/iPhoneWebcam/        Swift/SwiftUI app: Camera/, Streaming/, App/
ios/IWCore/              Swift package: protocol + crypto (swift test)
android/                 Kotlin Android app (CameraX), same protocol: see docs/android-setup.md
windows/
  VirtualCamera/         C++ COM media source DLL (loaded by Windows Frame Server) + vcamtest
  Core/                  C# library: transports, protocol, decoding, frame pipeline
  Core.Tests/            C# unit tests
  App/                   C# WPF companion app (UI)
  Tools/TestSender/      PC-side fake phone for testing without a device
  installer/             installer (Phase 8)
tests/protocol-vectors/  binary fixtures shared by Swift and C# tests
scripts/                 dev scripts (register DLL, run test sender, …)
```

## Docs

- [Architecture & decisions](docs/architecture.md)
- [Wire protocol](docs/protocol.md)
- [USB](docs/usb.md) · [Wi-Fi](docs/wifi.md) · [Virtual camera](docs/virtual-camera.md)
- [Building & installing the iPhone app without a Mac](docs/ios-setup.md)
- [Android app: build, install, USB/Wi-Fi](docs/android-setup.md)

## Development (Windows)

Prerequisites: Windows 11, [.NET SDK 10](https://dotnet.microsoft.com/download), Visual Studio 2022 Build Tools with the C++ workload (needed from Phase 5 on). Optional: `ffmpeg` on PATH for the realistic test source.

```powershell
cd windows
dotnet build PhoneWebcam.slnx
dotnet test Core.Tests/Core.Tests.csproj           # unit + loopback end-to-end tests
```

Try the receiver without a phone:

```powershell
.\scripts\run-test-sender.ps1            # fake phone on 127.0.0.1:47809 (add -Ffmpeg for ffmpeg testsrc2)
dotnet run --project windows/App          # click Connect, type the pairing code the sender prints
```

Virtual camera (Windows 11, once per machine, admin):

```powershell
.\scripts\build-vcam.ps1      # build PhoneWebcamSource.dll
.\scripts\install-vcam.ps1    # install + register (UAC prompt)
```

The Phone Webcam app then registers **"Phone Webcam (Windows Virtual Camera)"** while it runs. See [docs/virtual-camera.md](docs/virtual-camera.md).

Logs: `%LOCALAPPDATA%\PhoneWebcam\logs\app.log`. Pairings (DPAPI-protected): `%LOCALAPPDATA%\PhoneWebcam\pairings.json`.

Stability soak (default off): `$env:PHONEWEBCAM_SOAK_SECONDS=300; dotnet test windows/Core.Tests --filter Soak`.

Installation, usage (Chrome / Zoom / OBS) and troubleshooting sections will be filled in as phases land.
