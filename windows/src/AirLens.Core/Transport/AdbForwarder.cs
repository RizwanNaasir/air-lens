using System.Diagnostics;
using System.Text;

namespace AirLens.Core.Transport;

public enum AdbStatus
{
    Ok,
    AdbNotFound,
    NoDevice,
    Unauthorized,

    /// <summary>
    /// Another program ships a different ADB version and keeps restarting the shared ADB server
    /// (seen with SuperDisplay's bundled adb 1.0.40). Every restart tears down port forwards.
    /// </summary>
    ServerConflict,
    Failed,
}

public readonly record struct AdbResult(AdbStatus Status, string Message)
{
    public bool Ok => Status == AdbStatus.Ok;
}

/// <summary>The USB side of the link, as <see cref="StreamClient"/> needs it (a seam for tests).</summary>
public interface IUsbLink
{
    /// <summary>Makes the phone's port reachable on localhost; called before each USB connect.</summary>
    AdbResult Forward(int localPort, int remotePort);

    /// <summary>Cheap and side-effect free: is an authorized phone attached right now?</summary>
    bool PhoneAttached();
}

/// <summary>
/// Runs <c>adb forward tcp:L tcp:R</c> so the USB link looks like localhost. Called before every
/// USB connect attempt, so a re-plugged cable or a restarted ADB server heals by itself.
/// </summary>
public sealed partial class AdbForwarder : IUsbLink
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly Lazy<IReadOnlyList<string>> InstalledAdbs = new(FindInstalledAdbs);

    public AdbForwarder(string? adbPath = null)
    {
        AdbPath = adbPath ?? Locate();
    }

    /// <summary>The adb.exe in use. Can change at runtime to match another program's ADB server.</summary>
    public string? AdbPath { get; private set; }

    /// <summary>App-local copy first, then the Android SDK, then PATH.</summary>
    public static string? Locate()
    {
        var candidates = new List<string?>
        {
            Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe"),
            Env("ANDROID_HOME", "platform-tools", "adb.exe"),
            Env("ANDROID_SDK_ROOT", "platform-tools", "adb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        };
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (!string.IsNullOrWhiteSpace(dir)) candidates.Add(Path.Combine(dir.Trim(), "adb.exe"));
        }
        return candidates.FirstOrDefault(p => p != null && File.Exists(p));

        static string? Env(string name, params string[] parts) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } root ? Path.Combine([root, .. parts]) : null;
    }

    public AdbResult Forward(int localPort, int remotePort)
    {
        MatchRunningServer();
        var devices = Run("devices");
        if (!devices.Ok) return devices;
        var lines = DeviceLines(devices.Message);
        // A freshly (re)started ADB server needs a moment to enumerate USB devices.
        for (int attempt = 0; attempt < 4 && lines.Count == 0; attempt++)
        {
            Thread.Sleep(400);
            devices = Run("devices");
            if (!devices.Ok) return devices;
            lines = DeviceLines(devices.Message);
        }
        if (lines.Count == 0) return new(AdbStatus.NoDevice, "No phone found. Check the cable and that USB debugging is enabled.");
        if (lines.All(l => l.EndsWith("unauthorized"))) return new(AdbStatus.Unauthorized, "Accept the USB debugging prompt on the phone.");

        var forward = Run($"forward tcp:{localPort} tcp:{remotePort}");
        return forward.Ok ? new(AdbStatus.Ok, $"forwarded tcp:{localPort} -> phone tcp:{remotePort}") : forward;
    }

    /// <summary>
    /// Runs one adb command. If another program's ADB server has a different version, switch to an
    /// installed adb.exe of that version and retry once: two programs on the same version share one
    /// server, while different versions keep killing each other's server and every port forward.
    /// </summary>
    private AdbResult Run(string args)
    {
        var result = RunOnce(args, out string output);
        if (result.Status != AdbStatus.ServerConflict) return result;
        if (TryParseServerVersion(output) is int serverVersion && FindAdbWithVersion(serverVersion) is { } match && match != AdbPath)
        {
            AdbPath = match;
            // Our first call already replaced their server with ours, so the switched client
            // restarts it once more and reports the mismatch again. The next call is clean.
            var retry = RunOnce(args, out _);
            if (retry.Status == AdbStatus.ServerConflict) retry = RunOnce(args, out _);
            return retry.Status == AdbStatus.ServerConflict ? result : retry;
        }
        return result;
    }

    /// <summary>
    /// Asks the running ADB server for its protocol version through its own socket
    /// (<c>host:version</c> on port 5037), which disturbs nothing, then switches to an installed
    /// adb.exe of that version before any command runs. Without this, the first command from a
    /// mismatched client restarts the server, dropping every forward and costing seconds.
    /// </summary>
    private void MatchRunningServer()
    {
        if (QueryServerVersion() is not int server || AdbPath == null) return;
        if (CachedVersion(AdbPath) == server) return;
        if (FindAdbWithVersion(server) is { } match) AdbPath = match;
    }

    /// <summary>
    /// True if the running ADB server lists a phone in the "device" state (attached and USB
    /// debugging authorized). Asks the server through its socket (<c>host:devices</c>): no adb.exe
    /// process, no server restart, so it is cheap enough to poll every few seconds. No server
    /// running means no USB phone this AirLens can use.
    /// </summary>
    public bool PhoneAttached() => QueryDevices() is { } devices && devices.Any(d => d.State == "device");

    /// <summary>The server's device list as (serial, state), or null if no server is running.</summary>
    public static IReadOnlyList<(string Serial, string State)>? QueryDevices(int port = 5037)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"), out int envPort)) port = envPort;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            if (!client.ConnectAsync("127.0.0.1", port).Wait(300)) return null;
            client.ReceiveTimeout = 500;
            using var stream = client.GetStream();
            const string request = "host:devices";
            stream.Write(Encoding.ASCII.GetBytes($"{request.Length:x4}{request}"));
            var status = new byte[8]; // "OKAY" + 4 hex length
            stream.ReadExactly(status);
            var head = Encoding.ASCII.GetString(status);
            if (!head.StartsWith("OKAY")) return null;
            var body = new byte[Convert.ToInt32(head[4..8], 16)];
            stream.ReadExactly(body);
            return ParseDevices(Encoding.ASCII.GetString(body));
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or AggregateException or FormatException)
        {
            return null;
        }
    }

    /// <summary>"serial\tstate" lines, as both <c>host:devices</c> and <c>adb devices</c> print them.</summary>
    public static IReadOnlyList<(string Serial, string State)> ParseDevices(string text) =>
        text.Split('\n')
            .Select(l => l.Trim().Split('\t'))
            .Where(p => p.Length == 2 && p[0].Length > 0)
            .Select(p => (p[0], p[1].Trim()))
            .ToList();

    /// <summary>Returns the server's protocol version (e.g. 41), or null if no server is running.</summary>
    public static int? QueryServerVersion(int port = 5037)
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("ANDROID_ADB_SERVER_PORT"), out int envPort)) port = envPort;
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            if (!client.ConnectAsync("127.0.0.1", port).Wait(300)) return null;
            client.ReceiveTimeout = 500;
            using var stream = client.GetStream();
            const string request = "host:version";
            var bytes = Encoding.ASCII.GetBytes($"{request.Length:x4}{request}");
            stream.Write(bytes);
            var reply = new byte[12]; // "OKAY" + 4 hex length + 4 hex version
            stream.ReadExactly(reply);
            var text = Encoding.ASCII.GetString(reply);
            return text.StartsWith("OKAY") ? Convert.ToInt32(text[8..12], 16) : null;
        }
        catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException or AggregateException or FormatException)
        {
            return null;
        }
    }

    private static List<string> DeviceLines(string output) =>
        output.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("List of devices") && !l.StartsWith('*') && !l.StartsWith("adb server"))
            .ToList();

    /// <summary>"adb server version (40) doesn't match this client (41)" → 40.</summary>
    public static int? TryParseServerVersion(string adbOutput)
    {
        var m = ServerVersionRegex().Match(adbOutput);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"adb server version \((\d+)\) doesn't match this client")]
    private static partial System.Text.RegularExpressions.Regex ServerVersionRegex();

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int?> VersionCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Well-known copies first (milliseconds), the folder scan only if none matches.</summary>
    private static string? FindAdbWithVersion(int version)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string?[] known =
        [
            Locate(),
            Path.Combine(programFiles, "SuperDisplay", "adb", "adb.exe"),
            Path.Combine(programFiles, "scrcpy", "adb.exe"),
            Path.Combine(local, "Microsoft", "WinGet", "Links", "adb.exe"),
            @"C:\ProgramData\chocolatey\bin\adb.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "scoop", "shims", "adb.exe"),
        ];
        return known.Where(p => p != null && File.Exists(p)).Select(p => p!).FirstOrDefault(p => CachedVersion(p) == version)
               ?? InstalledAdbs.Value.FirstOrDefault(p => CachedVersion(p) == version);
    }

    private static int? CachedVersion(string path) => VersionCache.GetOrAdd(path, ClientVersion);

    /// <summary>"Android Debug Bridge version 1.0.41" → 41. <c>adb version</c> never touches the server.</summary>
    private static int? ClientVersion(string path)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(path, "version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            })!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var m = System.Text.RegularExpressions.Regex.Match(text, @"version 1\.0\.(\d+)");
            return m.Success ? int.Parse(m.Groups[1].Value) : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// adb.exe copies other programs install (SuperDisplay, scrcpy, phone suites), plus the usual
    /// SDK/PATH ones. Shallow scan of the program folders, done once.
    /// </summary>
    private static IReadOnlyList<string> FindInstalledAdbs()
    {
        var found = new List<string>();
        if (Locate() is { } preferred) found.Add(preferred);
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                 })
        {
            if (string.IsNullOrEmpty(root) || !System.IO.Directory.Exists(root)) continue;
            try
            {
                found.AddRange(System.IO.Directory.EnumerateFiles(root, "adb.exe", options));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private AdbResult RunOnce(string args, out string output)
    {
        output = "";
        if (AdbPath == null) return new(AdbStatus.AdbNotFound, "adb.exe not found. Install Android platform-tools or place adb next to AirLens.");
        var psi = new ProcessStartInfo(AdbPath, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        try
        {
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(Timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new(AdbStatus.Failed, $"adb {args} timed out");
            }
            output = stdout.Result + stderr.Result;
            if (output.Contains("doesn't match this client", StringComparison.OrdinalIgnoreCase))
            {
                return new(AdbStatus.ServerConflict,
                    "Another program is running a different ADB version and restarting the ADB server " +
                    "(for example SuperDisplay or a phone-manager app), and no installed adb.exe matches it. " +
                    "Close that program while using AirLens over USB.");
            }
            return p.ExitCode == 0 ? new(AdbStatus.Ok, output) : new(AdbStatus.Failed, output.Trim());
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new(AdbStatus.Failed, $"adb {args}: {e.Message}");
        }
    }
}
