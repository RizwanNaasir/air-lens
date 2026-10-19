using AirLens.Core.Pipeline;
using AirLens.Core.Transport;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AirLens.App.Pages;

/// <summary>Which phone, how it is connected, and the connection preferences.</summary>
public sealed partial class PhonePage : Page
{
    private readonly AppController _app = App.Controller;
    private bool _updating;

    public PhonePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            HostBox.Text = _app.Settings.Host;
            _app.Updated += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.Updated -= Refresh;
    }

    private void Refresh()
    {
        _updating = true;
        var s = _app.Stats;
        bool connected = s?.State is ReceiverState.Connected or ReceiverState.Streaming;
        var status = _app.Status;
        DeviceCard.Header = status.Phone.Title;
        DeviceCard.Description = status.Phone.Detail;
        PhoneIcon.Foreground = StatusBrushes.For(status.Phone.Tone);
        StreamCard.Header = status.Stream.Title;
        StreamCard.Description = status.Stream.Detail;
        StreamIcon.Foreground = StatusBrushes.For(status.Stream.Tone);
        ConnectionButton.Content = !_app.IsRunning ? "Connect" : connected ? "Disconnect" : "Stop looking";
        ConnectionButton.Style = _app.IsRunning ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        AutoConnectToggle.IsOn = _app.Settings.AutoConnect;
        if (HostBox.FocusState == FocusState.Unfocused && HostBox.Text != _app.Settings.Host) HostBox.Text = _app.Settings.Host; // discovery may have moved it
        LinkCard.Description = s is { } now
            ? (now.Endpoint is { } ep ? $"{(ep.Kind == EndpointKind.Usb ? "USB" : "Wi-Fi " + ep.Host)} · round trip {now.RttMs:0} ms · {now.Status}" : now.Status)
            : "Not connected";
        _updating = false;
    }

    private void OnConnectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (_app.IsRunning) _app.Disconnect();
        else _app.Connect();
    }

    private void OnAutoConnectToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _app.Settings.AutoConnect = AutoConnectToggle.IsOn;
        _app.Settings.Save();
    }

    private void OnSaveHostClick(object sender, RoutedEventArgs e) => _app.ConnectWifi(HostBox.Text);
}
