using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace AirLens.Probe;

/// <summary>
/// Opens the "AirLens" camera exactly as a third-party app would (Windows.Media.Capture → Frame
/// Server → our media source) and reports frame rate, format and picture content. Mean luma near
/// 16 with no variation means black frames, i.e. the source runs but the shared memory is not fed.
/// </summary>
internal static class VirtualCameraCheck
{
    public static async Task<string> RunAsync(int seconds, Action<string> log, string? snapshotPath = null)
    {
        await Task.Delay(3000); // let the stream start and the camera appear
        var groups = await MediaFrameSourceGroup.FindAllAsync();
        log("cameras: " + string.Join(", ", groups.Select(g => g.DisplayName)));
        var group = groups.FirstOrDefault(g => g.DisplayName.Contains("AirLens", StringComparison.OrdinalIgnoreCase));
        if (group == null) return "vcam-check: no AirLens camera visible to Windows.Media.Capture";

        using var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            SourceGroup = group,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        });
        var source = capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        var format = source.CurrentFormat;
        string formatText = $"{format.Subtype} {format.VideoFormat.Width}x{format.VideoFormat.Height} @ " +
                            $"{format.FrameRate.Numerator}/{format.FrameRate.Denominator}";
        log($"vcam-check: opened '{group.DisplayName}' as {formatText}");

        using var reader = await capture.CreateFrameReaderAsync(source);
        int frames = 0;
        var lumas = new List<double>();
        Windows.Graphics.Imaging.SoftwareBitmap? snapshot = null;
        reader.FrameArrived += (r, _) =>
        {
            using var frame = r.TryAcquireLatestFrame();
            // The bitmap must be disposed too: the reader recycles a handful of buffers, and an
            // undisposed bitmap holds one until the GC finalizes it (frames then trickle at ~1 fps).
            using var bitmap = frame?.VideoMediaFrame?.SoftwareBitmap;
            if (bitmap == null) return;
            int n = Interlocked.Increment(ref frames);
            if (n == 60 && snapshotPath != null)
            {
                snapshot = Windows.Graphics.Imaging.SoftwareBitmap.Convert(bitmap,
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore);
            }
            if (n % 15 != 0) return; // sample the content twice a second
            var buffer = new Windows.Storage.Streams.Buffer((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4));
            bitmap.CopyToBuffer(buffer);
            var bytes = buffer.ToArray();
            int lumaBytes = bitmap.PixelWidth * bitmap.PixelHeight;
            double sum = 0;
            int count = 0;
            for (int i = 0; i < Math.Min(lumaBytes, bytes.Length); i += 97) { sum += bytes[i]; count++; }
            lock (lumas) lumas.Add(count > 0 ? sum / count : 0);
        };
        var started = DateTime.UtcNow;
        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success) return $"vcam-check: reader start failed: {status}";
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await reader.StopAsync();
        double elapsed = (DateTime.UtcNow - started).TotalSeconds;
        if (snapshot != null && snapshotPath != null)
        {
            using var memory = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, memory);
            encoder.SetSoftwareBitmap(snapshot);
            await encoder.FlushAsync();
            memory.Seek(0);
            using var file = File.Create(snapshotPath);
            await memory.AsStreamForRead().CopyToAsync(file);
            snapshot.Dispose();
            log($"vcam-check: snapshot saved to {snapshotPath}");
        }

        double mean, spread;
        lock (lumas)
        {
            mean = lumas.Count > 0 ? lumas.Average() : 0;
            spread = lumas.Count > 0 ? lumas.Max() - lumas.Min() : 0;
        }
        return $"vcam-check: {frames} frames in {elapsed:0.0} s = {frames / elapsed:0.0} fps, {formatText}, " +
               $"mean luma {mean:0.0} (spread {spread:0.0}) -> {(mean > 20 ? "live picture" : "black frames")}";
    }
}
