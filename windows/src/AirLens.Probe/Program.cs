using AirLens.Core.Decoding;
using AirLens.Core.Pipeline;
using AirLens.Core.Protocol;
using AirLens.Core.Transport;
using AirLens.Core.VirtualCamera;
using AirLens.Probe;

// AirLens.Probe — headless end-to-end receiver.
//
//   AirLens.Probe --gpus                       list adapters and their H.264/HEVC decode support
//   AirLens.Probe [--usb] [--seconds 20]       receive over adb forward, decode, print stats
//   AirLens.Probe --wifi 192.168.1.14          receive over Wi-Fi
//   AirLens.Probe --decoder cuda|d3d11va|software
//   AirLens.Probe --ffmpeg C:\ffmpeg\bin       folder with FFmpeg 8.0 shared DLLs

var argv = args.ToList();
string? Value(string flag)
{
    int i = argv.IndexOf(flag);
    return i >= 0 && i + 1 < argv.Count ? argv[i + 1] : null;
}

if (argv.Contains("--gpus"))
{
    var adapters = GpuSelector.Enumerate();
    foreach (var a in adapters)
        Console.WriteLine($"{a}  vendor=0x{a.VendorId:X4}  H.264={a.SupportsH264}  HEVC={a.SupportsHevc}");
    Console.WriteLine($"chosen for H.264: {GpuSelector.Choose(adapters, VideoCodec.H264)?.Description ?? "none"}");
    Console.WriteLine($"chosen for HEVC:  {GpuSelector.Choose(adapters, VideoCodec.Hevc)?.Description ?? "none"}");
    return 0;
}

// --discover: list the phones that answer discovery on this PC's LAN adapters.
if (argv.Contains("--discover"))
{
    foreach (var (local, broadcast) in LanRouting.BroadcastTargets()) Console.WriteLine($"asking via {local} -> {broadcast}");
    var phones = await PhoneDiscovery.ScanAsync(TimeSpan.FromSeconds(1.5));
    foreach (var ph in phones) Console.WriteLine($"found: {ph.Name} at {ph.Address}:{ph.Port} id {ph.Id}{(ph.Busy ? " (busy)" : "")}");
    if (phones.Count == 0) Console.WriteLine("found: none");
    return phones.Count > 0 ? 0 : 4;
}

int port = int.TryParse(Value("--port"), out var p) ? p : ProtocolConstants.DefaultPort;
var endpoints = new List<StreamEndpoint>();
if (Value("--wifi") is { } host) endpoints.Add(StreamEndpoint.Wifi(host, port));
if (argv.Contains("--usb") || endpoints.Count == 0) endpoints.Insert(0, StreamEndpoint.Usb(port));
int seconds = int.TryParse(Value("--seconds"), out var s) ? s : 20;
var decoder = (Value("--decoder") ?? "auto").ToLowerInvariant() switch
{
    "cuda" => DecoderPreference.Cuda,
    "d3d11va" => DecoderPreference.D3D11VA,
    "software" => DecoderPreference.Software,
    _ => DecoderPreference.Auto,
};

var log = new Action<string>(m => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff}  {m}"));
using var pipeline = new ReceiverPipeline(new ReceiverOptions
{
    Endpoints = endpoints,
    Decoder = decoder,
    EnableVirtualCamera = !argv.Contains("--no-vcam"),
    FfmpegDirectory = Value("--ffmpeg"),
}, log);

try
{
    pipeline.Start();
}
catch (DllNotFoundException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}

// --vcam: create the per-user "AirLens" camera for the life of this process and feed it.
if (argv.Contains("--vcam"))
{
    var (state, message) = VirtualCameraHost.Start("AirLens");
    log($"virtual camera: {state} ({message}); installed={VirtualCameraHost.IsInstalled}");
}
// --vcam-check: open that camera like any app would (Windows.Media.Capture) and measure it.
Task<string>? check = argv.Contains("--vcam-check")
    ? Task.Run(() => VirtualCameraCheck.RunAsync(Math.Max(5, seconds - 6), log, Value("--vcam-snapshot")))
    : null;

