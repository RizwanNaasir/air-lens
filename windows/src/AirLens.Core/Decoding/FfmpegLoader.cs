using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace AirLens.Core.Decoding;

/// <summary>
/// Loads the FFmpeg 8.0 shared DLLs (avcodec-62.dll, avutil-60.dll, ...) once per process and
/// routes FFmpeg's log into ours.
///
/// Search order: explicit directory, <c>AIRLENS_FFMPEG_DIR</c>, <c>&lt;app&gt;\ffmpeg</c>, the app directory.
/// </summary>
public static unsafe class FfmpegLoader
{
    private static readonly object Gate = new();
    private static bool _loaded;

    // GC root: FFmpeg keeps a raw pointer to this delegate for the life of the process. If it were
    // collected, the next log line from a decoder thread would jump into freed memory.
    private static av_log_set_callback_callback? _logCallback;
    private static Action<string>? _log;

    public static string? Directory { get; private set; }

    /// <returns>A human-readable version string, e.g. "libavcodec 62.11.100".</returns>
    /// <exception cref="DllNotFoundException">No usable FFmpeg build was found.</exception>
    public static string Load(string? directory = null, Action<string>? log = null)
    {
        lock (Gate)
        {
            if (!_loaded)
            {
                var dir = new[]
                    {
                        directory,
                        Environment.GetEnvironmentVariable("AIRLENS_FFMPEG_DIR"),
                        Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                        AppContext.BaseDirectory,
                    }
                    .FirstOrDefault(d => d != null && System.IO.Directory.Exists(d) && System.IO.Directory.EnumerateFiles(d, "avcodec-*.dll").Any())
                    ?? throw new DllNotFoundException(
                        "FFmpeg shared DLLs (avcodec-62.dll etc.) not found. Put an FFmpeg 8.0 'shared' build's bin folder " +
                        "in <app>\\ffmpeg or set AIRLENS_FFMPEG_DIR.");

                ffmpeg.RootPath = dir;
                DynamicallyLoadedBindings.Initialize();
                Directory = dir;
                _loaded = true;
            }
            if (log != null) SetLog(log);
            uint v = ffmpeg.avcodec_version();
            return $"libavcodec {v >> 16}.{(v >> 8) & 0xFF}.{v & 0xFF}";
        }
    }

    private static void SetLog(Action<string> log)
    {
        _log = log;
        if (_logCallback != null) return;
        _logCallback = (p0, level, format, vl) =>
        {
            if (level > ffmpeg.av_log_get_level()) return;
            const int size = 1024;
            byte* line = stackalloc byte[size];
            int printPrefix = 1;
            ffmpeg.av_log_format_line2(p0, level, format, vl, line, size, &printPrefix);
            var text = Marshal.PtrToStringUTF8((IntPtr)line)?.TrimEnd();
            if (!string.IsNullOrEmpty(text)) _log?.Invoke($"ffmpeg: {text}");
        };
        ffmpeg.av_log_set_level(ffmpeg.AV_LOG_WARNING);
        ffmpeg.av_log_set_callback(_logCallback);
    }

    public static string ErrorText(int error)
    {
        const int size = 256;
        byte* buffer = stackalloc byte[size];
        ffmpeg.av_strerror(error, buffer, size);
        return Marshal.PtrToStringUTF8((IntPtr)buffer) ?? $"error {error}";
    }
}
