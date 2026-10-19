using AirLens.App.Updates;
using AirLens.Core.Status;
using AirLens.Core.Decoding;
using AirLens.Core.Pipeline;
using AirLens.Core.Transport;
using AirLens.Core.VirtualCamera;
using Microsoft.UI.Dispatching;

namespace AirLens.App;

/// <summary>
/// The one owner of the receiver, the virtual camera and the settings. Pages never touch the
/// pipeline directly: they read <see cref="Stats"/> when <see cref="Updated"/> fires (twice a
/// second, on the UI thread) and call the methods here. Navigating between pages therefore never
/// interrupts the stream.
/// </summary>
public sealed class AppController
{
    private readonly DispatcherQueue _ui;
    private readonly DispatcherQueueTimer _timer;
    private ReceiverPipeline? _pipeline;
    private ReceiverState _lastState = ReceiverState.Stopped;
    private readonly CancellationTokenSource _discoveryStop = new();
    private bool _settingsDirty;

    public AppController(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(500);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        _ = DiscoveryLoopAsync(_discoveryStop.Token);
        _ = UsbLoopAsync(_discoveryStop.Token);
        Updates = new UpdateService(ui, Settings);
        Look = new VirtualCameraLook(Settings.CameraMirror, Settings.CameraRotation, Settings.CameraZoom, Settings.CameraFill);
    }

    /// <summary>What is on the USB cable, from ADB every 2 s (cheap: one socket query, no adb.exe run).</summary>
    public UsbPresence Usb { get; private set; } = UsbPresence.Unknown;

    /// <summary>
    /// The two statuses every page shows (phone there? streaming here?), from one place so the pages
    /// never disagree. See <see cref="ConnectionStatus"/>.
    /// </summary>
    public ConnectionStatus Status => ConnectionStatus.From(new ConnectionInputs(
        IsRunning ? Stats : null, IsRunning, StartError, Usb, Phones, Settings.PhoneId, Settings.PhoneName,
        StreamingSince is { } since ? DateTime.Now - since : null));

