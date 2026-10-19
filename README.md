# AirLens

Turn an Android phone into a low-latency, watermark-free PC webcam: up to 4K, over USB or Wi-Fi,
hardware-encoded on the phone and NVDEC-decoded on the PC.

| | |
|---|---|
| `android/` | Kotlin + Jetpack Compose transmitter: Camera2 → GLES → MediaCodec (H.264/H.265) → TCP server |
| `windows/` | C# .NET 8 receiver: socket loop → FFmpeg hwaccel (D3D11VA/NVDEC) → WinUI 3 preview + virtual camera |
| `docs/ARCHITECTURE.md` | Design, research evaluations, latency budget, memory management, reference-device notes |
| `docs/PROTOCOL.md` | Wire format (byte-exact, with golden vectors) |
| `docs/VIRTUAL_CAMERA.md` | Building, installing and testing the Windows virtual camera |
| `tools/probe-stream.ps1` | Minimal protocol client for checking a phone's stream without the Windows app |

## Quick start

```bash
# Phone (debug build)
cd android && ./gradlew :app:assembleDebug && adb install -r app/build/outputs/apk/debug/app-debug.apk

# PC
cd windows && dotnet build -c Release && dotnet test
# Put an FFmpeg 8.x LGPL shared build (bin\*.dll and LICENSE.txt, e.g. BtbN's ffmpeg-n8.1-latest-win64-lgpl-shared-8.1)
# in windows/src/AirLens.App/ffmpeg/, or set AIRLENS_FFMPEG_DIR. Not a GPL build: the setup build refuses one.
```

Open AirLens on the phone and press **Start streaming**. The Windows app connects by itself over
USB. Without a cable it finds the phone on Wi-Fi: press **Connect** on the phone's card once.

## Install on Windows like a normal app

```bash
powershell -ExecutionPolicy Bypass -File windows/installer/build.ps1
```

