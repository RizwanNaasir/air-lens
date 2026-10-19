using AirLens.App.Rendering;
using AirLens.Core.Pipeline;
using AirLens.Core.Protocol;
using AirLens.Core.Status;
using AirLens.Core.Transport;
using AirLens.Core.VirtualCamera;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace AirLens.App.Pages;

/// <summary>
/// The landing page: the phone's picture, a one-line status, four health numbers, and the cards
/// for the phone connection, the virtual camera (with its picture controls), lens, focus and quality.
///
/// While the virtual camera is on, the preview is off (the app the picture is for shows it), so the
/// PC does not draw every frame twice. "Show preview" brings it back until the camera is toggled.
/// </summary>
public sealed partial class HomePage : Page
{
    /// <summary>How long a click wins over the phone's reported state while the phone applies it.</summary>
    private static readonly TimeSpan PendingWindow = TimeSpan.FromSeconds(3);

    private readonly AppController _app = App.Controller;
    private PreviewRenderer? _renderer;
    private bool _updating;
    private string _lensSignature = "";
    private string _presetSignature = "";
    private DateTime _lensPendingUntil;
    private DateTime _qualityPendingUntil;
    private DateTime _autoFocusPendingUntil;
    private DateTime _focusSliderPendingUntil;
    private readonly LookCards _look;
    private readonly DispatcherQueueTimer _focusTimer;
    private bool _previewAnyway;
    private bool _previewOff;
    private bool _lastVcamOn;

