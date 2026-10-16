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
        if (connected && s is { } st)
        {
            DeviceCard.Header = st.Device?.Device ?? "Phone";
            string lens = st.Device?.ActiveLens?.Label is { } l ? $"{l} lens · " : "";
            DeviceCard.Description = st.Stream is { } info
                ? $"{lens}{(info.Codec == Core.Protocol.VideoCodec.Hevc ? "H.265" : "H.264")} · {info.Width} × {info.Height} at {info.Fps} fps · {info.BitrateBps / 1e6:0} Mbps"
                : lens + "connected";
        }
        else
        {
            DeviceCard.Header = _app.IsRunning ? "Looking for your phone" : "Disconnected";
            DeviceCard.Description = _app.IsRunning ? "Open AirLens on the phone and press Start streaming" : "AirLens is not looking for your phone";
        }
        ConnectionButton.Content = _app.IsRunning ? "Disconnect" : "Connect";
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