    private async Task UsbLoopAsync(CancellationToken ct)
    {
        bool adbInstalled = AdbForwarder.Locate() != null;
        while (!ct.IsCancellationRequested)
        {
            var usb = await Task.Run(() => ReadUsb(adbInstalled), ct).ConfigureAwait(true);
            if (usb != Usb)
            {
                Usb = usb;
                Raise();
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static UsbPresence ReadUsb(bool adbInstalled)
    {
        if (!adbInstalled) return UsbPresence.NoAdb;
        // No ADB server yet means nothing has looked; AirLens starts it on its first USB attempt.
        if (AdbForwarder.QueryDevices() is not { } devices) return UsbPresence.NoPhone;
        if (devices.Any(d => d.State == "device")) return UsbPresence.Ready;
        if (devices.Any(d => d.State == "unauthorized")) return UsbPresence.Unauthorized;
        return devices.Count > 0 ? UsbPresence.Unresponsive : UsbPresence.NoPhone; // "offline", "authorizing"
    }

    /// <summary>New versions from the GitHub releases (Settings › Updates).</summary>
    public UpdateService Updates { get; }

    /// <summary>How the virtual camera frames the picture for apps (also shown in the preview).</summary>
    public VirtualCameraLook Look { get; private set; }

    public void SetLook(VirtualCameraLook look)
    {
        Look = look with
        {
            RotationDegrees = VirtualCameraGeometry.Normalize(look.RotationDegrees),
            Zoom = Math.Round(Math.Clamp(look.Zoom, 1.0, VirtualCameraLook.MaxZoom), 2),
        };
        Settings.CameraMirror = Look.Mirror;
        Settings.CameraRotation = Look.RotationDegrees;
        Settings.CameraZoom = Look.Zoom;
        Settings.CameraFill = Look.Fill;
        _settingsDirty = true; // saved on the next tick: a dragged zoom slider changes it many times a second
        if (_pipeline != null) _pipeline.VirtualCameraLook = Look;
        Raise();
    }

    /// <summary>Manual focus 0 (far) .. 1 (near), or null for autofocus.</summary>
    public void SetFocus(double? value) => _pipeline?.SetFocus(value);
    public void LockFocus() => _pipeline?.LockFocus();

    /// <summary>Phones that answered the last discovery scan on Wi-Fi.</summary>
    public IReadOnlyList<DiscoveredPhone> Phones { get; private set; } = [];

    public AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>Latest snapshot; null while disconnected on purpose (<see cref="Disconnect"/>).</summary>
    public ReceiverStats? Stats { get; private set; }

    public bool IsRunning => _pipeline != null;
    public ReceiverPipeline? Pipeline => _pipeline;

    /// <summary>When the current stream started showing pictures; null when not streaming.</summary>
    public DateTime? StreamingSince { get; private set; }

    /// <summary>Set when the receiver cannot start at all (e.g. FFmpeg missing).</summary>
    public string? StartError { get; private set; }

    public bool VirtualCameraOn { get; private set; }
    public string VirtualCameraMessage { get; private set; } = "";

    /// <summary>The D3D11 device the decoder renders on; the preview must use the same one.</summary>
    public Vortice.Direct3D11.ID3D11Device? DecoderDevice { get; private set; }

    /// <summary>Raised on the UI thread about twice a second and after every change.</summary>
    public event Action? Updated;

    /// <summary>Raised on the UI thread when the decoder moves to a (new) D3D11 device.</summary>
    public event Action<Vortice.Direct3D11.ID3D11Device?>? DecoderDeviceChanged;

    /// <summary>Starts looking for the phone: USB first, then the saved Wi-Fi address.</summary>
    public void Connect()
    {
        Stop();
        var pipeline = new ReceiverPipeline(new ReceiverOptions { Endpoints = Endpoints(), Decoder = DecoderPreference }, Log)
        {
            VirtualCameraLook = Look,
        };
        pipeline.SetVirtualCameraActive(VirtualCameraOn);
        pipeline.DecoderDeviceChanged += device => _ui.TryEnqueue(() =>
        {
            if (!ReferenceEquals(pipeline, _pipeline)) return;
            DecoderDevice = device;
            DecoderDeviceChanged?.Invoke(device);
        });
        try
        {
            pipeline.Start();
            StartError = null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or InvalidOperationException)
        {
            pipeline.Dispose();
            StartError = ex.Message;
            Log($"cannot start: {ex.Message}");
            Raise();
            return;
        }
        _pipeline = pipeline;
        Tick();
    }

    /// <summary>Saves the phone's Wi-Fi address and reconnects with it as the fallback to USB.</summary>
    public void ConnectWifi(string host)
    {
        Settings.Host = host.Trim();
        Settings.Save();
        Connect();
    }

    /// <summary>A phone discovery found: remember it as "the" phone and connect to it.</summary>
    public void ConnectPhone(DiscoveredPhone phone)
    {
        Settings.PhoneId = phone.Id;
        ConnectWifi(phone.Address.ToString());
    }

    /// <summary>USB first (lowest delay), then the phone's Wi-Fi address if one is known.</summary>
    private List<StreamEndpoint> Endpoints()
    {
        var endpoints = new List<StreamEndpoint> { StreamEndpoint.Usb() };
        if (!string.IsNullOrWhiteSpace(Settings.Host)) endpoints.Add(StreamEndpoint.Wifi(Settings.Host.Trim()));
        return endpoints;
    }

    /// <summary>
    /// Scans Wi-Fi for phones every few seconds while not connected, and now and then while
    /// connected (cheap: one small broadcast). When the remembered phone answers from a new
    /// address, the saved address and the receiver's endpoint list follow it, so pulling the USB
    /// cable or a new DHCP lease never needs the address typed again.
    /// </summary>
    private async Task DiscoveryLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var found = await PhoneDiscovery.ScanAsync(TimeSpan.FromMilliseconds(800), ct: ct);
                // Continuations run on the UI thread (the loop was started there).
                Phones = found;
                FollowRememberedPhone(found);
                Raise();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is System.Net.Sockets.SocketException or IOException)
            {
                Log($"discovery: {e.Message}");
            }
            bool connected = Stats?.State is ReceiverState.Connected or ReceiverState.Streaming;
            try
            {
                await Task.Delay(connected ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void FollowRememberedPhone(IReadOnlyList<DiscoveredPhone> found)
    {
        if (string.IsNullOrEmpty(Settings.PhoneId)) return;
        if (found.FirstOrDefault(p => p.Id == Settings.PhoneId) is not { } mine) return;
        string address = mine.Address.ToString();
        if (address == Settings.Host) return;
        Log($"discovery: {mine.Name} is at {address} on Wi-Fi (was {(Settings.Host.Length > 0 ? Settings.Host : "unknown")})");
        Settings.Host = address;
        Settings.Save();
        _pipeline?.SetEndpoints(Endpoints());
    }

    /// <summary>Stops looking for the phone until <see cref="Connect"/>.</summary>
    public void Disconnect()
    {
        Stop();
        Raise();
    }

    public void SetVirtualCamera(bool on)
    {
        if (on == VirtualCameraOn) return;
        if (on)
        {
            var (state, message) = VirtualCameraHost.Start("AirLens");
            VirtualCameraOn = state == VirtualCameraState.Running;
            VirtualCameraMessage = VirtualCameraOn ? "" : message;
            Log($"virtual camera: {state} ({message})");
        }
        else
        {
            VirtualCameraHost.Stop();
            VirtualCameraOn = false;
            VirtualCameraMessage = "";
        }
        Settings.VirtualCamera = VirtualCameraOn;
        Settings.Save();
        _pipeline?.SetVirtualCameraActive(VirtualCameraOn);
        Raise();
    }

    public void SetLens(string key) => _pipeline?.SetLens(key);
    public void SetQuality(string presetId) => _pipeline?.SetQuality(presetId);

    public DecoderPreference DecoderPreference => Settings.Decoder switch
    {
        "d3d11va" => DecoderPreference.D3D11VA,
        "cuda" => DecoderPreference.Cuda,
        "software" => DecoderPreference.Software,
        _ => DecoderPreference.Auto,
    };

    /// <summary>Saves the decoder choice; a running connection restarts to use it.</summary>
    public void SetDecoder(string decoder)
    {
        if (decoder == Settings.Decoder) return;
        Settings.Decoder = decoder;
        Settings.Save();
        if (IsRunning) Connect();
    }

    public void Shutdown()
    {
        _timer.Stop();
        _discoveryStop.Cancel();
        if (_settingsDirty) Settings.Save();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Stop();
        Log($"shutdown: pipeline {clock.ElapsedMilliseconds} ms");
        VirtualCameraHost.Stop();
        Log($"shutdown: virtual camera {clock.ElapsedMilliseconds} ms");
    }

    private void Stop()
    {
        var pipeline = _pipeline;
        _pipeline = null;
        Stats = null;
        StreamingSince = null;
        _lastState = ReceiverState.Stopped;
        if (DecoderDevice != null)
        {
            DecoderDevice = null;
            DecoderDeviceChanged?.Invoke(null);
        }
        pipeline?.Dispose();
    }

    private void Tick()
    {
        if (_settingsDirty)
        {
            _settingsDirty = false;
            Settings.Save();
        }
        if (_pipeline is { } p)
        {
            var s = p.GetStats();
            Stats = s;
            if (s.State == ReceiverState.Streaming && _lastState != ReceiverState.Streaming) StreamingSince = DateTime.Now;
            else if (s.State != ReceiverState.Streaming) StreamingSince = null;
            _lastState = s.State;
            // Whatever the link, the phone we are connected to becomes "the" phone.
            if (s.Device?.Id is { Length: > 0 } id && id != Settings.PhoneId)
            {
                Settings.PhoneId = id;
                Settings.Save();
                FollowRememberedPhone(Phones);
            }
            if (s.Device?.Device is { Length: > 0 } phoneName && phoneName != Settings.PhoneName)
            {
                Settings.PhoneName = phoneName;
                _settingsDirty = true;
            }
        }
        Raise();
    }

    private void Raise() => Updated?.Invoke();

    // %LOCALAPPDATA%\AirLens\receiver.log, rotated at 1 MB, for field diagnostics.
    public static readonly string LogFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AirLens");
    private static readonly string LogFile = Path.Combine(LogFolder, "receiver.log");
    private static readonly object LogGate = new();

    public static void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine($"AirLens: {message}");
        lock (LogGate)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                var info = new FileInfo(LogFile);
                if (info.Exists && info.Length > 1_000_000) File.Move(LogFile, LogFile + ".1", overwrite: true);
                File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
    }
}