// --latency: glass-to-glass flash test (the phone must be pointed at this PC's screen).
if (argv.Contains("--latency"))
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (pipeline.GetStats().State != ReceiverState.Streaming && DateTime.UtcNow < deadline) Thread.Sleep(100);
    if (pipeline.GetStats().State != ReceiverState.Streaming)
    {
        Console.Error.WriteLine("latency: no stream from the phone");
        return 3;
    }
    string result = LatencyTest.Run(pipeline, warmupSeconds: 3, measureSeconds: Math.Max(6, seconds), log);
    var ls = pipeline.GetStats();
    Console.WriteLine(result);
    Console.WriteLine($"latency: stream {ls.Stream}; decode {ls.DecodeMs:0.0} ms; rx->decoded {ls.EndToEndMs:0.0} ms; " +
                      $"rtt {ls.RttMs:0.0} ms (one way ~{ls.RttMs / 2:0.0}); playout hold {ls.HoldMs:0.0} ms; drops q{ls.QueueDrops}/s{ls.Superseded}");
    return 0;
}

// --lens <key> / --quality <best|balanced|fast>: remote control, the way the app's cards use it.
// Waits for the phone's DEVICE_STATE, asks for the change, then prints what the phone reports.
if (Value("--lens") is { } wantLens || Value("--quality") is not null)
{
    var deadline = DateTime.UtcNow.AddSeconds(15);
    while (pipeline.GetStats().Device == null && DateTime.UtcNow < deadline) Thread.Sleep(100);
    if (pipeline.GetStats().Device is not { } before)
    {
        Console.Error.WriteLine("control: the phone sent no DEVICE_STATE (older app?)");
        return 3;
    }
    void Print(string when, DeviceState d) => log(
        $"control {when}: {d.Device}, lens {d.ActiveLens?.Label ?? d.LensKey} of [{string.Join(", ", d.Lenses.Select(l => $"{l.Label}={l.Key}"))}], " +
        $"quality {d.Quality} of [{string.Join(", ", d.Presets.Select(p => $"{p.Id}: {p.Title}, {p.Summary}{(p.Direct ? ", direct" : "")}"))}]");
    Print("before", before);
    wantLens = Value("--lens");
    if (wantLens != null) pipeline.SetLens(wantLens);
    if (Value("--quality") is { } wantQuality) pipeline.SetQuality(wantQuality);
    var t0 = DateTime.UtcNow;
    while (DateTime.UtcNow - t0 < TimeSpan.FromSeconds(8))
    {
        Thread.Sleep(250);
        var d = pipeline.GetStats().Device;
        if (d != null && !ReferenceEquals(d, before) && (d.LensKey != before.LensKey || d.Quality != before.Quality)) break;
    }
    if (pipeline.GetStats().Device is { } after) Print($"after {(DateTime.UtcNow - t0).TotalSeconds:0.0} s", after);
}

// Consume frames like a renderer would (acquire newest, release immediately) so the mailbox and
// lease accounting are exercised exactly as in the app.
long seen = 0;
var consumer = new Thread(() =>
{
    while (true)
    {
        if (!pipeline.Mailbox.WaitForNewFrame(seen, 200)) continue;
        if (pipeline.Mailbox.TryAcquire(ref seen, out var lease)) lease!.Dispose();
    }
}) { IsBackground = true };
consumer.Start();

var end = DateTime.UtcNow.AddSeconds(seconds);
while (DateTime.UtcNow < end)
{
    Thread.Sleep(1000);
    var st = pipeline.GetStats();
    Console.WriteLine(
        $"{st.State,-12} {st.Decoder,-40} {st.DecodedFps,5:0.0} fps {st.ReceivedMbps,6:0.0} Mbps  " +
        $"decode {st.DecodeMs,5:0.0} ms  rx->decoded {st.EndToEndMs,5:0.0} ms  rtt {st.RttMs,5:0.0} ms  " +
        $"jitter {st.JitterMs,4:0.0} hold {st.HoldMs,4:0.0}  drops q{st.QueueDrops}/s{st.Superseded}/e{st.DecodeErrors} " +
        $"idr {st.KeyframeRequests}  pool {st.PoolOutstanding} leases {st.LiveLeases} leaked {st.LeakedLeases}" +
        (argv.Contains("--vcam") ? $"  vcam {(st.VirtualCameraConsumer ? "consumer" : "idle")} {st.VirtualCameraFrames} frames" : ""));
}
if (check != null) Console.WriteLine(await check);
if (argv.Contains("--vcam")) VirtualCameraHost.Stop();
return 0;
