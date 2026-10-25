# Virtual camera

## API

`MFCreateVirtualCamera` (Windows Build 22000+, `mfvirtualcamera.h`, `mfsensorgroup.lib`) registers a **software camera** backed by our COM media source. The Windows Camera Frame Server loads the source and shares it with every app, so MF, DirectShow (via Frame Server) and WinRT `MediaCapture` clients all see one device.

```cpp
MFCreateVirtualCamera(
    MFVirtualCameraType_SoftwareCameraSource,
    MFVirtualCameraLifetime_Session,      // V1: exists while companion app runs
    MFVirtualCameraAccess_CurrentUser,    // no admin needed at runtime
    L"iPhone Webcam",                     // Windows appends "Windows Virtual Camera"
    L"{CLSID-of-IPhoneWebcamSource}",
    nullptr, 0,                           // default categories: VIDEO_CAMERA, VIDEO, CAPTURE
    &vcam);
vcam->Start(nullptr);
```

## Process model

| Process | Account | Loads our DLL? |
|---|---|---|
| Companion app | user | yes (to call `vcam_*` exports) |
| Frame Server (`svchost`, FrameServer) | LOCAL SERVICE, session 0 | yes — produces the frames apps receive |
| Frame Server Monitor | LOCAL SYSTEM | yes |
| Chrome / OBS / Teams… | user | no — they talk to Frame Server |

Consequences:
- DLL registered in **HKLM** (admin, once, by installer) and installed under `C:\Program Files\iPhoneWebcam\` (service accounts can't read the user profile).
- Frames reach the source through named pipe `\\.\pipe\iPhoneWebcam.Frames` (app = server, DACL: user + LOCAL SERVICE + SYSTEM).
- The source must never block the Frame Server: pipe reads happen on a worker thread; `RequestSample` always returns the latest frame or the "waiting" card immediately.

## Media types offered

| Subtype | Sizes | FPS |
|---|---|---|
| NV12 | 1920×1080, 1280×720 | 30 |
| RGB32 | 1920×1080, 1280×720 | 30 |

If the requested size differs from the incoming iPhone frame, the app-side `FramePipeline` scales before publishing (the app tells the source which size is active through a small header in each pipe message).

## Interfaces to implement

`IMFMediaSourceEx` (+ `IMFMediaSource`, `IMFMediaEventGenerator`), `IMFGetService`, `IKsControl`, attribute store (`IMFAttributes`), one `IMFMediaStream2` with `IMFMediaEventQueue`. Reference: [smourier/VCamSample](https://github.com/smourier/VCamSample) and Microsoft's Windows-Camera *VirtualCamera* sample.

## Debugging

- ETW provider for the DLL; capture with `tracelog`/WPR or the TraceSpy tool. No `OutputDebugString` from session 0.
- After rebuilding the DLL: stop apps using the camera, `net stop FrameServer` (admin) to unload, re-register.

## Known risks

- Teams has been reported to preview but not transmit some virtual cameras → test early in Phase 5.
- Camera privacy setting *"Let desktop apps access your camera"* off → `E_ACCESSDENIED`; app shows a fix-it message.
- Windows 10 not supported by this API (DirectShow filter would be a separate, later component).
