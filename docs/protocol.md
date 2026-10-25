# iPhone Webcam Wire Protocol — v1

Status: draft for Phase 3/4 implementation. Any change bumps `version` or adds a new message type; never repurpose existing IDs.

## 1. Transport

- One TCP connection per session. **iPhone listens** on port **47809**; the PC connects.
  - Wi-Fi: PC → `iphone-ip:47809` directly.
  - USB: PC → usbmuxd `127.0.0.1:27015` → plist `Connect {DeviceID, PortNumber=47809}` → after `Result Number=0` the socket is a raw pipe to the same listener. See [usb.md](usb.md).
- The protocol below starts after the transport is established and is **identical for both transports**.
- `TCP_NODELAY` on both ends. One active client per iPhone; a second client gets `ERROR busy` and is closed.

## 2. Framing

Every message = **16-byte header** + payload. All integers **little-endian**.

```text
offset size field
0      2    magic     0x49 0x57  ("IW"; u16 LE = 0x5749)
2      1    version   1
3      1    type      message type (table §3)
4      2    flags     per-type, 0 if unused
6      2    reserved  0
8      4    length    payload length in bytes (header not included)
12     4    seq       sender's message counter, starts at 0, wraps
```

Limits: control payload ≤ 64 KiB; video payload ≤ 8 MiB. A receiver that sees bad magic, unknown version, or an over-limit length closes the connection (no resync attempt — TCP doesn't lose bytes, so a bad header is a bug or attack).

Unknown `type` values with valid headers are skipped (`length` bytes) and logged once — forward compatibility.

## 3. Message types

Control payloads (`0x01–0x7F`) are UTF-8 JSON objects: small, rare, easy to debug. Media payloads (`0x80+`) are binary.

| Type | Name | Dir | Payload |
|---|---|---|---|
| 0x01 | HELLO | PC→ | `{proto:1, pcId, pcName, appVersion}` |
| 0x02 | DEVICE_INFO | →PC | `{deviceId, deviceName, model, osVersion, appVersion, nonce, paired, cameras:[{id:"back"\|"front", torch:bool}], formats:[{w,h,fps:[30]}], codecs:["mjpeg"]}` |
| 0x03 | PAIR_REQUEST | PC→ | `{}` — iPhone shows a 6-digit code |
| 0x04 | PAIR_CONFIRM | PC→ | `{pcNonce, proof}` |
| 0x05 | AUTH | PC→ | `{pcNonce, mac}` (already paired) |
| 0x06 | AUTH_RESULT | →PC | `{ok:bool, reason?}` |
| 0x10 | START_STREAM | PC→ | `{camera:"back", width:1920, height:1080, fps:30, codec:"mjpeg", quality:0.7}` |
| 0x11 | STOP_STREAM | PC→ | `{}` |
| 0x12 | STREAM_STATE | →PC | `{state:"streaming"\|"stopped"\|"paused", reason?, camera, width, height, fps, codec}` — sent after every change, incl. app backgrounded |
| 0x13 | CAMERA_SWITCH | PC→ | `{camera:"front"}` |
| 0x14 | SET_RESOLUTION | PC→ | `{width, height}` |
| 0x15 | SET_FPS | PC→ | `{fps}` |
| 0x16 | SET_QUALITY | PC→ | `{quality: 0.1–1.0}` (MJPEG) / `{bitrate}` (H.264) |
| 0x17 | SET_TORCH | PC→ | `{on:bool}` |
| 0x18 | START_AUDIO | PC→ | `{sampleRate: 48000, channels: 1}`: start sending microphone audio |
| 0x19 | STOP_AUDIO | PC→ | `{}` |
| 0x1A | AUDIO_STATE | →PC | `{state: "streaming"\|"stopped"\|"denied", reason?}` |
| 0x20 | PING | both | binary: `u64 senderTimeUs` |
| 0x21 | PONG | both | binary: `u64 echoedTimeUs, u64 responderTimeUs` |
| 0x3F | DISCONNECT | both | `{reason}` — graceful close, peer must not auto-reconnect if `reason:"user"` |
| 0x40 | ERROR | both | `{code, message}` — codes: `busy`, `unauthorized`, `unsupported`, `camera_unavailable`, `bad_request` |
| 0x80 | VIDEO_FRAME | →PC | binary, §5 |
| 0x81 | AUDIO_FRAME | →PC | binary, §5a |

Commands that change stream settings are answered by a `STREAM_STATE` reflecting what the iPhone actually applied (it may clamp to supported values).

## 4. Session flow

```text
PC                                   iPhone
 │──── HELLO ─────────────────────────►│
 │◄─── DEVICE_INFO (nonce, paired?) ───│
 │                                     │
 │  paired:                            │
 │──── AUTH {pcNonce, mac} ───────────►│
 │  not paired:                        │
 │──── PAIR_REQUEST ──────────────────►│   iPhone displays code 482913
 │      (user types code on PC)        │
 │──── PAIR_CONFIRM {pcNonce, proof} ─►│
 │                                     │
 │◄─── AUTH_RESULT {ok:true} ──────────│
 │──── START_STREAM ──────────────────►│
 │◄─── STREAM_STATE streaming ─────────│
 │◄─── VIDEO_FRAME ────────────────────│ × N
 │──── PING ──────────────────────────►│   every 1 s
 │◄─── PONG ───────────────────────────│
```

Before `AUTH_RESULT ok`, the iPhone accepts only HELLO / PAIR_* / AUTH / PING / DISCONNECT. Anything else → `ERROR unauthorized` + close. 3 failed proofs → iPhone regenerates code and refuses pairing for 30 s.

### 4.1 Pairing & authentication crypto

`phoneNonce`, `pcNonce`: 16 random bytes, base64 in JSON. `code`: 6 ASCII digits. `proof` / `mac`: base64 in JSON.
`|` = byte concatenation of: ASCII label, raw (decoded) nonce bytes, UTF-8 `pcId`. The HMAC key `code` is its ASCII bytes.

```text
proof     = HMAC-SHA256(key=code,      msg="pair-v1" | phoneNonce | pcNonce | pcId)
pairKey   = HKDF-SHA256(ikm=code, salt=phoneNonce | pcNonce, info="iphonewebcam pairkey v1", len=32)
mac       = HMAC-SHA256(key=pairKey,   msg="auth-v1" | phoneNonce | pcNonce | pcId)
```

PC stores `{deviceId → pairKey}` protected with DPAPI (CurrentUser). iPhone stores `{pcId → pairKey, pcName}` in the Keychain; the iOS settings screen lists paired PCs and can revoke them.

**Threat model note:** this stops unknown/unpaired clients on the LAN from taking the camera. A passive attacker who records the *pairing exchange over Wi-Fi* could brute-force the 6-digit code offline. Mitigation: the PC app recommends pairing while connected by USB (usbmux traffic never touches the network). Video itself is not encrypted in v1 (see architecture §5).

## 5. VIDEO_FRAME payload

```text
offset size field
0      1    codec      1 = MJPEG (JPEG baseline, 4:2:0), 2 = H.264 Annex-B (reserved), 3 = HEVC (reserved)
1      1    flags      bit0 keyframe (always 1 for MJPEG), bit1 mirrored (front camera, already mirrored), bit2 discontinuity
2      1    rotation   0,1,2,3 = clockwise quarter turns needed to display upright (v1 always 0: landscape locked)
3      1    reserved   0
4      2    width      encoded width in pixels
6      2    height     encoded height in pixels
8      4    frameId    capture counter, increments by 1 per captured frame (gaps = dropped on iPhone)
12     8    ptsUs      capture time, iPhone monotonic clock (µs) — same clock as PING/PONG
20     ..   data       codec bitstream (length = header.length − 20)
```

### 5a. AUDIO_FRAME payload

```text
offset size field
0      1    codec       1 = PCM signed 16-bit little-endian, interleaved
1      1    channels    1 or 2
2      2    reserved    0
4      4    sampleRate  e.g. 48000
8      4    sequence    +1 per frame
12     8    ptsUs       capture time, phone monotonic clock (µs)
20     ..   pcm         normally 20 ms (48 kHz mono: 960 samples = 1920 bytes)
```

DEVICE_INFO carries `microphone: bool`. Audio frames are never dropped on the phone; the PC caps its playout buffer at 150 ms.

### 5.1 Backpressure rule (both ends)

- iPhone: at most **one** VIDEO_FRAME queued for send. A newly encoded frame replaces an unsent one (count as `droppedSend`). Encoding is skipped while the previous frame is still being written.
- PC: decoder reads the newest frame; if a newer frame arrived while decoding, the older one is discarded (`droppedDecode`).
- Control messages are never dropped and are written between frames (each message is written atomically; the send queue gives control priority).

## 6. Clock & latency estimation

PC sends `PING{t0 = pcTimeUs}`. iPhone replies `PONG{t0, t1 = phoneTimeUs}`. On receipt at `t2`:

```text
rtt    = t2 − t0
offset = t1 − (t0 + rtt/2)        // phoneClock − pcClock
```

Keep the offset from the lowest-RTT sample in the last 10 s window. Per frame: `latency = pcNowUs − (ptsUs − offset)` = capture → received. The UI shows this plus decode time.

The iPhone also pings the PC (same messages, roles swapped) to detect a dead link: no inbound traffic for **3 s** → connection considered lost.

## 7. Reconnection

- PC: on loss, state → *Reconnecting*, retry the same device with backoff 0.5 s, 1 s, 2 s, 2 s… Switch transport if the other one becomes available (prefer USB). After reconnect, re-auth and send `START_STREAM` with the last settings.
- iPhone: on loss, stop encoding (keep the capture session running for instant resume while foreground), keep listening.
- Virtual camera keeps emitting the "waiting" card throughout; consumer apps never see the device vanish.

## 8. Discovery (Wi-Fi)

**UDP probe (implemented: Windows + Android).** The PC sends the 11 ASCII bytes `PWDISCOVER1` to UDP port **47810**, both to `255.255.255.255` and to each IPv4 interface's directed broadcast. A phone running the app replies unicast with UTF-8 JSON `{"deviceId": "...", "name": "...", "port": 47809}`. Both sides ignore non-private, non-link-local peers. Android holds a Wi-Fi multicast lock while answering, because some drivers drop broadcasts in power-save.

**Bonjour (iOS only, not browsed by the PC yet).** Service type `_iphonewebcam._tcp`, port 47809, TXT: `id=<deviceId>`, `name=<deviceName>`, `proto=1`.

`deviceId` is a random UUID created on first launch, not a hardware identifier. Manual `ip:port` entry is always available.

**USB (Android).** No discovery packets: the PC lists devices with `adb devices -l` and connects through `adb forward tcp:0 tcp:47809`.

## 9. Test vectors

`tests/protocol-vectors/vectors.json`: pairing crypto and header byte layouts, generated from the C# implementation and checked by the C#, Kotlin and Swift test suites, so all three agree byte for byte.

## 10. Virtual-camera frame pipe (PC-internal)

`\\.\pipe\PhoneWebcam.Frames`, app → `PhoneWebcamSource.dll`. Per frame: a 32-byte little-endian header (`magic 'PWF1' u32, width u32, height u32, format u32 = 1 (NV12), payloadSize u32, flags u32 = 0, frameId u64`), then an NV12 frame (stride = width, BT.709 limited range). Latest-frame-wins: the app drops a frame if the previous one is still being written.
