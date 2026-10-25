# Phone Webcam

Use an Android phone or tablet as a real Windows webcam. Apps see it as **Phone Webcam (Windows Virtual Camera)** in Chrome, Teams, Zoom, OBS, Discord, the Camera app, or anything else that uses a webcam. It works over **USB** (preferred) or **Wi-Fi**, and everything stays on your own devices: no cloud, accounts, telemetry or recording.

> Status: **V1 complete for Android.** Verified on a Samsung Galaxy Tab A 10.1 (Android 11) with Windows 11 24H2: 1080p at 30 fps over USB, about 85–90 ms from the phone's camera to the PC, 0 dropped frames, read by Chrome and DirectShow (the OBS/Zoom path) at 30 fps. An iPhone app exists in `ios/` and builds in CI, but it's parked because sideloading it needs an Apple ID login that currently fails.

## What you need

| | |
|---|---|
| PC | Windows 11 (build 22000+), 64-bit |
| Phone | Android 8 or later with the Phone Webcam app (`PhoneWebcam-android.apk`) |
| USB | A USB cable and **USB debugging** turned on (see below). The PC needs Google's Android platform-tools (adb); the installer finds it or offers to download it. |
| Wi-Fi | Phone and PC on the same network (not a guest network) |

## Install

1. Download or build `PhoneWebcam-0.1.0-win-x64.zip` (see *Building*), unzip it, and run **`Install.cmd`**. Accept the Windows admin prompt; it's needed once to register the camera.
   - It installs to `C:\Program Files\PhoneWebcam` and adds **Phone Webcam** to the Start menu.
   - If platform-tools (adb) isn't on the PC, it offers to download it from dl.google.com.
   - If an Android device is plugged in with USB debugging on, it offers to install the phone app.
