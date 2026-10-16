using System.Diagnostics;
using System.Runtime.InteropServices;
using AirLens.Core.Pipeline;
using FFmpeg.AutoGen;

namespace AirLens.Probe;

/// <summary>
/// Automated glass-to-glass latency measurement ("flash test").
///
/// A full-screen window alternates black/white. Each change is timestamped right after Desktop
/// Window Manager composes it (DwmFlush), i.e. when it goes to the display. The phone films the
/// screen; for every decoded frame we compute the mean brightness of the picture centre and note
/// when it was decoded. Latency of one transition = first decoded frame past the brightness midpoint
/// minus the composition time of the flash that caused it.
///
/// That covers screen → camera exposure → ISP → GL → encoder → network → NVDEC decode. The monitor's
/// own input lag cancels out (the flash and the preview pass through the same display); putting the
/// decoded frame on screen adds the renderer's wait for the next vsync (0–1 refresh, ~8 ms average at 60 Hz).
/// </summary>
internal static unsafe class LatencyTest
{
    private readonly record struct Flash(long Ticks, bool White);
    private readonly record struct Sample(long DecodedTicks, double Luma);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    public static string Run(ReceiverPipeline pipeline, int warmupSeconds, int measureSeconds, Action<string> log)
    {
        var flashes = new List<Flash>();
        var samples = new List<Sample>();
        using var stop = new CancellationTokenSource();
        long measureFrom = 0;

        // Consumer: brightness of every decoded frame, stamped with its decode time.
        var consumer = new Thread(() =>
        {
            AVFrame* nv12 = ffmpeg.av_frame_alloc();
            long seen = 0;
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    if (!pipeline.Mailbox.WaitForNewFrame(seen, 100)) continue;
                    if (!pipeline.Mailbox.TryAcquire(ref seen, out var lease) || lease == null) continue;
                    double luma;
                    long decoded = lease.Meta.DecodedTicks;
                    using (lease) luma = CentreLuma(lease.Frame, nv12);
                    if (luma >= 0) lock (samples) samples.Add(new Sample(decoded, luma));
                }
            }
            finally
            {
                ffmpeg.av_frame_free(&nv12);
            }
        }) { IsBackground = true, Name = "Latency-Consumer" };
        consumer.Start();

        // Flasher: a top-most full-screen window on its own STA thread.
        var ui = new Thread(() =>
        {
            using var form = new Form
            {
                FormBorderStyle = FormBorderStyle.None,
                WindowState = FormWindowState.Maximized,
                TopMost = true,
                BackColor = Color.Black,
                ShowInTaskbar = false,
                Text = "AirLens latency test",
            };
            var label = new Label
            {
                Text = "AirLens latency test - keep the phone camera pointed at this screen",
                ForeColor = Color.Gray,
                BackColor = Color.Transparent,
                AutoSize = true,
                Font = new Font(FontFamily.GenericSansSerif, 14),
            };
            form.Controls.Add(label);
            form.Shown += (_, _) => label.Location = new Point(20, 20);
            bool white = false;
            var timer = new System.Windows.Forms.Timer { Interval = 600 };
            var started = Stopwatch.GetTimestamp();
            timer.Tick += (_, _) =>
            {
                if (stop.IsCancellationRequested)
                {
                    timer.Stop();
                    form.Close();
                    return;
                }
                white = !white;
                form.BackColor = white ? Color.White : Color.Black;
                form.Refresh(); // paint now
                DwmFlush();     // returns after DWM has composed the change for the display
                long now = Stopwatch.GetTimestamp();
                if (Stopwatch.GetElapsedTime(started, now) > TimeSpan.FromSeconds(warmupSeconds))
                {
                    if (Interlocked.Read(ref measureFrom) == 0) Interlocked.Exchange(ref measureFrom, now);
                    lock (flashes) flashes.Add(new Flash(now, white));
                }
            };
            timer.Start();
            Application.Run(form);
        }) { IsBackground = true, Name = "Latency-Flasher" };
        ui.SetApartmentState(ApartmentState.STA);
        ui.Start();

        log($"latency: flashing for {warmupSeconds} s warm-up (camera exposure settles) + {measureSeconds} s measurement");
        Thread.Sleep(TimeSpan.FromSeconds(warmupSeconds + measureSeconds));
        stop.Cancel();
        ui.Join(2000);
        consumer.Join(2000);

        return Analyse(flashes, samples, Interlocked.Read(ref measureFrom), log);
    }

    /// <summary>Mean Y over the central 50% of the picture (sparse grid), or -1 if unavailable.</summary>
    private static double CentreLuma(AVFrame* frame, AVFrame* nv12)
    {
        AVFrame* src = frame;
        if (frame->hw_frames_ctx != null)
        {
            ffmpeg.av_frame_unref(nv12);
            if (ffmpeg.av_hwframe_transfer_data(nv12, frame, 0) < 0) return -1;
            src = nv12;
        }
        int w = src->width, h = src->height, stride = src->linesize[0];
        byte* y = src->data[0];
        if (y == null || w <= 0 || h <= 0) return -1;
        long sum = 0;
        int count = 0;
        for (int row = h / 4; row < h * 3 / 4; row += 8)
        {
            byte* line = y + (long)row * stride;
            for (int col = w / 4; col < w * 3 / 4; col += 8)
            {
                sum += line[col];
                count++;
            }
        }
        return count == 0 ? -1 : (double)sum / count;
    }

    private static string Analyse(List<Flash> flashes, List<Sample> samples, long measureFrom, Action<string> log)
    {
        if (flashes.Count < 4 || samples.Count < 10) return $"latency: not enough data ({flashes.Count} flashes, {samples.Count} frames)";
        var measured = samples.Where(s => s.DecodedTicks >= measureFrom).OrderBy(s => s.DecodedTicks).ToList();
        if (measured.Count < 10) return "latency: no decoded frames during the measurement";

        // Brightness levels of the two states: robust low/high percentiles of what the camera saw.
        var sorted = measured.Select(s => s.Luma).OrderBy(v => v).ToList();
        double low = sorted[(int)(sorted.Count * 0.1)];
        double high = sorted[(int)(sorted.Count * 0.9)];
        double mid = (low + high) / 2;
        if (high - low < 20)
            return $"latency: the camera does not see the flashes (brightness {low:0}..{high:0}). Point it at the screen.";

        var latencies = new List<double>();
        for (int i = 0; i < flashes.Count; i++)
        {
            var flash = flashes[i];
            long until = i + 1 < flashes.Count ? flashes[i + 1].Ticks : long.MaxValue;
            var hit = measured.FirstOrDefault(s => s.DecodedTicks > flash.Ticks && s.DecodedTicks < until &&
                                                   (flash.White ? s.Luma > mid : s.Luma < mid));
            if (hit.DecodedTicks == 0) continue;
            latencies.Add(Stopwatch.GetElapsedTime(flash.Ticks, hit.DecodedTicks).TotalMilliseconds);
        }
        if (latencies.Count == 0) return "latency: no transitions detected";
        latencies.Sort();
        double Median(List<double> v) => v[v.Count / 2];
        double frameInterval = measured.Count > 1
            ? Stopwatch.GetElapsedTime(measured[0].DecodedTicks, measured[^1].DecodedTicks).TotalMilliseconds / (measured.Count - 1)
            : 0;
        log("latency: per transition (ms): " + string.Join(" ", latencies.Select(l => l.ToString("0"))));
        return $"latency: screen -> decoded frame over {latencies.Count}/{flashes.Count} transitions: " +
               $"median {Median(latencies):0} ms, min {latencies[0]:0} ms, max {latencies[^1]:0} ms " +
               $"(brightness {low:0}/{high:0}, decoded frame interval {frameInterval:0.0} ms)";
    }
}