    public HomePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        _look = new LookCards(_app);
        foreach (var card in _look.Cards) VcamExpander.Items.Add(card);
        // A dragged slider fires per pixel; the phone gets at most one focus change per 100 ms.
        _focusTimer = DispatcherQueue.CreateTimer();
        _focusTimer.Interval = TimeSpan.FromMilliseconds(100);
        _focusTimer.IsRepeating = false;
        _focusTimer.Tick += (_, _) => _app.SetFocus(FocusSlider.Value);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        LeftColumn.SizeChanged += (_, e) => PreviewFrame.Height = Math.Max(180, e.NewSize.Width * 9 / 16);
        Preview.SizeChanged += (_, _) => ResizeRenderer();
        Preview.CompositionScaleChanged += (_, _) => ResizeRenderer();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _app.Updated += Refresh;
        _app.DecoderDeviceChanged += BindRenderer;
        _lastVcamOn = _app.VirtualCameraOn;
        _previewOff = PreviewOff;
        BindRenderer(_app.DecoderDevice);
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _app.Updated -= Refresh;
        _app.DecoderDeviceChanged -= BindRenderer;
        BindRenderer(null); // no point rendering a page nobody sees
    }

    /// <summary>Two columns when there is room, otherwise the cards go under the preview.</summary>
    private void ApplyLayout(double width)
    {
        bool narrow = width < 980;
        Grid.SetColumn(RightColumn, narrow ? 0 : 1);
        Grid.SetRow(RightColumn, narrow ? 1 : 0);
        Columns.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        Columns.ColumnDefinitions[1].MinWidth = narrow ? 0 : 400;
        Columns.ColumnSpacing = narrow ? 0 : 24;
        RightColumn.Margin = narrow ? new Thickness(0, 16, 0, 0) : new Thickness(0);

        // Phone and Stream side by side, or stacked when there is no room for two columns of text.
        bool stack = width < 720;
        Grid.SetColumn(StreamStatusBlock, stack ? 0 : 1);
        Grid.SetRow(StreamStatusBlock, stack ? 1 : 0);
        StatusGrid.ColumnDefinitions[1].Width = stack ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        StatusGrid.ColumnSpacing = stack ? 0 : 40;
    }

    private void Refresh()
    {
        var s = _app.Stats;
        bool running = _app.IsRunning;
        bool streaming = s?.State == ReceiverState.Streaming;
        bool connected = s?.State is ReceiverState.Connected or ReceiverState.Streaming;
        var device = connected ? s?.Device : null;
        string name = device?.Device ?? "your phone";
        string via = s?.Endpoint?.Kind == EndpointKind.Wifi ? "Wi-Fi" : "USB";

        _updating = true;
        try
        {
            var status = _app.Status;
            UpdateStatus(status);

            UsbCard.Visibility = Show(running && !connected);
            UsbCard.Description = _app.Usb switch
            {
                UsbPresence.Ready => "Phone on the cable. Press Start streaming on the phone",
                UsbPresence.Unauthorized => "Accept the USB debugging prompt on the phone",
                UsbPresence.Unresponsive => "USB debugging isn't answering. Unplug the cable and plug it back in",
                UsbPresence.NoAdb => "Needs Android's platform tools (adb) on this PC",
                _ => "No phone on the cable. Is USB debugging on?",
            };
            WifiCard.Visibility = Show(!connected);
            UpdateFoundPhones(connected);
            PhoneCard.Visibility = Show(connected);
            PhoneCard.Header = name;
            PhoneCard.Description = status.Phone.Detail;
            // "Your phone" listed above already offers Connect; one button is enough.
            bool mineListed = _app.Phones.Any(p => p.Id == _app.Settings.PhoneId);
            StoppedCard.Visibility = Show(!running && !mineListed);
            if (HostBox.FocusState == FocusState.Unfocused && HostBox.Text != _app.Settings.Host) HostBox.Text = _app.Settings.Host;

            if (_app.VirtualCameraOn != _lastVcamOn)
            {
                _lastVcamOn = _app.VirtualCameraOn;
                _previewAnyway = false; // each time the camera goes on, the preview goes off again
            }
            VcamToggle.IsOn = _app.VirtualCameraOn;
            VcamExpander.Description = !_app.VirtualCameraOn
                ? (string.IsNullOrEmpty(_app.VirtualCameraMessage) ? "Apps don't see “AirLens”" : Sentence(_app.VirtualCameraMessage))
                : s?.VirtualCameraConsumer == true ? "In use by an app"
                : streaming ? "Apps can pick “AirLens”" : "Apps can pick “AirLens”. The picture is black until the phone connects";

            _look.Refresh();
            UpdateLens(device);
            UpdateFocus(device);
            UpdateQuality(device);
            UpdateTiles(s, streaming);
            UpdateHeat(device, name);

            bool off = PreviewOff;
            PreviewPlaceholder.Visibility = Show(!streaming || off);
            PreviewPlaceholderText.Text = !running ? "Stream is off"
                : streaming && off ? "Preview is off while the virtual camera is on, so the PC and the phone don't draw every frame twice"
                : connected ? "Waiting for the first picture" : "The phone's picture appears here";
            ShowPreviewButton.Visibility = Show(streaming && off);
            HiddenBadge.Visibility = Show(_app.VirtualCameraOn && _app.Look.Hidden);
            if (_renderer != null && s?.Stream is { } info)
            {
                // What apps get: the phone's orientation plus the picture controls.
                var look = _app.Look;
                _renderer.RotationDegrees = VirtualCameraGeometry.Normalize(info.RotationDegrees + look.RotationDegrees);
                _renderer.Mirror = info.MirrorRequired ^ look.Mirror;
                _renderer.Zoom = (float)look.Zoom;
                _renderer.Fill = look.Fill;
                _renderer.FullRange = info.FullRange;
            }
        }
        finally
        {
            _updating = false;
        }
        if (PreviewOff != _previewOff)
        {
            _previewOff = PreviewOff;
            BindRenderer(_app.DecoderDevice); // stops or starts the renderer
        }
    }

    /// <summary>Off while the camera is on (unless asked for), and while the window is in the notification area.</summary>
    private bool PreviewOff => !_app.WindowVisible || _app.VirtualCameraOn && !_previewAnyway;

    /// <summary>The Phone and Stream lines (ConnectionStatus), the same ones the Phone page shows.</summary>
    private void UpdateStatus(ConnectionStatus status)
    {
        PhoneStatusTitle.Text = status.Phone.Title;
        PhoneStatusDetail.Text = status.Phone.Detail;
        PhoneDot.Fill = StatusBrushes.For(status.Phone.Tone);
        StreamStatusTitle.Text = status.Stream.Title;
        StreamStatusDetail.Text = status.Stream.Detail;
        StreamDot.Fill = StatusBrushes.For(status.Stream.Tone);
    }

    /// <summary>A warning while the phone is hot, and what AirLens did about it.</summary>
    private void UpdateHeat(DeviceState? device, string name)
    {
        var heat = device?.Heat;
        bool lowered = heat?.Lowered != null;
        HeatBar.IsOpen = heat is { } h && (lowered || h.IsHot);
        if (!HeatBar.IsOpen) return;
        if (lowered)
        {
            HeatBar.Title = $"{Sentence(name)} is hot, so it streams at {heat!.Lowered} for now.";
            HeatBar.Message = "Full quality comes back by itself after it has been cool for 5 minutes. Taking it out of a case or out of the sun helps.";
        }
        else
        {
            HeatBar.Title = $"{Sentence(name)} is getting hot.";
            HeatBar.Message = "The phone already lowers the bitrate and frame rate. If it stays hot for a minute, AirLens also lowers the quality until it cools.";
        }
    }

    private string _phonesSignature = "";

    /// <summary>One card per phone discovery found, with Connect; none while connected.</summary>
    private void UpdateFoundPhones(bool connected)
    {
        var phones = connected ? [] : _app.Phones;
        WifiCard.Description = phones.Count > 0
            ? "Or type the address the phone shows"
            : "Looking for phones on this network. Or type the address the phone shows";
        string signature = string.Join("|", phones.Select(p => $"{p.Id}@{p.Address}:{p.Busy}"));
        if (signature == _phonesSignature) return;
        _phonesSignature = signature;
        FoundPhonesPanel.Children.Clear();
        foreach (var phone in phones)
        {
            bool mine = phone.Id == _app.Settings.PhoneId;
            var connect = new Button { Content = "Connect", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            connect.Click += (_, _) => _app.ConnectPhone(phone);
            AutomationProperties.SetName(connect, $"Connect to {phone.Name}");
            FoundPhonesPanel.Children.Add(new SettingsCard
            {
                Header = phone.Name,
                Description = $"Wi-Fi {phone.Address}" + (mine ? " · your phone" : "") + (phone.Busy ? " · in use by a PC" : ""),
                HeaderIcon = new FontIcon { Glyph = "" },
                Content = connect,
            });
        }
    }

    private void UpdateLens(DeviceState? device)
    {
        LensCard.IsEnabled = device is { Lenses.Count: > 0 };
        LensSegmented.Visibility = Show(device is { Lenses.Count: > 0 });
        LensCard.Description = device is { Lenses.Count: > 0 } ? "Switches on the phone instantly" : "Available once a phone is connected";
        if (device == null) return;
        string signature = string.Join("|", device.Lenses.Select(l => l.Key + "=" + l.Label));
        if (signature != _lensSignature)
        {
            _lensSignature = signature;
            LensSegmented.Items.Clear();
            foreach (var lens in device.Lenses)
            {
                var item = new SegmentedItem { Content = ShortLabel(lens), Tag = lens.Key };
                ToolTipService.SetToolTip(item, lens.Label);
                AutomationProperties.SetName(item, lens.Label);
                LensSegmented.Items.Add(item);
            }
            _lensPendingUntil = DateTime.MinValue;
        }
        if (DateTime.Now < _lensPendingUntil) return;
        int index = device.Lenses.ToList().FindIndex(l => l.Key == device.LensKey);
        if (LensSegmented.SelectedIndex != index) LensSegmented.SelectedIndex = index;
    }

    private void UpdateFocus(DeviceState? device)
    {
        var focus = device?.Focus;
        bool supported = focus is { Supported: true };
        bool auto = focus?.Value == null;
        FocusCard.IsEnabled = supported;
        FocusCard.Description = device == null ? "Available once a phone is connected"
            : !supported ? "This lens doesn't allow manual focus"
            : auto ? "Autofocus hunting? Turn it off to hold focus where it is now"
            : "Fixed. Slide to refocus";
        AutoFocusToggle.Visibility = Show(supported);
        if (DateTime.Now >= _autoFocusPendingUntil)
        {
            AutoFocusToggle.IsOn = auto;
            FocusRow.Visibility = Show(supported && !auto);
        }
        if (DateTime.Now >= _focusSliderPendingUntil && focus?.Value is { } v && Math.Abs(FocusSlider.Value - v) > 0.005) FocusSlider.Value = v;
    }

    private void UpdateQuality(DeviceState? device)
    {
        QualityCard.IsEnabled = device is { Presets.Count: > 0 };
        if (device == null)
        {
            QualityCard.Description = "Available once a phone is connected";
            if (_presetSignature.Length > 0)
            {
                _presetSignature = "";
                QualityBox.Items.Clear(); // no stale choice from the last phone
            }
            return;
        }
        bool custom = device.ActivePreset == null;
        string signature = string.Join("|", device.Presets.Select(p => p.Id + "=" + p.Title)) + (custom ? "|custom" : "");
        if (signature != _presetSignature)
        {
            _presetSignature = signature;
            QualityBox.Items.Clear();
            foreach (var p in device.Presets) QualityBox.Items.Add(new ComboBoxItem { Content = p.Title, Tag = p.Id });
            if (custom) QualityBox.Items.Add(new ComboBoxItem { Content = "Custom", Tag = DeviceState.Custom, IsEnabled = false });
            _qualityPendingUntil = DateTime.MinValue;
        }
        if (DateTime.Now < _qualityPendingUntil) return;
        QualityCard.Description = device.ActivePreset?.Summary ?? "Set in the phone's advanced settings";
        string id = device.ActivePreset?.Id ?? DeviceState.Custom;
        int index = QualityBox.Items.Cast<ComboBoxItem>().ToList().FindIndex(i => (string)i.Tag == id);
        if (QualityBox.SelectedIndex != index) QualityBox.SelectedIndex = index;
    }

    private void UpdateTiles(ReceiverStats? s, bool streaming)
    {
        if (!streaming || s is not { } st)
        {
            FpsValue.Text = BitrateValue.Text = PcDelayValue.Text = DroppedValue.Text = "–";
            return;
        }
        FpsValue.Text = $"{st.DecodedFps:0} fps";
        BitrateValue.Text = $"{st.ReceivedMbps:0} Mbps";
        // Last byte received → frame ready, plus the smoothing hold on Wi-Fi.
        PcDelayValue.Text = $"{Math.Max(1, st.EndToEndMs + st.HoldMs):0} ms";
        DroppedValue.Text = $"{st.QueueDrops + st.DecodeErrors}";
    }

    // ---- input -------------------------------------------------------------------------------

    private void OnLensChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || LensSegmented.SelectedItem is not SegmentedItem { Tag: string key }) return;
        _lensPendingUntil = DateTime.Now + PendingWindow;
        _app.SetLens(key);
    }

    private void OnQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || QualityBox.SelectedItem is not ComboBoxItem { Tag: string id } || id == DeviceState.Custom) return;
        _qualityPendingUntil = DateTime.Now + PendingWindow;
        if (_app.Stats?.Device?.Presets.FirstOrDefault(p => p.Id == id) is { } preset) QualityCard.Description = preset.Summary;
        _app.SetQuality(id);
    }

    private void OnVcamToggled(object sender, RoutedEventArgs e)
    {
        if (!_updating) _app.SetVirtualCamera(VcamToggle.IsOn);
    }

    private void OnAutoFocusToggled(object sender, RoutedEventArgs e)
    {
        if (_updating) return;
        _autoFocusPendingUntil = DateTime.Now + PendingWindow;
        FocusRow.Visibility = Show(!AutoFocusToggle.IsOn);
        // Off = hold focus where autofocus has it now; the phone answers with that position.
        if (AutoFocusToggle.IsOn) _app.SetFocus(null);
        else _app.LockFocus();
    }

    private void OnFocusSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updating) return;
        _focusSliderPendingUntil = DateTime.Now + PendingWindow;
        if (!_focusTimer.IsRunning) _focusTimer.Start();
    }

    private void OnShowPreviewClick(object sender, RoutedEventArgs e)
    {
        _previewAnyway = true;
        Refresh();
    }

    private void OnConnectWifiClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(HostBox.Text))
        {
            HostBox.Focus(FocusState.Programmatic);
            return;
        }
        _app.ConnectWifi(HostBox.Text);
    }

    private void OnHostKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) OnConnectWifiClick(sender, e);
    }

    private void OnDisconnectClick(object sender, RoutedEventArgs e) => _app.Disconnect();
    private void OnConnectClick(object sender, RoutedEventArgs e) => _app.Connect();

    // ---- preview -----------------------------------------------------------------------------

    /// <summary>
    /// The renderer must live on the decoder's device; rebuild it whenever that changes.
    ///
    /// Not re-entrant: disposing the old renderer joins its thread, and a join on the UI thread can
    /// run queued callbacks, including another device change. That request is parked and applied
    /// when the current one finishes, so no renderer is ever created and then lost (a lost one
    /// keeps a thread drawing into a swap chain nobody shows).
    /// </summary>
    private void BindRenderer(Vortice.Direct3D11.ID3D11Device? device)
    {
        if (_binding)
        {
            _parkedDevice = device;
            _parked = true;
            return;
        }
        _binding = true;
        try
        {
            while (true)
            {
                var old = _renderer;
                _renderer = null;
                old?.Dispose();
                if (!_parked && device != null && !PreviewOff && _app.Pipeline is { } pipeline && IsLoaded)
                {
                    var (w, h) = PanelPixels();
                    _renderer = new PreviewRenderer(device, pipeline.Mailbox, w, h, AppController.Log);
                    SwapChainPanelInterop.SetSwapChain(Preview, _renderer.SwapChainPointer);
                    ResizeRenderer();
                    _renderer.Start();
                }
                if (!_parked) break;
                _parked = false;
                device = _parkedDevice;
            }
        }
        finally
        {
            _binding = false;
        }
    }

    private bool _binding;
    private bool _parked;
    private Vortice.Direct3D11.ID3D11Device? _parkedDevice;

    private (int W, int H) PanelPixels() =>
        ((int)Math.Max(1, Preview.ActualWidth * Preview.CompositionScaleX),
         (int)Math.Max(1, Preview.ActualHeight * Preview.CompositionScaleY));

    private void ResizeRenderer()
    {
        var (w, h) = PanelPixels();
        _renderer?.Resize(w, h, Preview.CompositionScaleX, Preview.CompositionScaleY);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static Visibility Show(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The Android app's short names, so five lenses fit one row: Wide, Auto, Main, Tele, Front.</summary>
    private static string ShortLabel(LensInfo lens) => lens.Kind switch
    {
        "ultra_wide" => "Wide",
        "telephoto" => "Tele",
        _ => lens.Label.Split(" (")[0],
    };

    private static string Sentence(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}
