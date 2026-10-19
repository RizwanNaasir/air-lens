using AirLens.App.Pages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AirLens.App;

/// <summary>
/// Shell in the Windows 11 Settings style: Mica, a custom title bar, and a NavigationView whose
/// pages all read from the one <see cref="AppController"/>.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // AppWindow sizes are physical pixels: scale 1280x800 DIPs by the monitor's DPI.
        double dpiScale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new Windows.Graphics.SizeInt32((int)(1280 * dpiScale), (int)(800 * dpiScale)));
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "AirLens.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            AppIcon.Source = new BitmapImage(new Uri(icon));
        }
        ContentFrame.Navigate(typeof(HomePage));
        // Closing (the X, Alt+F4) keeps AirLens running in the notification area, unless the
        // setting is off, Exit was chosen, or there is no icon to come back from.
        AppWindow.Closing += (_, e) =>
        {
            if (App.Instance.Quitting || !App.Controller.Settings.KeepRunningInTray) return;
            if (App.Instance.HideToTray()) e.Cancel = true;
        };

        // An update is waiting: a badge on Settings, where it is installed.
        App.Controller.Updates.Changed += ShowUpdateBadge;
        Nav.Loaded += (_, _) => ShowUpdateBadge();
    }

    /// <summary>Off the screen and the taskbar; the stream and the virtual camera keep running.</summary>
    public void HideToTray()
    {
        AppWindow.Hide();
        App.Controller.WindowVisible = false;
    }

    /// <summary>Back from the notification area (or from minimized), in front.</summary>
    public void ShowFromTray()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        App.Controller.WindowVisible = true;
    }

    private void ShowUpdateBadge()
    {
        if (Nav.SettingsItem is NavigationViewItem settings)
            settings.InfoBadge = App.Controller.Updates.Stage == Updates.UpdateStage.Available ? new InfoBadge() : null;
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        Type page = args.IsSettingsSelected
            ? typeof(SettingsPage)
            : (args.SelectedItem as NavigationViewItem)?.Tag switch
            {
                "phone" => typeof(PhonePage),
                "vcam" => typeof(VirtualCameraPage),
                "diagnostics" => typeof(DiagnosticsPage),
                _ => typeof(HomePage),
            };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, new EntranceNavigationTransitionInfo());
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);
}
