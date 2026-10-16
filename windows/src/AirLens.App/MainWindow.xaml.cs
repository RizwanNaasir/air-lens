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
        Closed += (_, _) => App.Controller.Shutdown();
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
}
