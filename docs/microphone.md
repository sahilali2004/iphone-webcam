# Phone microphone

The phone's microphone can be used as the PC's microphone in calls.

## Why it needs a virtual audio cable

Windows 11 has a user-mode API for virtual **cameras** (`MFCreateVirtualCamera`), but **none for microphones**. A new recording device can only come from a kernel-mode audio driver. Shipping our own would mean a Microsoft-signed driver (EV certificate plus attestation signing) or running Windows in test-signing mode, which weakens system security. So, as the spec asks, Phone Webcam doesn't install a driver. It sends the phone's audio into an existing, signed **virtual audio cable**:

```text
Phone mic ─PCM over USB/Wi-Fi─► Phone Webcam ─WASAPI─► "CABLE Input" (playback)
                                                            │  (cable driver)
                     Zoom / Teams / Chrome ◄── "CABLE Output" (microphone)
```

| Cable | Notes |
|---|---|
| **VB-CABLE** (donationware, vb-audio.com/Cable) | Recommended. Driver catalog signed by Microsoft (Hardware Compatibility Publisher); Phone Webcam detects it and names the microphone to pick. Its licence forbids bundling it in another installer, so install it yourself (below). |
| Steam Streaming Microphone | Installed by Steam. Detected, but only works if both of its sides use the same format. On the dev PC its playback side was 192 kHz and its recording side 48 kHz, which garbles audio; the app warns about this. |
| VoiceMeeter | Detected (VoiceMeeter Input → Output). |

Without a cable, the audio can still be played on speakers or headphones (for testing), but other apps can't use it as a microphone.

## Installing VB-CABLE

1. Download `VBCABLE_Driver_Pack45.zip` from https://vb-audio.com/Cable/ and extract it.
2. Right-click **VBCABLE_Setup_x64.exe** › **Run as administrator** › **Install Driver**, and wait for the "installation complete" message.
3. Restart Windows. **CABLE Input** (playback) and **CABLE Output** (microphone) appear, and Phone Webcam preselects the cable.

## Using it

1. On the PC, tick **Use phone microphone** and choose the cable in the list (it's preselected if found).
2. In the call app, choose the microphone the dashboard names, for example **CABLE Output (VB-Audio Virtual Cable)**.
3. The phone asks for microphone permission the first time and shows **· Mic on** while streaming.

## Protocol

`START_AUDIO {sampleRate, channels}` → phone replies `AUDIO_STATE {state: streaming | stopped | denied}` and sends `AUDIO_FRAME`s: a 20-byte header (codec 1 = PCM s16le, channels, sample rate, sequence, ptsUs) plus 20 ms of PCM. Default: 48 kHz mono, 768 kbit/s, no codec delay. See [protocol.md](protocol.md) §5a.

The PC keeps at most 150 ms queued; anything beyond that is dropped so audio can't drift behind the video. On Android, the source is `VOICE_COMMUNICATION`, so the device's echo cancellation and noise suppression apply.

## Verified (2026-10-25)

| Check | Result |
|---|---|
| Fake phone (440 Hz tone) → PC output, captured by WASAPI loopback | 440.1 Hz, amplitude exactly as sent, 0 backlog drops |
| Galaxy Tab mic: a 1 kHz beep from the PC speakers, picked up acoustically | Arrives at the PC output, dominant frequency 1000.0 Hz |
| Microphone on/off and resume after a reconnect | Covered by `WebcamControllerTests` |
| Through Steam's cable into "Microphone (Steam Streaming Microphone)" | Distorted because of the cable's own format mismatch (detected and shown in the app) |
| Through VB-CABLE into Zoom/Teams | Not tested: VB-CABLE isn't installed on the dev PC |
