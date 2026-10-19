using Microsoft.UI.Xaml;

namespace AirLens.App;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        // Without this a XAML exception ends the process with nothing in our log.
        UnhandledException += (_, e) => AppController.Log($"unhandled UI exception: {e.Exception}");
    }

    /// <summary>Owner of the stream, the virtual camera and the settings; created at launch.</summary>
    public static AppController Controller { get; private set; } = null!;

    /// <summary>
    /// Without arguments (the Start menu) the app does what the settings say: look for the phone
    /// (USB first, then the saved Wi-Fi address) and turn the virtual camera back on if it was on.
    /// Shortcut and test arguments: <c>--connect usb</c>, <c>--connect wifi:192.168.1.14</c>,
    /// <c>--vcam</c>, <c>--update-feed http://127.0.0.1:4174/latest.json</c> (a test release feed).
    /// </summary>
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Controller = new AppController(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
        _window = new MainWindow();
        _window.Activate();

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
}
