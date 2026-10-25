# Virtual camera

Status: **working** (Phase 5, 2026-10-25). Apps list it as **"Phone Webcam (Windows Virtual Camera)"**. Windows appends the suffix and it can't be removed.

## Install (once, admin)

```powershell
.\scripts\build-vcam.ps1        # builds windows/VirtualCamera → PhoneWebcamSource.dll + vcamtest.exe
.\scripts\install-vcam.ps1      # copies to C:\Program Files\PhoneWebcam, registers in HKLM (UAC prompt)
.\scripts\install-vcam.ps1 -Uninstall
```

Re-run `install-vcam.ps1` after rebuilding the DLL. Updating needs admin by design: the DLL runs inside a Windows service, so a user-writable copy would be a privilege-escalation hole.

## How it works

`MFCreateVirtualCamera` (Windows 11, build 22000+) registers a **software camera** backed by our COM media source. The Windows Camera Frame Server loads the source and shares it with every app: Media Foundation, WinRT `MediaCapture`, and DirectShow through the Frame Server's bridge.

| Process | Account | Role |
|---|---|---|
| Phone Webcam app | user | Calls `PhoneWebcam_StartVirtualCamera` on start and `…Stop` on exit (session lifetime, current user, no admin needed at runtime) |
| Frame Server (`svchost`) | LOCAL SERVICE | Loads `PhoneWebcamSource.dll`, produces the frames apps receive |
| Chrome / OBS / Teams / Camera | user | Talk to the Frame Server, never to our DLL directly |

Source layout (`windows/VirtualCamera/src`):

| File | Role |
|---|---|
| `Activator` | What the Frame Server CoCreates; creates the media source lazily |
| `MediaSource` | `IMFMediaSourceEx`, `IMFGetService`, `IKsControl`, `IMFSampleAllocatorControl`, sensor profile, presentation descriptor |
| `MediaStream` | `IMFMediaStream2`; **delivery thread paced at 30 fps** by a high-resolution waitable timer (answering `RequestSample` immediately let a reader pull ~390 fps) |
| `FrameProducer` | Draws the frame (GDI) and writes NV12 (BT.709 limited range) or RGB32 into the sample |
| `dllmain` | COM entry points, HKLM registration, app-facing exports |

Plain COM, no WIL/C++/WinRT/NuGet; the CRT is linked statically so the service needs no VC++ redistributable. The interface contract follows Microsoft's virtual-camera documentation and [smourier/VCamSample](https://github.com/smourier/VCamSample) (MIT), with two of its bugs fixed (stream index in `Start`, `=` vs `==` in `SetStreamState`).

## Formats

| Subtype | Sizes | FPS |
|---|---|---|
| NV12 (default) | 1920×1080, 1280×720 | 30 |
| RGB32 | 1920×1080, 1280×720 | 30 |
| YUY2 | added automatically by the Frame Server for DirectShow clients | 30 |

## What it shows

Phase 5: a test card, "Phone Webcam · Waiting for the phone…", with colour bars, a moving marker (it stops if frames freeze), resolution and clock. Phase 6 replaces it with the phone's video while a phone is streaming.

## Verified (2026-10-25, Windows 11 24H2 build 26100)

| Check | Result |
|---|---|
| `vcamtest` via Media Foundation (Frame Server path) | Found in device list; NV12 1080p **30.0 fps**, RGB32 720p **30.0 fps**; frame sizes exact; snapshot correct |
| Chrome (`getUserMedia`, headless) | Listed, opened at 1920×1080, **30.0 fps** measured, colour-bar pixels exact |
| DirectShow (ffmpeg `-f dshow`, the OBS/Zoom/Discord path) | Listed; captured 1080p (YUY2) frames correctly |
| App lifecycle | Camera appears when the Phone Webcam app starts, disappears when it closes |
| Not yet tested | Windows Camera app, Teams (known risk: some Teams builds preview virtual cameras without sending them), Zoom |

## Debugging

- Trace output: `OutputDebugString` prefixed `[PhoneWebcamSource <pid>]`; view with Sysinternals DebugView (enable *Capture Global Win32*, since the Frame Server runs in session 0).
- If the DLL is locked during reinstall: `install-vcam.ps1` stops `FrameServer`/`FrameServerMonitor` first; they restart on demand.
- Camera privacy off (*Let desktop apps access your camera*) → start fails with `0x80070005`; the app shows the fix.
