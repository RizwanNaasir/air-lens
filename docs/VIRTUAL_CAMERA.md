# AirLens virtual camera

AirLens registers a standard Windows camera, **"AirLens (Windows Virtual Camera)"**, for anything
that uses Windows.Media.Capture, Media Foundation or DirectShow, which covers Teams, Zoom,
Chrome/Edge, the Camera app and OBS ("Video Capture Device"). The Windows.Media.Capture and
DirectShow paths are verified below; the individual apps have not been tried yet. There is no kernel driver. It is a user-mode
COM DLL that Windows' Frame Server loads (design: [ARCHITECTURE.md §3.1](ARCHITECTURE.md)).

```
AirLens app (medium integrity)                    Frame Server (svchost, Local Service, session 0)
  decoder → GpuFrameMailbox                         AirLens.VirtualCamera.dll  (IMFMediaSource)
     │ D3D11 video processor: size, rotate, mirror,    ▲ creates the section + event, heartbeat,
     │ zoom, fit/fill (VirtualCameraGpuStage)          │
     │ GPU→CPU NV12, only while a client exists        │
     ▼                                                 │ reads frames, scales/fits, MEMediaSample
  Global\AirLensVirtualCamFrames  ──────────────────────┘
  (3-slot NV12 ring, seqlock)                       → every app that opens the camera (shared)
```

## Build

Needs Visual Studio 2022 Build Tools with the **Desktop development with C++** workload (MSVC v143)
and a Windows 11 SDK (10.0.22000+).

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" `
  windows\src\AirLens.VirtualCamera\AirLens.VirtualCamera.vcxproj /p:Configuration=Release /p:Platform=x64
```

The output is `bin\Release\AirLens.VirtualCamera.dll`. It uses the static CRT and depends only on
system DLLs (mfplat, ole32, advapi32, shell32, kernel32); `mfsensorgroup.dll` is resolved at
runtime, so the DLL still loads on Windows 10. The native project is deliberately not in
`AirLens.Windows.sln`, so `dotnet build` of the managed solution needs no C++ toolchain.

## Install (once, elevated)

The AirLens setup (`Setup.cmd`, built by `windows\installer\build.ps1`; see
[ARCHITECTURE.md §8](ARCHITECTURE.md)) installs the virtual camera together with the app, so that
is all a normal install needs. To install or update only the camera DLL while developing:

```powershell
Start-Process powershell -Verb RunAs -ArgumentList '-ExecutionPolicy Bypass -File windows\src\AirLens.VirtualCamera\install.ps1'
```

`install.ps1` copies the DLL to `C:\Program Files\AirLens\VirtualCamera\`, registers the COM class
under **HKLM** (Frame Server cannot see a user's HKCU classes), restarts the Frame Server services so
an updated DLL actually loads, and logs to `C:\ProgramData\AirLens\install.log`. To remove it, run
`install.ps1 -Uninstall`. Day-to-day use never elevates: the app creates the camera per user with
`MFVirtualCameraLifetime_Session`, so it disappears when AirLens exits (even after a crash).

## Use

- **WinUI app:** switch on **Virtual camera** on Home, or on its own page, which also shows whether
  an app is using it. `AirLens.App.exe --vcam` turns it on at launch. The app turns the camera back
  on by itself if it was on last time. It can be on before the phone connects; apps then see black.
- **Picture controls** (under Virtual camera on Home, and in the Picture section of its page):
  Mirror, Rotate 0/90/180/270° (on top of the phone's own rotation), Zoom 1–4× from the centre,
  Framing (Fit with bars, or Fill, which crops) and Hide video (apps get black, the stream keeps
  running). They are saved, except Hide, which is off again at the next start. The preview on Home
  shows the same picture, and it is off while the camera is on (the phone's too) unless you press
  "Show preview".
- **Probe:** `AirLens.Probe.exe --usb --vcam`. Add `--vcam-check` to open the camera as a
  Windows.Media.Capture client and report fps, format and content, and
  `--vcam-snapshot <file.png>` to save what an app receives.

Formats offered: NV12 **1920×1080** (default), 1280×720 and 3840×2160, at 30 fps. The app renders
each frame on the GPU at the size the client asked for, fitted or filled as chosen, so the source
copies it as is. If the app cannot (no D3D11 video processor), it sends the stream's own size and
the source scales it: an exact 2:1 ratio (4K phone → 1080p call) gets a 2×2 box filter, anything
else is fitted with black bars, never stretched.

## Verified (2026-10-15, Windows 11, RTX 4060 laptop, Huawei P30 over USB)

| Client | Result |
|---|---|
| FFmpeg via DirectShow (`-f dshow -i video="AirLens (Windows Virtual Camera)"`) | 29–30 fps, 1920×1080 |
| Windows.Media.Capture `MediaFrameReader` (`--vcam-check`) | 29.8–33 fps, NV12 1920×1080, live picture; 4:3 phone stream correctly pillarboxed |
| Picture controls (2026-10-16, `--vcam-check --vcam-snapshot`) | 32–33 fps, 1920×1080. 90° turns clockwise with side bars; Mirror flips left to right, also when turned; Fill crops a turned picture to fill the frame; 2× crops the centre; Hide gives black frames (mean luma 16) |
| Listed alongside | Camo, OBS Virtual Camera, Integrated Camera |
| App exit | Camera removed automatically; clean shutdown in ~2 s (≈1.6 s is Windows unregistering the camera) |

## Diagnostics

- Source trace (inside Frame Server): `C:\ProgramData\AirLens\vcam.log`. Every 2 s it reports
  sample requests, deliveries, how many carried a live frame, and the average delivery time.
- App side: `%LOCALAPPDATA%\AirLens\receiver.log` and the probe output, which report why the
  shared memory could not be opened (no client yet, access denied, layout mismatch).

## Lessons learned building it (worth keeping)

1. **Releasing the activator is not a shutdown.** `MFCreateVirtualCamera` and Frame Server keep using
   the source after releasing its `IMFActivate`. Shutting the source down in the activator's
   destructor made `MFCreateVirtualCamera` fail with `MF_E_SHUTDOWN` (`0xC00D3E85`).
2. **Frame Server keeps the DLL loaded.** After replacing it, restart `FrameServer` and
   `FrameServerMonitor`, or the old code keeps running. `install.ps1` does this.
3. **Objects created by a service carry a System integrity label.** With "no write up", a normal
   (medium-integrity) app is denied write access even when the DACL allows it. The section and event
   therefore get an explicit `S:(ML;;NW;;;ME)` label.
4. **For events, `SYNCHRONIZE` comes from generic execute, not generic read.** The event's DACL needs
   `GX` for `OpenEvent(SYNCHRONIZE | EVENT_MODIFY_STATE)`.
5. **No reference cycle between source and stream.** The stream holds a weak pointer to the source,
   cleared at shutdown, so nothing can leak inside the long-running Frame Server process.
6. **WinRT clients must dispose `VideoMediaFrame.SoftwareBitmap`**, not just the frame reference.
   Otherwise the reader's small buffer pool starves and frames trickle in at ~1 fps.
