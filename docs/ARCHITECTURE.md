# AirLens — Architecture

AirLens turns an Android phone into a low-latency PC webcam. Two native applications share one
lean transport:

```
 ┌──────────────────────────── Android (transmitter) ────────────────────────────┐
 │                                                                               │
 │  Camera2 ──► SurfaceTexture ──► GL pass (rotate/crop/mirror) ──► MediaCodec    │
 │  (sensor)    (GPU OES tex)      EGL window surface on                (HW H.264/│
 │                                 codec.createInputSurface()            H.265)   │
 │                                                                     │         │
 │                         Annex-B access units (pooled byte[]) ◄──────┘         │
 │                                       │                                       │
 │                               StreamServer (TCP :47474, 0.0.0.0)              │
 │                         ┌─────────────┴──────────────┐                        │
 │                     Wi-Fi LAN                 adb forward (USB)                │
 └─────────────────────────┼────────────────────────────┼────────────────────────┘
                           │      AirLens Protocol v1    │
 ┌─────────────────────────┼──────── Windows (receiver) ─┼────────────────────────┐
 │                   StreamClient (dedicated thread, reconnect + backoff)         │
 │                                       │                                        │
 │                       CompressedFrameQueue (backlog → skip to IDR)             │
 │                                       │                                        │
 │          FfmpegHwDecoder ── D3D11VA on the NVIDIA adapter (NVDEC silicon)      │
 │                         └─ CUDA hwaccel (NVDEC) / DXVA / software fallback     │
 │                                       │                                        │
 │                      PlayoutController (USB: realtime, Wi-Fi: adaptive)        │
 │                                       │                                        │
 │                     GpuFrameMailbox (latest-frame-wins, ref-counted AVFrames)  │
 │                   ┌───────────────────┴───────────────────┐                    │
 │        D3D11 SwapChainPanel preview            Shared-memory NV12 ring         │
 │        (WinUI 3, GPU NV12→RGB shader)          (only while a consumer exists)  │
 │                                                           │                    │
 │                                         AirLens.VirtualCamera.dll (C++ COM     │
 │                                         IMFMediaSource, loaded by Frame Server)│
 │                                                           │                    │
 │                                         Zoom / Teams / OBS / Chrome / Camera   │
 └────────────────────────────────────────────────────────────────────────────────┘
```

---

## 1. Decisions at a glance

| Area | Decision | Why |
|---|---|---|
| Android camera API | **Camera2** (not CameraX) | Exact control of AE FPS range, stabilization (EIS adds latency), noise-reduction mode, session parameters and output surfaces. CameraX's `VideoCapture` owns its own encoder; we need ours. |
| Android min SDK | **29 (Android 10)** | `KEY_MAX_B_FRAMES`, `KEY_PREPEND_HEADER_TO_SYNC_FRAMES`, thermal status API, `WIFI_MODE_FULL_LOW_LATENCY`, `MediaCodecInfo.isHardwareAccelerated`. Every 4K60-capable phone ships ≥ 10. |
| Encoder | **MediaCodec, Surface input, async callback, no B-frames, on-demand IDR** | Zero CPU copies of raw pixels; one tiny memcpy of the *compressed* access unit. |
| Rotation / aspect | **GLES pass between camera and encoder** (optional bypass) | Rotation and crop happen on the GPU before compression, without reconfiguring the encoder when the phone rotates. |
| Transport | **TCP, custom 20-byte header, sender-side frame dropping** (v1) | Only TCP works through `adb forward`; see §3.2 for the UDP evaluation and roadmap. |
| Windows language | **C# / .NET 8 + WinUI 3** for the app, **C++** for the virtual camera DLL | Managed code for the pipeline (fast to iterate, safe), native code only where Windows forces in-proc COM. |
| Decoder | **FFmpeg 8.x (FFmpeg.AutoGen 8.0)**, an **LGPL** shared build, native `h264`/`hevc` decoders with **hwaccel**, not the `*_cuvid` wrappers | hwaccel decoders emit frames with lower latency (no internal CUVID surface queue) and support D3D11 output for zero-copy rendering. Those decoders are LGPL code, so an LGPL build does everything AirLens needs; a GPL build would put the distributed app under the GPL (`installer\build.ps1` refuses one). Shipped: BtbN's `ffmpeg-n8.1-latest-win64-lgpl-shared-8.1`, verified at 4K HEVC and 1080p H.264 on the P30, with its licence and `THIRD-PARTY-NOTICES.txt`. |
| Rate priority | **Quality first** (default): under congestion or heat, lower **fps** before bits per frame | The owner prefers image quality over smoothness. "Keep fps" is the alternative (§3.5). |
| Lenses | **Every lens the HAL exposes** (separate IDs, physical sub-cameras, or zoom ratio), switchable live | On the P30: ultra-wide, main, telephoto, front and the logical "auto" camera (§6). |
| GPU path | **D3D11VA on the NVIDIA adapter** (default), **CUDA** (optional) | On NVIDIA drivers, D3D11 video decode runs on the same NVDEC engine. D3D11 keeps the frame a GPU texture all the way to the swap chain. |
| Virtual camera | **Media Foundation virtual camera (`MFCreateVirtualCamera`)**, DirectShow filter only as a Windows 10 fallback | No kernel driver. Visible to modern MF apps (Teams, Camera app, Chrome/Edge) and to OBS's "Video Capture Device" source. |

---

## 2. Codebase structure

