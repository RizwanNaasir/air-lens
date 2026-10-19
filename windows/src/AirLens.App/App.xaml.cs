using AirLens.App.Tray;
using AirLens.Core.Pipeline;
using AirLens.Core.Status;
using Microsoft.UI.Xaml;

namespace AirLens.App;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayIcon? _tray;
    private SingleInstance? _instance;

    public App()
    {
        InitializeComponent();
        // Without this a XAML exception ends the process with nothing in our log.
        UnhandledException += (_, e) => AppController.Log($"unhandled UI exception: {e.Exception}");
    }

    /// <summary>Owner of the stream, the virtual camera and the settings; created at launch.</summary>
    public static AppController Controller { get; private set; } = null!;

    public static App Instance => (App)Current;

    /// <summary>Exit was chosen (or the installer asked): closing the window really closes it.</summary>
    public bool Quitting { get; private set; }

    /// <summary>
    /// Without arguments (the Start menu) the app does what the settings say: look for the phone
    /// (USB first, then the saved Wi-Fi address) and turn the virtual camera back on if it was on.
    /// Shortcut and test arguments: <c>--connect usb</c>, <c>--connect wifi:192.168.1.14</c>,
    /// <c>--vcam</c>, <c>--update-feed http://127.0.0.1:4174/latest.json</c> (a test release feed).
    /// If AirLens is already running, this launch shows that copy's window and ends.
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _instance = SingleInstance.Claim(ui, ShowWindow, Quit);
        if (_instance == null)
        {
            AppController.Log("already running: showing that window instead");
            SingleInstance.ShowRunning();
            Exit();
            return;
        }

        Controller = new AppController(ui);
        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            // The icon first: one whose process has ended lingers until the mouse passes over it.
            _instance?.Dispose();
            _tray?.Dispose();
            Controller.Shutdown();
        };
        _window.Activate();
        StartTray();

        var argv = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(argv, "--connect");
        if (i >= 0 && i + 1 < argv.Length)
        {
            var target = argv[i + 1];
            if (target.StartsWith("wifi:", StringComparison.OrdinalIgnoreCase)) Controller.ConnectWifi(target[5..]);
            else Controller.Connect();
        }
        else if (Controller.Settings.AutoConnect)
        {
            Controller.Connect();
        }
        if (argv.Contains("--vcam") || Controller.Settings.VirtualCamera) Controller.SetVirtualCamera(true);

        int f = Array.IndexOf(argv, "--update-feed");
        if (f >= 0 && f + 1 < argv.Length && Uri.TryCreate(argv[f + 1], UriKind.Absolute, out var feed)
            && AirLens.Core.Updates.UpdateFeed.IsAllowed(feed))
        {
            Controller.Updates.Feed = feed;
        }
        Controller.Updates.StartAutoCheck();
    }

    /// <summary>Hides the window to the notification area; false if there is no icon to come back from.</summary>
    public bool HideToTray()
    {
        if (_window == null || _tray is not { IsShown: true } tray) return false;
        _window.HideToTray();
        if (!Controller.Settings.TrayNoticeShown)
        {
            tray.ShowNotice("AirLens is still running",
                "The stream and the virtual camera keep going. Find AirLens next to the clock; right-click its icon to exit.");
            Controller.Settings.TrayNoticeShown = true;
            Controller.Settings.Save();
        }
        return true;
    }

    public void ShowWindow()
    {
        if (!Quitting) _window?.ShowFromTray();
    }

    /// <summary>Exit from the icon's menu or the installer's quit signal: the same shutdown as closing used to be.</summary>
    public void Quit()
    {
        if (Quitting) return;
        Quitting = true;
        AppController.Log("exit requested");
        if (_window != null) _window.Close(); // Closed: icon, single-instance names, Controller.Shutdown
        else Exit();
    }

    private void StartTray()
    {
        try
        {
            _tray = new TrayIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AirLens.ico"), TrayItems, OnTrayCommand, ShowWindow);
            _tray.SetTooltip(TrayMenu.Tooltip(Controller.Status));
            Controller.Updated += () => _tray?.SetTooltip(TrayMenu.Tooltip(Controller.Status));
        }
        catch (Exception e)
        {
            // Without an icon, closing the window exits as it did before (HideToTray says no).
            AppController.Log($"tray: no notification-area icon: {e.Message}");
            _tray = null;
        }
    }

    private IReadOnlyList<TrayItem> TrayItems()
    {
        var c = Controller;
        bool connected = c.Stats?.State is ReceiverState.Connected or ReceiverState.Streaming;
        return TrayMenu.Build(new TrayInputs(c.Status, c.IsRunning, connected, c.VirtualCameraOn, c.Stats?.Device));
    }

    private void OnTrayCommand(TrayItem item)
    {
        var c = Controller;
        AppController.Log($"tray: {item.Command}{(item.Argument is { } a ? " " + a : "")}");
        switch (item.Command)
        {
            case TrayCommand.Open:
                ShowWindow();
                break;
            case TrayCommand.Connect:
                c.Connect();
                break;
            case TrayCommand.Disconnect:
                c.Disconnect();
                break;
            case TrayCommand.VirtualCamera:
                c.SetVirtualCamera(!c.VirtualCameraOn);
                // The window may be hidden, so say why it didn't start where the user is looking.
                if (!c.VirtualCameraOn && c.VirtualCameraMessage.Length > 0)
                    _tray?.ShowNotice("The virtual camera didn't start", c.VirtualCameraMessage);
                break;
            case TrayCommand.Lens when item.Argument is { } key:
                c.SetLens(key);
                break;
            case TrayCommand.Exit:
                Quit();
                break;
        }
    }
}
