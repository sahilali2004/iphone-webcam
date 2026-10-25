# Building and installing the iPhone app (no Mac)

Xcode only runs on macOS, so the app is built by a free GitHub Actions macOS runner and installed from Windows with Sideloadly.

## One-time setup

1. **GitHub**: create a free account and a **private** repository (e.g. `iphone-webcam`).
2. **Push this folder** to it (from `D:\WEBCAM SOFTWARE`):
   ```powershell
   git init -b main
   git add .
   git commit -m "Initial import"
   git remote add origin https://github.com/<you>/iphone-webcam.git
   git push -u origin main
   ```
3. **Sideloadly** (Windows): install from <https://sideloadly.io>. It uses the Apple Mobile Device Service that is already installed on this PC.
4. **Apple ID**: a free Apple ID works. Apps signed with a free ID expire after **7 days**; re-sideload (or let Sideloadly auto-refresh) to renew. The paid Apple Developer Program ($99/yr) gives 1-year signing.

## Each build

1. Push changes (or run the workflow manually: GitHub → Actions → **iOS** → *Run workflow*).
2. The workflow:
   - runs `swift test` on `ios/IWCore`, checking protocol + crypto byte-for-byte against `tests/protocol-vectors/vectors.json` (generated from the Windows code),
   - generates the Xcode project with XcodeGen,
   - builds an unsigned Release build and uploads **iPhoneWebcam-unsigned-ipa**.
3. Download the artifact (zip) from the workflow run and extract `iPhoneWebcam-unsigned.ipa`.
4. Connect the iPhone by USB, unlock it, tap *Trust This Computer* if asked.
5. In Sideloadly: drag in the `.ipa`, enter your Apple ID, click **Start**.
6. On the iPhone: Settings › General › VPN & Device Management → trust your Apple ID's developer profile. With a free ID, also enable **Developer Mode** (Settings › Privacy & Security › Developer Mode) when iOS asks.

## First run

1. Open **iPhone Webcam**, hold the phone in landscape (charging port on the right), tap **Start Camera**.
2. Allow **Camera** and **Local Network** when asked. Without Local Network permission the PC cannot connect over Wi-Fi.
3. The panel shows `Connect from your PC to 192.168.x.x`.
4. On the PC: open iPhone Webcam, type that address, click **Connect**.
5. The iPhone shows a 6-digit code; type it on the PC. Pairing is remembered on both sides.

## Troubleshooting

| Symptom | Fix |
|---|---|
| PC says "Nothing is accepting connections" | iPhone app not started, or in background. Bring it to the front. |
| PC times out / can't reach | Same Wi-Fi network? Guest networks and "AP isolation" block device-to-device traffic. |
| iPhone shows "Network unavailable" | Settings › Privacy & Security › Local Network → enable iPhone Webcam. |
| App won't open after a week | Free-ID signature expired; re-sideload. |