```
AirLens/
├── README.md
├── docs/
│   ├── ARCHITECTURE.md            ← this file
│   └── PROTOCOL.md                ← wire format, byte-exact
├── tools/
│   └── probe-stream.ps1           ← protocol client: fps/bitrate/RTT, saves raw .h264 for ffprobe
│
├── android/                       ← Gradle project (AGP 9.4, Kotlin 2.3, Compose)
│   ├── settings.gradle.kts / build.gradle.kts / gradle.properties
│   ├── gradle/libs.versions.toml, gradle/wrapper/*
│   └── app/
│       ├── build.gradle.kts, proguard-rules.pro
│       └── src/
│           ├── main/
│           │   ├── AndroidManifest.xml
│           │   ├── res/ (strings, theme, notification icon)
│           │   └── java/net/silow/airlens/
│           │       ├── AirLensApp.kt                 Application: notification channel, log init
│           │       ├── MainActivity.kt               Compose host, stealth window flags, debug ADB start/stop hook
│           │       ├── ui/                           Design B "Viewfinder", stock Material 3
│           │       │   ├── AirLensScreen.kt          State; bottom sheet (portrait) or side panel (landscape)
│           │       │   ├── Viewfinder.kt             Preview, live badge and stats, lens pill, quick controls
│           │       │   ├── AdvancedSettings.kt       "All settings": every option, connection, stats, device report
│           │       │   ├── PreviewController.kt      Who draws the preview: idle session or the stream
│           │       │   └── CameraPreview.kt, Theme.kt TextureView host; dynamic or baseline dark scheme
│           │       ├── stream/
│           │       │   ├── StreamConfig.kt           Lens, size, fps, codec, bitrate ("Max"), priority, power mode
│           │       │   ├── QualityPresets.kt         Per lens: best / balanced / fast, built from its modes (§3.6)
│           │       │   ├── StreamStatus.kt           StateFlow bus service → UI
│           │       │   ├── StreamPipeline.kt         Camera + GL + encoder for one config; live lens switch
│           │       │   ├── PreviewSession.kt         Camera + GL → screen while nothing streams (720p capture)
│           │       │   ├── PreviewHub.kt             Hands the screen's surface to the running stream
│           │       │   └── StreamingService.kt       Foreground service (type=camera), control thread, recovery
│           │       ├── camera/
│           │       │   ├── LensCatalog.kt            Every exposed lens: IDs, physical sub-cameras, zoom ratio
│           │       │   ├── CameraCapabilities.kt     Sizes × fps × HW encoder; device report
│           │       │   └── CameraController.kt       Camera2 session, physical-camera outputs, AE/EIS/NR controls
│           │       ├── gl/
│           │       │   ├── EglCore.kt                EGL14 context, EGL_RECORDABLE_ANDROID
│           │       │   ├── FrameTransform.kt         Pure math: rotation/crop/mirror UV matrix
│           │       │   └── GlFrameProcessor.kt       OES texture → encoder surface, latest-frame latch
│           │       ├── encoder/
│           │       │   ├── EncoderSelector.kt        Picks HW encoder + profile + bitrate ceiling
│           │       │   ├── VideoEncoder.kt           MediaCodec async, feature negotiation, IDR/bitrate control
│           │       │   ├── BitrateController.kt      AIMD link-budget estimate
│           │       │   └── RatePolicy.kt             Budget + heat + priority → (fps, bitrate)
│           │       ├── net/
│           │       │   ├── Protocol.kt               Constants + header codec (shared spec)
│           │       │   ├── EncodedFrame.kt           Pooled frame with reserved header space
│           │       │   ├── SendQueue.kt              Byte/frame budget, drop-to-keyframe policy
│           │       │   ├── DeviceState.kt            DEVICE_STATE / CONTROL JSON (remote lens and quality)
│           │       │   ├── DiscoveryResponder.kt     Answers the PC's UDP discovery query (no IP typing)
│           │       │   ├── ClientSession.kt          Reader + writer threads, watchdog
│           │       │   └── StreamServer.kt           Accept loop, preemption, config cache
│           │       ├── power/
│           │       │   ├── PowerLocks.kt             Rotating partial wakelock + low-latency Wi-Fi lock
│           │       │   ├── ThermalGovernor.kt        Thermal status + battery-temperature fallback
│           │       │   ├── OrientationTracker.kt     Sensor-based rotation (works screen-off)
│           │       │   └── OemPowerGuide.kt          EMUI "App launch" / battery-optimisation shortcuts
│           │       └── util/AirLog.kt, HandlerExt.kt, DeviceName.kt, DeviceId.kt  File log; marketing name; hashed stable id
│           └── test/java/net/silow/airlens/          56 JVM tests: protocol, queue, loopback server, transform, rate policy, presets, discovery
│
└── windows/
    ├── AirLens.Windows.sln
    ├── Directory.Build.props
    ├── src/
    │   ├── AirLens.Core/                      ← all pipeline logic, no UI dependency, unit-tested
    │   │   ├── Protocol/       PacketHeader, PacketType, StreamInfo, ClientPackets
    │   │   ├── Transport/      StreamClient (socket loop), StreamEndpoint, AdbForwarder, LanRouting, PhoneDiscovery
    │   │   ├── Buffering/      PacketBuffer(+Pool), CompressedFrameQueue, PlayoutController
    │   │   ├── Decoding/       IVideoDecoder, FfmpegHwDecoder, GpuSelector, FfmpegLoader,
    │   │   │                   GpuFrameMailbox, FrameLease
    │   │   ├── VirtualCamera/  SharedFrameLayout, SharedMemoryFrameWriter, VirtualCameraHost
    │   │   └── Pipeline/       ReceiverPipeline (wires everything, owns threads)
    │   ├── AirLens.Probe/                     ← headless receiver: --gpus, USB/Wi-Fi decode + live stats
    │   ├── AirLens.App/                       ← WinUI 3 app, Windows 11 Settings style (Mica, NavigationView)
    │   │   ├── AppController.cs               Owns pipeline, virtual camera, settings; pages only read it
    │   │   ├── Pages/                         Home (preview, status, cards), Phone, Virtual camera, Diagnostics, Settings
    │   │   └── Rendering/                     D3D11 SwapChainPanel preview (NV12 → RGB shader)
    │   └── AirLens.VirtualCamera/             ← C++ in-proc COM media source (Frame Server), MSBuild project
    │       ├── include/AirLensSharedFrame.h   shared-memory contract (mirrors SharedFrameLayout.cs)
    │       ├── src/Activator, MediaSource, MediaStream, SharedFrameReader, dllmain (COM + HKLM registration)
    │       ├── src/Registration.cpp           MFCreateVirtualCamera start/stop exports
    │       └── install.ps1                    elevated one-time install, Frame Server restart (docs/VIRTUAL_CAMERA.md)
    ├── installer/                             ← build.ps1 → out\AirLens-<ver>\Setup.cmd (+ zip); install/uninstall scripts, icon generator
    └── tests/AirLens.Core.Tests/               40 xUnit tests: protocol, device state, discovery, queue policy, playout, loopback, ADB, LAN routing
```

---

## 3. Research evaluations

### 3.1 Virtual camera without a kernel driver

**Correction first:** `Windows.Media.Devices.Core` is the WinRT namespace for camera
*intrinsics* (`CameraIntrinsics`, `FrameController`, depth mapping). It does not create cameras.
The API you want is the Win32 **Media Foundation virtual camera**: `MFCreateVirtualCamera` in
`mfvirtualcamera.h` / `mfsensorgroup.lib`, available from **Windows 11 build 22000**.

How it works:

1. We ship a **user-mode in-process COM DLL** that implements `IMFMediaSource` (+ `IMFMediaSourceEx`,
   `IKsControl`) and one `IMFMediaStream` producing NV12 samples. No `.sys`, no INF, no driver
   signing, no test-signing mode. Defender/SmartScreen treat it like any signed application DLL.
2. Windows loads that DLL inside the **Frame Server** service (`svchost`, *Local Service*) and the
   **Frame Server Monitor** (*Local System*), plus the processes that enumerate it. Every app that
   opens the camera talks to Frame Server, so the source sees one consumer pipeline, and many apps
   can share the camera at once (a DirectShow filter cannot do that).
3. Our app calls `MFCreateVirtualCamera(SoftwareCameraSource, Session, CurrentUser, …, "{CLSID}")`
   and `Start()`. With `MFVirtualCameraLifetime_Session` the camera exists exactly as long as our
   `IMFVirtualCamera` object, so it disappears if AirLens exits or crashes, leaving no stale device.

**Can it be per-user with no UAC?** Partly. Stated precisely:

| Step | Elevation |
|---|---|
| Creating/starting the camera with `MFVirtualCameraAccess_CurrentUser` | **None.** Only `AllUsers` requires administrator rights (documented). |
| Registering the media source CLSID | **One-time admin, in HKLM.** The source is loaded by system services that cannot see the user's HKCU classes, so HKCU registration does not work. |
| DLL location | Must be readable by *Local Service* (e.g. `C:\Program Files\AirLens\`), **not** under `%USERPROFILE%`. |

So the honest answer is **"no driver, one UAC prompt at install time, zero prompts afterwards."**
The installer (MSIX with a COM server extension, or a signed MSI) does the HKLM registration once.
Daily use never elevates.

**Getting frames into the service.** The source runs in a different process, account and session
(session 0), so it cannot read our managed memory. v1 uses a **named shared-memory NV12 ring**
(`Global\AirLensVirtualCamFrames`, 3 slots, seqlock-style publish) plus an auto-reset event:

- The **media source creates** the section and event, because creating `Global\` objects needs
  `SeCreateGlobalPrivilege`, which services have and standard users do not. It applies a DACL that
  grants the interactive user read/write access.
- The app **opens** it, retrying cheaply until a consumer app actually starts the camera. While no
  one is watching, the app skips the GPU→CPU readback entirely, so an idle virtual camera costs nothing.
- Cost at 1080p60: 3.1 MB × 60 = ~187 MB/s of memcpy, about 1–2% of one core. At 4K60 it is ~750 MB/s,
  which is still fine on a modern desktop but measurable.
- **Phase 2 (zero-copy):** a shared `ID3D11Texture2D` (NT handle + keyed mutex) duplicated into the
  Frame Server process. The source hands the texture to MF through the `IMFDXGIDeviceManager` it
  receives. This removes the readback but needs careful cross-process handle brokering, so it comes after v1 works.

**Built and verified** (details, install and lessons learned: [VIRTUAL_CAMERA.md](VIRTUAL_CAMERA.md)):
"AirLens (Windows Virtual Camera)" delivers 1920×1080 NV12 at 29–33 fps to a Windows.Media.Capture
client and to FFmpeg through DirectShow, and Windows removes it when AirLens exits. Two details the
design above did not anticipate: the shared objects need an explicit **medium integrity label**
(service-created objects otherwise block writes from the app), and an updated DLL only takes effect
after **restarting Frame Server**.

**Picture controls run on the GPU.** Before the readback, a D3D11 video processor
(`VirtualCameraGpuStage`) turns the decoded texture into exactly the frame the app gets: the size
the app asked for (1920×1080 unless it asks otherwise), the phone's own rotation and mirror (direct
mode), and the picture controls on Home and the Virtual camera page. Those are Mirror, Rotate
(0/90/180/270°, added to the phone's), Zoom 1–4× (a centre crop; from a 4K stream 2× still has full
1080p detail), Framing (Fit with black bars, or Fill, which crops) and Hide (black frames while the
stream keeps running). Crop, scale, rotation and colour conversion are one pass on the GPU that
decoded the frame, and only the final NV12 frame is read back, so a 4K stream costs the same copy as
1080p. Mirror applies to the turned picture, so it is always left to right on screen. Verified with
probe snapshots of the camera at 0°, 90°, mirror, 90° plus mirror, 90° plus Fill, and 2×. Without a
video processor the old path (download and copy, without rotation, mirror or zoom) still feeds the camera.

**Preview off while the virtual camera is on.** The app the camera is for already shows the
picture, so the PC stops its preview renderer and tells the phone (`virtualCamera` in `CONTROL`),
which stops drawing its own preview in GL mode. This saves GPU work and battery on both sides; the
network stream is the same, because the camera needs it. Both offer "Show preview" for a check, and
the PC preview applies the same picture controls, so it shows what apps get.

**OBS:** an MF virtual camera appears in OBS as a normal *Video Capture Device*, so no OBS plugin is
needed. It is also listed for DirectShow clients, which was verified with FFmpeg `-f dshow`. A native OBS source plugin (consuming the same shared-memory ring) is an optional extra if a
user wants to skip the camera pipeline.

**Windows 10 fallback:** a DirectShow source filter. Because it is loaded into each client
process, it can usually be registered per user under `HKCU\Software\Classes` with no admin rights.
Apps that run elevated or as a service read only HKLM, so verify this per target app. Only
DirectShow apps see it (OBS, Zoom, Chrome's DShow path). It reads the same shared-memory ring, so it is a thin
adapter. Treat it as a stretch goal.

### 3.2 TCP vs. UDP for 4K@60 over unstable Wi-Fi

Numbers: 4K60 HEVC ≈ 35–60 Mbps, about 4–7 Mbit per IDR, and around 50–80 KB per P-frame. At 1080p60
H.264 ≈ 12–20 Mbps.

| | TCP + app-level dropping (v1) | UDP + custom reassembly/NACK/FEC |
|---|---|---|
| USB (`adb forward`) | **Works.** ADB only forwards TCP. | **Impossible** over `adb forward` (would need USB tethering/RNDIS). |
| Loss behavior | Retransmits lost segments; **head-of-line blocking** stalls every later byte for ≥ 1 RTT. On Wi-Fi bursts, cwnd collapse can add 100–300 ms. | Late or lost packets only affect their frame. NACK retransmit costs 1 RTT (2–10 ms on a LAN), and FEC hides isolated losses. |
| Picture integrity | Never corrupt, but it **stalls** | Corrupt until NACK repair or the next IDR, unless you also use intra-refresh or reference-frame invalidation. |
| Congestion control | Built in and battle-tested | You must write it (GCC/BBR-like pacing), or 4K60 bursts will overflow AP queues. |
| Implementation cost | Small | Large: fragmentation, pacing, NACK, FEC, reordering, congestion control, keyframe recovery. Essentially rebuilding SRT/WebRTC. |

**Verdict.** For **sustained 4K60 on genuinely lossy Wi-Fi**, a properly engineered UDP transport
(NACK + light FEC + paced sending + congestion control + intra-refresh) has **better tail latency**,
because one lost packet never freezes the stream. Raw UDP *without* those pieces is worse than TCP:
every lost packet corrupts a reference chain.

TCP is still the right **v1** for both links, because:

1. USB requires it, and USB is the sub-30 ms path.
2. On a healthy 5 GHz LAN (the target environment), loss is low enough that TCP's latency is dominated
   by our own queues, and we control those: `TCP_NODELAY`, a send buffer sized to ~100 ms of bitrate,
   DSCP AF41 (Wi-Fi WMM *video* access category), and a **sender-side drop-to-keyframe policy** that
   stops a stall from turning into a backlog.
3. The protocol header is already transport-agnostic (type, flags, sequence, timestamp), and the
   Windows side already handles missing frames (wait for IDR). That means `UdpTransport` can be added later
   without touching the decoder or the encoder.

**Roadmap:** v2 adds `UdpTransport` for Wi-Fi only: MTU-sized fragments carrying
`(frameSeq, fragIndex, fragCount)`, receiver NACKs, XOR FEC (1 parity packet per 10), sender pacing,
and receiver-report-driven bitrate. USB stays TCP.

### 3.3 Zero-copy Android pipeline

The rule: **raw pixels never touch CPU-visible memory.** They live in gralloc/AHardwareBuffer
memory and move between producers and consumers through `BufferQueue`s.

```
Camera HAL ──gralloc──► SurfaceTexture (OES texture, GPU) ──GLES draw──► EGLSurface
                                                                            │ (same gralloc buffers)
                                                     MediaCodec.createInputSurface() ──► HW encoder
```

Configuration details that make this zero-copy:

1. `MediaFormat.KEY_COLOR_FORMAT = COLOR_FormatSurface`, then `codec.configure(…, CONFIGURE_FLAG_ENCODE)`
   and **`codec.createInputSurface()` between `configure()` and `start()`**.
2. **Direct mode (no GL):** pass that `Surface` straight into the Camera2 capture session as an output
   target. The camera writes encoder-ready buffers directly. The size must be in
   `StreamConfigurationMap.getOutputSizes(MediaCodec::class.java)`. Caveat: Camera2 sets a rotation
   *transform hint* on the buffer, which **encoders ignore**, so the image stays in sensor orientation.
   AirLens then sends the needed rotation in `STREAM_INFO`, and the PC rotates for free in its shader.
   This is the most efficient 4K mode, and the lowest-latency one: on the P30 it is **44 ms faster
   glass-to-glass at 4K** and 13 ms faster at 1080p than GL mode (§6.1). Gap: only the preview
   applies the rotation and mirror today; the virtual-camera writer copies the frame as received.
3. **GL mode (default):** the camera renders into a `SurfaceTexture`. One GLES draw (rotate, crop to the
   chosen aspect, mirror) writes to an `EGLSurface` created on the encoder's input surface, using an
   EGL config with `EGL_RECORDABLE_ANDROID`. `eglPresentationTimeANDROID` passes the camera timestamp
   through. This is still zero-copy (GPU texture sampling, no `glReadPixels`) and costs a few percent of
   GPU at 4K60. It lets the encoder keep a **fixed output size** while the phone rotates, so rotation
   never restarts the encoder. The same draw also goes to the phone's on-screen preview, after the
   encoder has its copy and without waiting for the display (swap interval 0), so the phone shows
   exactly the frame the PC gets. Before a stream starts, `PreviewSession` runs the same GL stage
   from a 720p capture straight to the screen. In stealth mode the preview is not drawn at all, and
   direct mode has none while live (the camera feeds the encoder only).
4. **Latest-frame latch:** `SurfaceTexture.updateTexImage()` is FIFO. If the GL thread ever falls
   behind, we count pending `onFrameAvailable` calls and latch them all, drawing only the newest. Latency
   can never pile up inside the GPU stage.
5. **No B-frames** (`KEY_MAX_B_FRAMES = 0`), `KEY_LATENCY = 1`, `KEY_PRIORITY = 0` (realtime),
   `KEY_PREPEND_HEADER_TO_SYNC_FRAMES = 1` (SPS/PPS on every IDR, so a reconnecting PC can start
   instantly), vendor low-latency keys where known (Qualcomm `vendor.qti-ext-enc-low-latency.enable`).
   **`KEY_OPERATING_RATE = 240`**, far above the real frame rate: it tells the codec to run at full
   clock instead of pacing itself to 30 fps. On the P30 at 4K this cut the encoder's delay from
   ~93 ms to ~51 ms (glass-to-glass 209 → 182 ms). Like every optional key it goes through feature
   negotiation, so a codec that rejects it just runs without it.
6. The only CPU copy is the **compressed** output (~50–300 KB per frame) into a pooled `byte[]`. That is
   necessary because `releaseOutputBuffer()` must happen immediately, or the encoder stalls.

**4K@60 check:** a size×fps pair is offered only if (a) `getOutputMinFrameDuration(klass, size) ≤ 1/fps`,
(b) `CONTROL_AE_AVAILABLE_TARGET_FPS_RANGES` has an upper bound ≥ fps, and (c) a **hardware** encoder
reports `areSizeAndRateSupported(w, h, fps)`. Some OEMs expose 4K60 only to their own camera app through
vendor tags; those devices fall back to 4K30 or 1440p60.

**Thermal:** zero-copy removes the CPU part of the heat. What remains is the sensor, ISP, encoder and
radio. AirLens also: (1) requests `Window.setSustainedPerformanceMode(true)` while in stealth mode,
(2) disables EIS (which costs ISP power and adds 1–3 frames of latency), (3) listens to
`PowerManager` thermal status and steps bitrate down at MODERATE and fps down at SEVERE (fps changes go through
the repeating request, so the session is not reconfigured), and (4) keeps the panel dark (see §5).

### 3.4 Latency mitigation on the Windows side

**First, an honest latency budget.** The "< 30 ms USB" target is achievable for the **AirLens
pipeline** (encoder output → frame on screen). It is not achievable *glass-to-glass* on any phone:
the camera HAL alone holds 2–4 frames (`REQUEST_PIPELINE_MAX_DEPTH`), which is 33–66 ms at 60 fps
before AirLens sees a pixel. **Measured on the P30** (§6.1): 98 ms at 1080p to 182 ms at 4K over
USB, and Wi-Fi adds ~15 ms. Almost all of it is on the phone; the PC side is ~1 ms plus one vsync.
`AirLens.Probe --latency` measures it automatically (method in §6.1).

| Stage | USB | Wi-Fi (5 GHz) | Who controls it |
|---|---|---|---|
| Exposure + ISP + HAL pipeline | 33–66 ms | same | Camera HAL (EIS off saves 1–3 frames) |
| GL pass | < 1 ms | same | AirLens |
| HW encode (no B-frames) | 4–12 ms | same | SoC vendor, plus low-latency keys |
| **Transport** | **1–3 ms** | **3–15 ms (p99 ~40)** | AirLens queues + link |
| **NVDEC decode** (1080p / 4K) | **1–2 / 3–5 ms** | same | AirLens |
| **Playout hold** | **0 ms** | **0–25 ms adaptive** | AirLens |
| **Present** (flip model, waitable, latency 1) | **≤ 16.7 ms** | same | AirLens + DWM |
| **AirLens segment** | **~10–25 ms ✔ < 30** | **~25–60 ms ✔ < 100** | |

**Strategy: decode everything, display only the newest, never queue silently.**

1. **Never drop compressed frames selectively.** With no B-frames, every P-frame references the previous
   one, so dropping one corrupts the picture until the next IDR. Drops therefore happen only as *"drop
   everything up to the next keyframe"*, at two points:
   - **Sender (`SendQueue`)**: if queued bytes exceed ~100 ms of bitrate or 8 frames, drop all
     unsent frames, request an IDR (rate-limited to 4/s) and mark the next frame `DISCONTINUITY`.
     The camera and encoder never block on the network.
   - **Receiver (`CompressedFrameQueue`)**: if the decode backlog exceeds 3 frames / 50 ms (USB) or
     6 frames / 150 ms (Wi-Fi), drop to the newest queued keyframe, or flush and send
     `REQUEST_KEYFRAME`. When a keyframe arrives while frames are still queued, the older frames are
     discarded for free, because the keyframe resets every reference.
2. **Latest-frame-wins presentation (`GpuFrameMailbox`).** The decoder publishes into a one-slot
   mailbox. The renderer and the virtual-camera writer each take a reference at their own cadence.
   An unconsumed frame that gets overwritten is a *presentation* drop, which is harmless, because it was decoded.
3. **Adaptive playout (`PlayoutController`).** For each frame, `transit = arrival_local − pts_sender`.
   A windowed minimum of `transit` (2 s, 8 buckets) tracks the clock offset *and* drift without any
   clock sync. `jitter = transit − min`. The target hold is `clamp(2 × EWMA(jitter), 0, maxHold)`, with
   **`maxHold = 0` on USB** (pure realtime) and **≈ 1.5 frame intervals on Wi-Fi** (smooths motion
   under jitter). A frame that is already late is published immediately. The hold is serviced by the
   decoder thread's own wait timeout, so no extra thread or timer is needed.
4. **Presentation:** a flip-model composition swap chain with `FRAME_LATENCY_WAITABLE_OBJECT` and
   `SetMaximumFrameLatency(1)`. The render thread waits on the waitable, takes the newest frame,
   copies it GPU-side and presents. That is at most one vsync of display latency.
5. **Feedback loop:** the receiver sends a `RECEIVER_REPORT` every second (drops, decode backlog). The
   sender's link-budget estimate drops ×0.7 on congestion and recovers +5% per clean report after 2 s.
   How that budget is spent (frame rate or bits per frame) is the rate policy's job (§3.5).

### 3.5 Rate policy: quality first

The owner's stated priority is **highest bitrate over frame rate**, so the default is:

- **Start at the top.** First run picks the selected lens's best mode (4K30 on the P30), and the bitrate
  is "Max": the hardware encoder's advertised ceiling (`videoCapabilities.bitrateRange.upper`),
  measured at **60 Mbps H.264 / 80 Mbps H.265** on the P30.
- **Under congestion or heat, keep bits per frame and give up frames.** `RatePolicy` turns
  (link budget, thermal level, priority) into a target: `fps = snapDown(budget / bitsPerFrame)` onto
  rates the camera holds steadily (fixed AE ranges, e.g. 30 → 25 → 24 → 20 → 15 on the P30), and
  `bitrate = bitsPerFrame × fps`. Only below 15 fps does per-frame quality drop. Lowering fps is also
  the stronger thermal lever, because it cuts ISP, GPU, encoder and radio work together.
- **Resolution beats frame rate when choosing a sensor mode.** A lens whose native 4K mode is slower
  (the P30 telephoto: 4K at 20 fps) streams native 4K20 rather than 1080p30 upscaled.
- **"Keep fps"** (`RatePriority.SMOOTHNESS`) is the classic alternative: it holds frame rate and
  cuts bitrate instead.

Frame-rate changes go through the camera's repeating request (no session or encoder restart), and
bitrate changes through `PARAMETER_KEY_VIDEO_BITRATE`, so neither causes a visible glitch.

### 3.6 Quality presets and remote control

People should not have to choose a codec, a bitrate and a pipeline mode. For every resolution the
lens offers, the settings that measured best are bundled into one **quality preset** (§6.1):

| Preset | Resolution | Codec | Path | Why |
|---|---|---|---|---|
| `best` · Best picture | Largest the lens can stream (4K30 main, 4K20 tele) | H.265 | GL | Most picture per bit |
| `balanced` | Next size down above 1080p (1440p) | H.265 | GL | Sharp, and about 50 ms quicker |
| `fast` · Lowest delay | 1080p | H.264 | Direct when the camera allows it | ~100 ms glass-to-glass, and it matches the virtual camera's 1080p |

All three use the encoder's maximum bitrate. `QualityPresets.forModes` builds them per lens from the
modes the camera, the GL path and the hardware encoder accept, so a lens with fewer modes offers
fewer presets (the P30 front camera has no `balanced`), and a phone without H.265 gets H.264.
Settings changed in the phone's advanced options show as `custom`. Switching lens keeps the chosen
preset: "Best picture" on the telephoto is its own 4K20, not the main lens's 4K30.

The phone sends its lenses and presets to the PC in `DEVICE_STATE`, and the PC answers with
`CONTROL` (docs/PROTOCOL.md, "Remote control"). The Windows app therefore offers the same Lens and
Quality choices as the phone. Measured on the P30 over USB, a lens change is confirmed in about
0.3 s and a preset change (an encoder restart) in about 0.5 s.

**Focus.** Autofocus can hunt in low light, through glass or on plain walls. Home and the phone's
advanced settings offer Autofocus on or off and a Far to Near slider. Turning autofocus off sends
`focus: "lock"`, which fixes focus wherever autofocus has the lens at that moment (read from each
capture result's `LENS_FOCUS_DISTANCE`), so a sharp picture stays sharp and the slider fine-tunes
from there. Fixed focus sets `CONTROL_AF_MODE_OFF` and `LENS_FOCUS_DISTANCE`, scaled from 0
(infinity) to the lens's `LENS_INFO_MINIMUM_FOCUS_DISTANCE`, in the repeating request only, so
nothing restarts. A lens that reports no minimum focus distance has fixed focus and shows the
control disabled. Switching lens returns to autofocus. Verified on the P30 main lens from the PC:
lock (0.02 aimed far, 0.60 at a keyboard close by), 0.30, 0.75 and back to auto.

---

## 4. Connection resilience

Both sides treat "the other end vanished" as normal control flow, not an exception path.

**Android `StreamServer`**
- `ServerSocket` bound to `0.0.0.0:47474` with `SO_REUSEADDR`, so one listener serves Wi-Fi clients and
  `adb forward tcp:47474 tcp:47474` (which arrives on loopback).
- A **new authenticated client preempts the old one.** A PC app that crashed and restarted never waits
  for a half-dead socket to time out.
- Each session has a reader thread with `SO_TIMEOUT = 3 s` (the client pings every 500 ms). On timeout
  it closes the socket, which also unblocks a writer stuck in `write()` on a dead Wi-Fi link.
- Every socket error ends **that session only**. The encoder keeps running and frames are recycled to the pool.
- Camera disconnects and encoder errors restart the *pipeline* with exponential backoff (1 s → 10 s).
  The server, and the PC connection with it, stays up. Clients see a new `STREAM_INFO` epoch and reset their decoder.

**Windows `StreamClient`**
- A dedicated thread runs connect → handshake → read loop. Any `SocketException`, `IOException`,
  `ProtocolException` or timeout closes the socket, raises `Disconnected`, waits with backoff
  (250 ms → 2 s) and reconnects. The UI keeps the last frame with a "reconnecting" badge.
- On reconnect the decoder is flushed (not reallocated, unless codec or size changed). The phone
  replays its cached SPS/PPS and requests an IDR for the new viewer, so decoding starts at the next
  keyframe whether or not the encoder supports `PREPEND_HEADER` (the P30's does not need it).
- USB mode runs `adb forward` before each connect attempt (handles cable re-plugs). Endpoints are tried
  in priority order: USB first, then the phone's Wi-Fi address.
- **Back to USB by itself.** USB is the better link (≈3 ms round trip against ≈10 ms, about 15 ms
  less glass-to-glass, no Wi-Fi power-save spikes), but a healthy Wi-Fi session never ends on its
  own, so trying USB only on reconnect would keep a replugged phone on Wi-Fi. While on Wi-Fi with
  USB first in the list, a timer asks the ADB server every 3 s whether an authorized phone is
  attached (`host:devices` on its socket: no adb process, no server restart). When one is, the
  client runs `adb forward`, connects, sends HELLO and waits for the first packet, all next to the
  running Wi-Fi session. Only then does it close Wi-Fi, and the receive thread carries on over the
  already open USB socket (the phone switches viewers on the HELLO). If USB does not answer (a
  phone on the cable that is not streaming), Wi-Fi is untouched and the check rests for 30 s.
  Measured on the P30 with `adb reconnect` as the unplug: USB dropped, Wi-Fi took over in 165 ms,
  and the app was back on USB 2 s later, at 29.7 fps with no decode errors.
- **Nobody types the Wi-Fi address.** The app sends a UDP discovery query on every LAN adapter
  (`PhoneDiscovery`), every 3 s while disconnected and every 15 s while connected, and each phone
  running AirLens answers with its name and a stable id (docs/PROTOCOL.md, "Discovery"). The phone
  the PC is connected to, over either link, becomes "your phone" (its id arrives in
  `DEVICE_STATE`). Whenever that phone answers from a new address (a new DHCP lease), the saved
  address and the receiver's endpoint list follow it, with no restart. Pulling the USB cable
  therefore falls back to Wi-Fi by itself. Other phones on the network are only listed, with a
  Connect button, never joined automatically. Verified with the P30: found at 192.168.1.72 on the
  first scan; a stale saved address was replaced within half a second of connecting over USB.
- **Wi-Fi traffic is pinned to the LAN adapter.** A VPN can claim a subnet that overlaps the local
  Wi-Fi. On the development PC, a Tailscale subnet route for a remote 192.168.1.0/24 captured the
  route to the phone at 192.168.1.14: ping worked, but TCP connects timed out. For Wi-Fi endpoints
  `LanRouting` finds the local adapter whose subnet contains the phone and binds the socket to its
  address before connecting. Windows then routes only through that adapter (strong-host send), so
  no VPN or Tailscale setting needs changing.
- **ADB server conflicts heal themselves.** Some PC software ships its own ADB and restarts the
  shared ADB server whenever a different version talks to it, which silently drops every port
  forward. On the development PC this was SuperDisplay (a Windows service bundling adb 1.0.40,
  against the SDK's 1.0.41). Before any command, `AdbForwarder` asks the running server for its
  version through the ADB socket (`host:version` on port 5037, which disturbs nothing) and switches to an
  installed adb.exe of that version. Both programs then share one server. As a fallback it also
  reacts to the "server version (X) doesn't match this client (Y)" message. Measured effect: USB
  connect went from ~7 s (two server restarts) to ~0.2 s.

---

## 5. Screen and thermal strategy (Android)

| Mode | How | Use when |
|---|---|---|
| **Stealth** (default) | Activity stays foreground; full-screen black overlay; `screenBrightness = 0`; `FLAG_KEEP_SCREEN_ON`; sustained performance mode. **No wakelock.** Tap to reveal controls for 5 s. | **Wi-Fi.** The low-latency Wi-Fi lock is only honored while the app is foreground *and the screen is on*. On OLED, black pixels are unlit, so this is effectively "off". |
| **Screen off** | Foreground service (type `camera`) + `PARTIAL_WAKE_LOCK` + Wi-Fi lock; user presses power. | **USB**, or LCD phones where the backlight cannot go dark. Wi-Fi may drop to power-save, which adds 20–100 ms spikes. |

In screen-off mode the partial wake lock exists only while a stream is active. It is acquired when
the service receives the start command and released on stop and in `onDestroy`. Every 45 minutes
a fresh lock is acquired *before* the old one is released, so no single lock looks "abnormal" to
OEM watchdogs (§6), and each lock carries a 60-minute timeout as a last resort. Camera access
survives screen-off because the service is a foreground service with
`foregroundServiceType="camera"`, started while the activity is visible. Android 11+ requires that
for while-in-use permissions.

**Heat.** `ThermalGovernor` reports `normal`, `warm`, `hot` or `critical`, from the thermal status
API or, on HALs that always report NONE (the P30), from battery temperature at 41 / 44 / 47 °C. The
levels lower bitrate and frame rate in place, without a restart. That does not lower the
resolution, so a phone that stays hot keeps the ISP and encoder working at 4K. `ThermalStepper`
adds that step: after 60 s at `hot` or above (15 s at `critical`) the stream drops one quality
preset (4K → 1440p → 1080p), at most two steps, each needing another full hold, and it steps back up
one preset per 5 minutes at `normal`. Each step restarts the encoder (about a second), which is why
the holds are long. It changes only the settings the stream runs with; the saved choice stays, so
the phone and the PC can say "streams at 1440p for now". The temperature and the step travel in
`DEVICE_STATE` (`heat`): the PC shows the temperature on the Phone card and a warning while the
phone is hot or stepped down. The stepper is unit tested (`ThermalStepperTest`); a real step-down
has not been observed on the P30 yet, which stays near 40 °C on a desk.

---

## 6. Reference device: Huawei P30 (and how AirLens adapts to any phone)

The first test device is a **Huawei P30 ELE-L29** (Kirin 980, OLED, **EMUI 12.0, Android 10 /
API 29**, build 10.1.0.149). Nothing is hard-coded to it. Every limit below was **measured on the
device** (`dumpsys media.camera`, the vendor `media_codecs.xml`, and AirLens itself) and is
**discovered at runtime** on any other phone by `LensCatalog` / `CameraCapabilities`. The in-app
*Device report* also writes `device-report.txt` next to the log (below).

**Lenses, as exposed to third-party apps.** EMUI lists all five camera IDs. Camera `0` is a
LIMITED logical multi-camera over physicals `2`, `3`, `4`:

| AirLens label | ID | Sensor (pixel array) | Best streamable mode |
|---|---|---|---|
| Auto (multi-lens) | `0` (logical) | 7296×5472 | 2160p30 |
| Main | `2` | 7296×5472 (40 MP) | 2160p30 |
| Ultra-wide | `3` | 4608×3456 (16 MP) | 2160p30 |
| Telephoto | `4` | 3264×2448 (8 MP) | **2160p20** (1080p30) |
| Front | `1` | 6528×4896 (32 MP) | 1440p30 4:3 / 1080p30 |

Lens labels come from field of view, not from the HAL's zoom figures. The P30 reports sensor sizes
that put its marketed 0.6×/3× lenses at ~0.4×/~2.1×, so AirLens names lenses by kind only.

| Topic | Measured on the P30 | How AirLens handles it generically |
|---|---|---|
| Frame rate | **All lenses top out at 30 fps.** AE ranges: `[12,15] [15,15] [14,20] [20,20] [24,24] [14,25] [25,25] [14,30] [30,30]`. 60 fps exists only in a constrained high-speed session at 1080p, which AirLens does not use (batched, two-surface limit) | Modes are offered only if size × rate passes camera, AE and encoder checks (§3.3). The steady rates (15/20/24/25/30) feed the quality-first policy (§3.5) |
| Encoders | `OMX.hisi.video.encoder.avc`: up to 3840×2160, **60 Mbps**. `OMX.hisi.video.encoder.hevc`: up to 3840×2160, **80 Mbps** | "Max" bitrate = the selected encoder's ceiling. `EncoderSelector` picks by capability, never by name |
| Encoder quirks | `configure()` fails (`0x80001001`) if `KEY_PROFILE` is set **without `KEY_LEVEL`**, because Android's ACodec requires both. With the level it accepts **H.264 High**. `prepend-sps-pps-to-idr-frames` is rejected outright. Accepted: no B-frames, profile+level, CBR, realtime priority, latency 1, operating rate, BT.709 | Profile is always sent with the codec's highest advertised level. **Feature negotiation** (`VideoEncoder`): find the longest working prefix of optional keys, retry each rejected key on its own, and cache the result per codec/size/rate. One unsupported key never stops a stream, and never costs the keys that do work |
| SPS/PPS on IDR | Not relied on | The server caches `CODEC_CONFIG` and replays it to every new session |
| Thermal API | **Confirmed: reports NORMAL while the battery is at 41 °C** | `ThermalGovernor` falls back to **battery temperature** (`ACTION_BATTERY_CHANGED`, sticky, no permission) with 41 / 44 / 47 °C steps. Observed live: after ~12 min of 4K 60 Mbps while charging, battery 41.0 °C → WARM → camera capped at 24 fps, quality per frame kept |
| Background killing | EMUI PowerGenie/`HwPFWService` **force-stops apps holding a wakelock > 60 min** | **Stealth mode holds no wakelock at all** (keep-screen-on keeps the CPU awake). Screen-off mode swaps in a new wakelock every 45 min before releasing the old one. The app links to *Settings → Battery → App launch* ("Manage manually", all toggles on) |
| App logs | **EMUI drops third-party `Log.i/d`** unless "AP Log" is enabled in a hidden service menu | `AirLog` mirrors lifecycle/config/error lines to a pullable file: `adb pull /sdcard/Android/data/net.silow.airlens/files/airlens.log` |
| Wi-Fi | 802.11ac 2×2 on 5 GHz. Low-latency lock support depends on the vendor HAL | API 29–33: hold `FULL_LOW_LATENCY` **and** `FULL_HIGH_PERF` (whichever the HAL honors applies). API 34+: low-latency only |
| Display | OLED | Stealth black overlay ≈ panel off (unlit pixels) |
| USB | ADB over USB-C. EMUI needs *Developer options → "Allow ADB debugging in charge only mode"* to keep ADB up when the USB mode is "charging only" | The Windows app re-runs `adb forward` on every reconnect and detects ADB server conflicts (§4) |

**Measured end to end** (P30 → USB `adb forward` → RTX 4060 Laptop, D3D11VA, WinUI preview,
2026-10-15, Auto lens, 2160p30 H.264 High, Max = 60 Mbps CBR, GL mode):

| Metric | Result |
|---|---|
| Frame rate delivered and decoded | 29.6–29.9 fps |
| Bitrate on the wire | 55–62 Mbps |
| Connect → first decoded frame | ~0.2 s after the TCP connect; ~0.6 s from app launch |
| Dropped frames (queue / superseded / decode errors) | 0 / 0 / 0 over 15 s runs |
| NVDEC decode time per 4K frame (D3D11VA) | 0.3–0.4 ms |
| Last byte received → frame decoded | 0.3–0.4 ms |
| USB round trip (PING/PONG) | 2.7–4.5 ms |
| Leaks (pool outstanding / live leases / leaked leases) | 0 / 0 / 0 |
| Offline cross-check, same capture through FFmpeg CLI | D3D11VA 186 fps, CUDA 251 fps |

**Over Wi-Fi** (phone 5 GHz ch 153, −43 dBm, 526 Mbps link; PC Wi-Fi 6 on the same AP; 25–30 s runs):

| Metric | 4K30 H.264 High, Max (60 Mbps) | 4K30 H.265 Main, Max (80 Mbps) |
|---|---|---|
| Frame rate | 29.4–30.8 fps | 29.4–30.3 fps |
| Bitrate on the wire | 54–62 Mbps | 58–66 Mbps (the encoder's practical ceiling) |
| Dropped frames | 0 | 0 |
| NVDEC decode | 0.3 ms | 0.5 ms |
| Round trip | 7–20 ms (≈10 typical) | 7–14 ms |
| Jitter (EWMA) / smoothing hold | 5–24 ms / 9–49 ms (≈17 typical) | 6–27 ms / 12–50 ms (cap) |
| Connect | 0.25 s from app launch | same |

The adaptive hold behaves as designed. It is 0 on USB, and on Wi-Fi it tracks twice the measured
jitter, up to the cap of 1.5 frame intervals (50 ms at 30 fps). That trades up to one frame of delay
for even motion, and keeps Wi-Fi well inside the 100 ms budget. Both codecs reach about the same
bitrate on this phone, so **H.265 is the quality-first choice**: the same bits buy noticeably more
picture.

The **PC-side** segment, from last byte received to a decoded frame ready for display, is under 1 ms,
well inside the 30 ms USB budget (§3.4). Transport and phone-side encode come on top of that.
Glass-to-glass results are in §6.1.

Two startup bugs found and fixed during this run: the receiver fed SPS/PPS to FFmpeg as a standalone
packet (now held and prepended to the next keyframe), and a decode error flushed the queued keyframe
that would have repaired it (the queue now drops only up to the next queued keyframe and keeps
requesting one).

**P30 starting profile:** Auto or Main lens, 2160p30, H.264 at Max (60 Mbps) over USB. Over Wi-Fi,
H.265 at Max (80 Mbps) gives the most quality per bit, if the link sustains it. Otherwise the policy
steps fps down. For video calls through the virtual camera, 1080p is the better choice (§6.1).

Optional (advanced, the user's own choice, reversible): on EMUI 9/10 PowerGenie can be disabled for
the current user with `adb shell pm uninstall -k --user 0 com.huawei.powergenie`, and restored with
`adb shell cmd package install-existing com.huawei.powergenie`. AirLens does not depend on this.

### 6.1 Glass-to-glass latency

**Method: the flash test (`AirLens.Probe --latency`).** A full-screen window flips between black and
white every 600 ms. Each flip is timestamped once Desktop Window Manager has composed it
(`DwmFlush`). The phone films the screen. For every decoded frame the probe takes the mean brightness
of the picture centre and the time it was decoded. One sample is the first decoded frame past the
brightness midpoint, minus the timestamp of the flip that caused it. That covers monitor → exposure
→ ISP → GL → encoder → link → decode. The monitor's own lag is in the number, which stands in for the
lag the preview has on the same monitor. Left out is the renderer's wait for the next vsync (0–1
refresh, ~8 ms on average at 60 Hz). On the phone, `PipelineTiming` logs its own share every 3 s:
camera timestamp (start of exposure) to the GL latch, and to encoder output.

```bash
adb shell am start -n net.silow.airlens/.MainActivity --ez autostart true --es lens 2 --ei width 1920 --ei height 1080 --ez direct true
```

```bash
AirLens.Probe.exe --usb --latency --seconds 10 --no-vcam
```

Use `--wifi <phone IP>` instead of `--usb` for Wi-Fi. The debug build also accepts `--es template
preview|record` and `--ei oprate <n>` for experiments (`DebugTuning`).

**Over USB** (2026-10-15, P30 main lens aimed at the laptop screen, 30 fps, Max bitrate, ~16
samples per run; every row except the first uses operating rate 240):

| Mode | Codec | Screen → decoded (median) | Sensor → GL | Sensor → encoded |
|---|---|---|---|---|
| 4K GL, operating rate 30 (old default) | H.264 | 209 ms | 91.5 ms | 184.8 ms |
| 4K GL | H.264 | 182 ms | 89.4 ms | 140.3 ms |
| 4K GL | H.265 | 169 ms | 94.6 ms | 145.8 ms |
| 4K direct | H.264 | **138 ms** | — | 112.8 ms |
| 4K direct | H.265 | 145 ms | — | 106.8 ms |
| 1440p GL | H.264 | 123 ms | 71.0 ms | 93.5 ms |
| 1080p GL | H.264 | 111 ms | 61.9 ms | 78.7 ms |
| 1080p direct | H.264 | **98 ms** | — | 69.9 ms |

**Over Wi-Fi** (same setup, round trip 6–7 ms, playout hold 18–20 ms):

| Mode | Codec | Screen → decoded, median (min–max) | vs. USB |
|---|---|---|---|
| 4K direct | H.264 | 151 ms (122–367) | +13 ms |
| 4K GL | H.265 | 185 ms (158–201) | +16 ms |
| 1080p direct | H.264 | 114 ms (73–134) | +16 ms |

**What the numbers say:**

- **The phone is the latency.** In the 4K GL H.264 run (182 ms): ~89 ms from start of exposure to
  the GL stage (exposure, ISP, HAL pipeline depth), ~51 ms for the GL draw plus the encoder, ~2 ms for
  USB and decode. The remaining ~40 ms comes before the camera timestamp: monitor response and
  scanout, waiting for the next exposure to start (0–33 ms at 30 fps), and the rolling shutter
  reaching the centre of the picture.
- **Operating rate 240 is a free 27 ms at 4K** and is now the default (§3.3).
- **Resolution is the biggest lever.** The camera HAL and the encoder both take longer per 4K frame;
  1080p is 70–85 ms faster than 4K.
- **Direct mode saves 44 ms at 4K** and 13 ms at 1080p, with the same picture, because it skips the GL
  pass. It is not the default yet, because the virtual camera does not apply the receiver-side
  rotation and mirror (§3.3).
- **Codec and camera template barely matter.** H.265 and H.264 are within ~10 ms of each other.
  `TEMPLATE_RECORD` and `TEMPLATE_PREVIEW` gave identical results (182 vs. 181 ms at 4K, 111 vs.
  111 ms at 1080p), so AirLens keeps `TEMPLATE_RECORD`.
- **Wi-Fi adds ~15 ms**, mostly the playout hold that smooths jitter. The 367 ms maximum in the 4K
  direct run was an outlier, not the norm: that run's median was 151 ms.

**Against the targets.** The part AirLens controls (encoder output → frame on screen) is ~2 ms plus
one vsync on USB and ~25 ms plus one vsync on Wi-Fi, inside the < 30 ms and < 100 ms targets. Full
glass-to-glass at 4K is well above both, set by the phone's camera and encoder. No PC-side change can
recover it.

**Recommended profiles on the P30:**

| Goal | Profile | Glass-to-glass, USB / Wi-Fi |
|---|---|---|
| Best picture (default) | 4K30 GL, H.265 Max | 169 / 185 ms |
| Best picture, less delay | 4K30 direct mode (preview only, until the virtual camera rotates) | 138–145 / 151 ms |
| Video calls | 1080p30 direct mode | 98 / 114 ms |

The virtual camera publishes 1920×1080, so for calls a 4K stream adds 70+ ms and buys only a
better downscale.

---

## 7. Memory management for multi-hour streams

The design goal is **zero steady-state allocation on every per-frame path**, so a 6-hour session has
the same heap profile as a 6-minute one.

### Android
| Technique | Where |
|---|---|
| **Pooled `EncodedFrame` objects** with a reserved 20-byte header prefix: the encoder copies the AU at offset 20, the session writes the header in place, and one `write()` sends it all. Buffers grow to the high-water mark (power of two) and are reused forever. | `EncodedFrame.kt`, `SendQueue.kt` |
| **Ownership is single and explicit**: pool → encoder → server → session queue → writer → pool. Every exit path (drop, overflow, session close, no client) calls `recycle()`. The pool counts `outstanding` frames, and the status UI shows it, so a leak is visible immediately. | all `net/` |
| **Bounded everything**: the send queue is limited by bytes *and* frames, and the pool caps retained frames (64). Nothing grows with stream length. | `SendQueue`, `EncodedFramePool` |
| **MediaCodec output buffers are released in `finally`**, even if the copy throws. A leaked output buffer stalls the encoder within ~4 frames. | `VideoEncoder.onOutputBufferAvailable` |
| **Native resources are released in reverse construction order**, each in its own `runCatching`, so one failure cannot skip the rest: capture session → camera device → SurfaceTexture/Surface → EGL surfaces/context → codec → encoder input `Surface` → `HandlerThread.quitSafely()`. | `StreamPipeline.close()` |
| **No per-frame lambdas or boxing**: GL `Runnable`s and the `FloatBuffer`s and matrices are preallocated, and the header `ByteBuffer` is a reused view. | `GlFrameProcessor`, `ClientSession` |
| **Wake/Wi-Fi locks** are non-reference-counted and released in `finally` plus `onDestroy`, and they carry a timeout as a last resort. | `PowerLocks.kt` |

### Windows
| Technique | Where |
|---|---|
| **`PacketBufferPool`**: socket payloads are read directly into pooled `byte[]`s that only ever grow, so steady-state LOH allocations are 0. An `Outstanding` counter catches leaks. | `Buffering/` |
| **One `AVPacket` and one `AVFrame` per decoder for its whole life**, with `av_frame_unref` after every receive. Packets are non-refcounted views over the pinned pooled buffer, and `avcodec_send_packet` copies them into its own padded buffer, so the pin is released immediately. | `FfmpegHwDecoder` |
| **GPU surface pool sizing**: `extra_hw_frames = 4` covers the mailbox slot and one lease per consumer. Leases are `IDisposable` and hold their own `AVFrame` ref, released right after the GPU copy. Holding refs longer would starve the decoder (a stall, not a leak), and the mailbox counts live leases. | `GpuFrameMailbox`, `FrameLease` |
| **Native callbacks are GC-rooted**: the `get_format` and `av_log` delegates are stored in fields for the decoder's lifetime. A collected delegate becomes an access violation hours into a stream. | `FfmpegHwDecoder`, `FfmpegLoader` |
| **Deterministic native teardown**: `avcodec_free_context` → `av_buffer_unref(hw_device_ctx)` → D3D11 device release, in `Dispose` with a finalizer safety net and an idempotent guard. | `FfmpegHwDecoder.Dispose` |
| **Bounded queues only**: `CompressedFrameQueue` (frames + ms budget) and a one-slot mailbox. Backlog turns into dropped frames, never memory. | `Buffering/` |
| **GC mode**: Server GC off, Concurrent GC on, `GCSettings.LatencyMode = SustainedLowLatency` while streaming. With zero steady-state allocation, gen-2 collections effectively never happen. | `ReceiverPipeline` |
| **Shared memory**: one `MemoryMappedFile` view, mapped once and reused. The GPU→CPU readback and copy run only while the camera client's heartbeat is fresh, so an idle virtual camera costs no bandwidth. | `SharedMemoryFrameWriter`, `ReceiverPipeline` |

---

## 8. Build and run

```
# Android
cd android && ./gradlew :app:assembleDebug :app:testDebugUnitTest
adb install -r app/build/outputs/apk/debug/app-debug.apk

# USB link (the Windows app does this automatically when "USB" is selected)
adb forward tcp:47474 tcp:47474

# Debug builds: drive the phone from the PC
adb shell am start -n net.silow.airlens/.MainActivity --ez autostart true --es lens 2 --es codec HEVC --ei bitrate 0
adb shell am start -n net.silow.airlens/.MainActivity --ez autostop true
adb pull /sdcard/Android/data/net.silow.airlens/files/airlens.log

# Protocol-level check without the Windows app
powershell -File tools/probe-stream.ps1 -Seconds 10 -Out stream.h264
ffprobe -hide_banner stream.h264

# Windows
cd windows && dotnet build -c Release && dotnet test
# An FFmpeg 8.x LGPL *shared* build (bin\*.dll and LICENSE.txt; BtbN's win64-lgpl-shared) -> src/AirLens.App/ffmpeg/
# or set AIRLENS_FFMPEG_DIR
src/AirLens.Probe/bin/Release/.../AirLens.Probe.exe --gpus          # adapter + NVDEC profile check
src/AirLens.Probe/bin/Release/.../AirLens.Probe.exe --usb --seconds 30
```

Verified on the development PC (RTX 4060 Laptop + Radeon iGPU, 150% scaling): `--gpus` selects the
RTX 4060 for both H.264 and HEVC; the probe and the WinUI app receive and decode the P30's 4K stream
live (§6). `AirLens.App.exe --connect usb` (or `--connect wifi:<ip>`) connects on launch. The
receiver log is at `%LOCALAPPDATA%\AirLens\receiver.log`. FFmpeg used: gyan.dev 8.0.1
full_build-shared, from the GyanD/codexffmpeg GitHub release, SHA-256 verified.

The virtual camera DLL needs Visual Studio 2022 (Desktop C++) and the Windows 11 SDK.

**Install as a normal app.** `windows\installer\build.ps1` builds the camera DLL, publishes the app
self-contained (no .NET or Windows App SDK install on the target PC), and writes
`windows\installer\out\AirLens-<version>\` plus a zip of it. Running `Setup.cmd` from that folder asks
for administrator rights once, then installs:

| What | Where |
|---|---|
| App | `C:\Program Files\AirLens\App\AirLens.App.exe` |
| Virtual camera | `C:\Program Files\AirLens\VirtualCamera\`, COM class in HKLM |
| Start menu shortcut | All users' Start menu → "AirLens" (`-DesktopShortcut` adds one on the desktop) |
| Settings → Apps entry | "AirLens", publisher silow.net; Uninstall runs `C:\Program Files\AirLens\uninstall.ps1` |
| Per-user settings and log | `%LOCALAPPDATA%\AirLens\` (`settings.json`, `receiver.log`; kept on uninstall) |

Running `Setup.cmd` again updates in place. Launched without arguments (from the Start menu), the
app restores the last session: USB or Wi-Fi and the phone IP, the virtual-camera switch, and
reconnects if it was connected when closed. The setup is 314 MB (119 MB zipped). Most of that is
libavcodec (101 MB) and the .NET and Windows App SDK runtimes. Only the three FFmpeg libraries the
decoder loads are shipped: avcodec, avutil and swresample.

The setup ships an LGPL FFmpeg build with its licence and `THIRD-PARTY-NOTICES.txt`;
`build.ps1` refuses a GPL build (§1). The scripts are unsigned, so SmartScreen will warn on a
downloaded copy. USB mode also expects adb on the PC (Android platform-tools, or one that another
app installed; see §4).

**Updates from Settings.** The app keeps itself current from the public GitHub releases
(`AirLens.Core/Updates`, `AirLens.App/Updates/UpdateService.cs`):

- **Check.** About once a day, 20 s after start-up (Settings › Check for updates automatically,
  on by default), or on demand, it reads GitHub's latest-release API. Drafts and pre-releases are
  never "latest". The tag (`v0.1.1`) is compared with the app's own version, all four parts filled
  in, because `System.Version` ranks 0.1.0 below 0.1.0.0. A newer release puts a badge on Settings.
- **Install** is always the user's click, after a dialog that warns that the virtual camera stops
  for a few seconds. The app downloads `AirLens-<version>-windows-x64.zip` and checks its size and
  SHA-256 against the release before anything runs. The SHA-256 comes from GitHub's own asset
  `digest`, or a `<sha256>  <file>` line in the notes as a fallback; a release with neither is
  refused, and a file that does not match is deleted. Then it unpacks the zip and runs its
  `Setup.cmd`. That is the same installer as a manual install: Windows asks for administrator
  rights once (Program Files and the machine-wide camera need them, so a silent update is not
  possible with this install layout), the installer closes AirLens, updates it in place and starts
  it again. A declined prompt or failed install is reported in Settings. Only HTTPS is accepted,
  except a feed on this PC (`--update-feed http://127.0.0.1:…/latest.json`) for testing. Old
  update files are removed at the next start.
- **Verified** on this PC with a local feed: 0.1.0 found 0.1.0.1, refused a copy whose SHA-256 was
  wrong (nothing kept, app still running), then downloaded the real one, ran setup after the
  Windows prompt and came back as 0.1.0.1 from Program Files. That install then checked the real
  GitHub feed and reported "up to date".
- **Releasing an update:** push a tag `v<version>`. `.github/workflows/release.yml` builds and
  tests both apps on GitHub with that version (`build.ps1 -Version`, Gradle `-PappVersion`;
  Android's versionCode is major×10000 + minor×100 + patch) and publishes the release with
  `AirLens-<version>-windows-x64.zip` and `AirLens-<version>-android.apk` under exactly those
  names. Nothing is committed afterwards: the website reads the version, links, sizes and
  checksums from GitHub's latest release when the page loads, as the updater does. The APK is
  signed with the key in the repository secrets, the one earlier APKs used, so phones update in
  place. Installed Windows copies offer the release within a day. Only the repository admin can
  create a version tag, so only the admin can release (README, Repository rules).
  `build.ps1 -Version 0.1.0.1` still builds a local test package. Copies of 0.1.0 predate the
  updater and need a later version installed by hand once.
