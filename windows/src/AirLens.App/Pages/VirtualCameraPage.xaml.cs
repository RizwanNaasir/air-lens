using AirLens.Core.VirtualCamera;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirLens.App.Pages;

/// <summary>
/// The "AirLens" camera other apps see: on/off, whether an app is using it, the picture controls,
/// and what it outputs.
/// </summary>
public sealed partial class VirtualCameraPage : Page
{
    private readonly AppController _app = App.Controller;
    private readonly LookCards _look;
    private bool _updating;

    public VirtualCameraPage()
    {
        InitializeComponent();
        _look = new LookCards(_app);
        foreach (var card in _look.Cards) PicturePanel.Children.Add(card);
        Loaded += (_, _) =>
        {
            _app.Updated += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.Updated -= Refresh;
    }

    private void Refresh()
    {
        _updating = true;
        NotInstalledBar.IsOpen = !VirtualCameraHost.IsInstalled;
        VcamToggle.IsOn = _app.VirtualCameraOn;
        VcamToggle.IsEnabled = VirtualCameraHost.IsInstalled;
        _look.Refresh();
        var s = _app.Stats;
        UseCard.Description = !_app.VirtualCameraOn
            ? (string.IsNullOrEmpty(_app.VirtualCameraMessage) ? "Off" : _app.VirtualCameraMessage)
            : s?.VirtualCameraConsumer == true
                ? $"Yes, an app has it open · {s.Value.VirtualCameraFrames:N0} frames delivered"
                : "No app has it open right now";
        _updating = false;
    }

    private void OnVcamToggled(object sender, RoutedEventArgs e)
    {
        if (!_updating) _app.SetVirtualCamera(VcamToggle.IsOn);
    }
}