Then run `Setup.cmd` in `windows\installer\out\AirLens-0.1.0\` (or hand someone the zip next to it).
It asks for administrator rights once and installs AirLens to `C:\Program Files\AirLens`, with the
virtual camera, a Start menu entry and an uninstall entry in Settings → Apps. Started from the
Start menu, the app remembers USB/Wi-Fi, the phone IP and the virtual-camera switch, and reconnects
if it was connected when you closed it. Details: `docs/ARCHITECTURE.md` §8.

USB needs adb on the PC (Android platform-tools, or one another program installed) and USB
debugging on the phone; Wi-Fi needs neither.

## Website and downloads

- **Downloads** are the files on each GitHub release, for example
  `releases/download/v0.1.1/AirLens-0.1.1-windows-x64.zip` and `AirLens-0.1.1-android.apk`.
- **Landing page:** `site/` (plain HTML, CSS and one small script, no build), live at
  https://airlens-cyan.vercel.app. Everything it says about the current release (version, links,
  sizes, SHA-256) comes from `site/release.json`; without it, the buttons go to the latest release
  on GitHub. It is the Vercel project `silow/airlens`; `vercel.json` makes Vercel serve only
  `site/`, and a push that does not touch `site/` skips the deploy.

## Releasing (GitHub Actions)

Releases are built on GitHub, not on a PC: `.github/workflows/release.yml` tests and builds both
apps (the Windows setup with the camera DLL and an LGPL FFmpeg it downloads and verifies, and the
APK), publishes the release with checksums, then rewrites `site/release.json` on `main`, which
Vercel deploys. Installed Windows copies find the release within a day (Settings › Updates).
`.github/workflows/tests.yml` runs the unit tests on every push and pull request.

1. **Once:** give the workflow the Android signing key as repository secrets. It must be the key
   that signed the earlier APKs (so far this PC's debug keystore), or phones can't update to the
   new APK. The workflow refuses to publish without it.

   ```powershell
   $env:GH_TOKEN = gh auth token --user RizwanNaasir
   [Convert]::ToBase64String([IO.File]::ReadAllBytes("$env:USERPROFILE\.android\debug.keystore")) | gh secret set ANDROID_KEYSTORE_BASE64 --repo RizwanNaasir/air-lens
   gh secret set ANDROID_KEYSTORE_PASSWORD --repo RizwanNaasir/air-lens --body android
   gh secret set ANDROID_KEY_ALIAS --repo RizwanNaasir/air-lens --body androiddebugkey
   gh secret set ANDROID_KEY_PASSWORD --repo RizwanNaasir/air-lens --body android
   ```

2. **Optional:** write what's new in `docs/releases/v<version>.md`; it goes at the top of the notes.
3. **Release:** `git tag v0.1.2` and `git push origin v0.1.2`. The tag is the version; nothing
   else needs changing. To try the builds first, run Actions › Release › Run workflow with
   "publish" unticked: it builds and tests and keeps the files as artifacts.

Details: `docs/ARCHITECTURE.md` §8.

## Status

- **End to end works over USB and Wi-Fi**: Huawei P30 → RTX 4060 laptop at **4K30, ~60 Mbps**
  (H.264 High or H.265 Main), ~30 fps, zero drops, NVDEC decode 0.3–0.5 ms per frame, live WinUI
  preview. Round trip is ~3 ms on USB and ~10 ms on Wi-Fi. Pulling the cable moves the stream to
  Wi-Fi, and plugging it back in returns it to USB, both by themselves. Measurements are in `docs/ARCHITECTURE.md` §6.
- Android app: 62 unit tests pass. The main screen is a viewfinder (design B, stock Material 3). It
  shows the live picture exactly as the PC gets it, a Wide / Auto / Main / Tele pill and a front
  switch on the picture, and a bottom sheet with the connection, Quality (4K / 1440p / 1080p),
  Start/Stop and Dark screen. Pulling the sheet up shows every setting. In landscape the sheet
  becomes a side panel. All five P30 camera options are discovered and switchable, with the
  quality-first rate policy and the thermal fallback seen working on the device.
- Windows: 49 unit tests pass. The app is in the Windows 11 Settings style. Home shows the preview,
  a status line, frame rate, bitrate, PC delay and drops, plus cards for the phone, the virtual
  camera, the lens and the quality. Phone, Virtual camera, Diagnostics and Settings pages hold the
  rest. It connects by itself at launch: over USB, or over Wi-Fi without typing an address,
  because it finds the phone on the network and follows its address when it changes. The
  receiver handles VPN subnet overlaps (Tailscale) and conflicting ADB installs (SuperDisplay).
- **Remote control from the PC**: lens, quality presets (Best picture, Balanced, Lowest delay) and
  focus, the same choices the phone offers, sent over the stream connection. Turning autofocus off
  holds focus where autofocus has it, for when it hunts; a slider fine-tunes. See `docs/ARCHITECTURE.md` §3.6.
- **Heat**: the phone's temperature shows on the PC. A hot phone already lowers bitrate and frame
  rate; if it stays hot for a minute, AirLens also steps down one quality preset (at most two) and
  steps back up after 5 cool minutes, without touching the saved choice. See `docs/ARCHITECTURE.md` §5.
- **Virtual camera works**: "AirLens (Windows Virtual Camera)" is a standard Windows camera at
  1920×1080 ~30 fps. It was verified with a Windows.Media.Capture client and with FFmpeg via
  DirectShow, the two APIs that Teams/Zoom/Chrome/OBS/Camera use; those apps themselves have not
  been tried yet. One-time elevated install, no kernel driver: see `docs/VIRTUAL_CAMERA.md`.
  Picture controls (Mirror, Rotate, Zoom up to 4×, Fit or Fill, Hide video) run on the GPU before
  the copy. While the camera is on, the PC and phone previews are off unless you ask for them.
- **Glass-to-glass latency measured** with an automated flash test (`AirLens.Probe --latency`):
  98 ms at 1080p and 138–182 ms at 4K over USB, with Wi-Fi adding ~15 ms. The PC side is ~1 ms
  plus one vsync; the rest is the phone's camera and encoder. Results and recommended profiles are
  in `docs/ARCHITECTURE.md` §6.1.
- Not yet done: the zero-copy path into the virtual camera (a shared GPU texture; today the
  finished 1080p frame is copied through shared memory), and screen-reader selection of the
  segmented choices (Narrator reads them but cannot pick one).