2. **Phone app** (if the installer didn't do it): copy `PhoneWebcam-android.apk` to the phone and open it, allowing installs from that source. Or, with USB debugging on: `adb install PhoneWebcam-android.apk`.

To remove everything: `Uninstall.cmd`.

### Turning on USB debugging (once)

On the phone: **Settings › About phone › Software information** → tap **Build number** 7 times. Then **Settings › Developer options** → turn on **USB debugging**. Plug in the cable and tap **Allow** on the "Allow USB debugging?" prompt (tick *Always allow from this computer*).

## Use it

1. **Phone:** open **Phone Webcam**, hold it sideways, and tap **Start camera**.
2. **PC:** open **Phone Webcam** from the Start menu. It finds the phone automatically (USB first, then Wi-Fi) and shows **Phone connected**.
   - First time only: the phone shows a 6-digit code. Type it on the PC. Both remember each other after that.
3. Press **Start Webcam**. The status light turns red (**Live**).
4. In your video app, choose **Phone Webcam (Windows Virtual Camera)**:
   - **Chrome / Google Meet:** the camera menu inside the call, or ⋮ › Settings › Privacy and security › Site settings › Camera.
   - **Teams:** Settings › Devices › Camera.
   - **Zoom:** Settings › Video › Camera.
   - **OBS:** Sources › + › Video Capture Device › Device: Phone Webcam (Windows Virtual Camera).
   - **Windows Camera app:** the switch-camera button.

Rear/front camera and 720p/1080p can be changed on the PC (or the phone) while live. While Phone Webcam runs on the PC but no phone is streaming, apps see a "Waiting for the phone" card instead of a frozen image.

## Troubleshooting

| Problem | Fix |
|---|---|
| PC says **No phone** | USB: check the cable, USB debugging, and the *Allow* prompt on the phone. Wi-Fi: same network? Guest networks and "AP isolation" block devices from seeing each other. You can also choose **Enter an address…** and type the address shown in the phone app. |
| **Waiting for phone: Open Phone Webcam…** | Open the phone app and tap **Start camera**. It must stay in the foreground; Android stops the camera for background apps. |
| Pairing code rejected | Type the code currently on the phone. After 3 wrong tries the phone refuses pairing for 30 seconds. |
| Camera missing in other apps | Phone Webcam must be running on the PC. If the panel says the camera isn't installed, rerun `Install.cmd`. If it says Windows blocked camera access: **Settings › Privacy & security › Camera › Let desktop apps access your camera**. |
| App shows the camera but it's black | Usually the phone is face-down or covered. Check the preview in the Phone Webcam window. |
| Stutters on Wi-Fi | Use USB, or switch to 720p. |

Logs: `%LOCALAPPDATA%\PhoneWebcam\logs\app.log` (no video content is ever logged).

## Privacy & security

- Video goes only from your phone to your PC: USB through adb, or your local Wi-Fi. The PC refuses connections to non-local addresses, and the phone refuses connections from them.
- A PC must be **paired** (6-digit code, HMAC-SHA256 challenge after that) before the phone sends any video. Pairing keys are stored with Windows DPAPI on the PC and in app-private storage on the phone (backups disabled).
- Between the app and the virtual camera, frames use a local named pipe that denies network access.
- **Not encrypted on Wi-Fi in V1.** Others on the same Wi-Fi can't control the camera, but could observe the stream. Prefer USB on untrusted networks. Pairing over USB also keeps the code exchange off the network.

## How it works

```text
Phone app ──JPEG frames over TCP (USB via adb forward, or Wi-Fi)──► Phone Webcam (PC)
    CameraX → JPEG                                                   decode → NV12 ─┐
                                                                                    │ named pipe
                          Chrome / Teams / Zoom / OBS ◄── Windows Frame Server ◄── PhoneWebcamSource.dll
                                                                   (virtual camera, MFCreateVirtualCamera)
```

Details: [architecture](docs/architecture.md) · [wire protocol](docs/protocol.md) · [virtual camera](docs/virtual-camera.md) · [Android app](docs/android-setup.md) · [USB](docs/usb.md) · [Wi-Fi](docs/wifi.md) · [iPhone app (parked)](docs/ios-setup.md)

## Repository layout

```text
android/                 Android app (Kotlin, CameraX)
ios/                     iPhone app (Swift/SwiftUI) + IWCore protocol package; built by GitHub Actions
windows/
  App/                   PC app (WPF dashboard)
  Core/                  protocol, pairing, transports (Wi-Fi, USB/adb), discovery, decoding, virtual-camera feed
  Core.Tests/            xUnit tests, incl. loopback end-to-end and controller lifecycle tests
  VirtualCamera/         PhoneWebcamSource.dll (C++ Media Foundation source) + vcamtest
  Tools/TestSender/      fake phone for testing without a device
  installer/             Install/Uninstall scripts
tests/protocol-vectors/  byte-exact fixtures shared by the C#, Kotlin and Swift tests
scripts/                 build, install, publish helpers
```

## Building

Prerequisites: Windows 11, .NET SDK 10, Visual Studio 2022 Build Tools (C++ workload, Windows 11 SDK), Android Studio (for its SDK and JDK).

```powershell
.\scripts\publish.ps1        # everything → dist\PhoneWebcam-0.1.0-win-x64.zip
```

Pieces individually:

```powershell
dotnet build windows\PhoneWebcam.slnx                # PC app + tests
dotnet test windows\Core.Tests\Core.Tests.csproj     # 49 tests
.\scripts\build-vcam.ps1                             # virtual camera DLL + vcamtest.exe
.\scripts\install-vcam.ps1                           # dev install of the DLL (admin)
.\scripts\android-install.ps1                        # build + install the phone app over USB
.\scripts\run-test-sender.ps1                        # fake phone on 127.0.0.1 (no device needed)
```

Android unit tests: `cd android; .\gradlew.bat testDebugUnitTest`. iOS: see [docs/ios-setup.md](docs/ios-setup.md).

## Not in V1

Audio/microphone, H.264, 60 fps/4K, streaming while the phone app is in the background, Windows 10, iPhone over USB, and encryption on Wi-Fi. The architecture leaves room for each; see the decision log in [docs/architecture.md](docs/architecture.md).
