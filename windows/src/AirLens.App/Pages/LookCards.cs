using AirLens.Core.VirtualCamera;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AirLens.App.Pages;

/// <summary>
/// The virtual camera's picture controls (mirror, rotate, zoom, framing, hide), as settings cards.
/// Home shows them inside the Virtual camera expander, the Virtual camera page as a section; both
/// read and write <see cref="AppController.Look"/>, so they always agree.
/// </summary>
internal sealed class LookCards
{
    private readonly AppController _app;
    private readonly ToggleSwitch _mirror = new();
    private readonly Segmented _rotate = new();
    private readonly Slider _zoom = new() { Minimum = 1, Maximum = VirtualCameraLook.MaxZoom, StepFrequency = 0.1, Width = 180 };
    private readonly TextBlock _zoomText = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 40 };
    private readonly Segmented _framing = new();
    private readonly ToggleSwitch _hide = new();
    private bool _updating;

    public LookCards(AppController app)
    {
        _app = app;
        foreach (var deg in new[] { "0°", "90°", "180°", "270°" }) _rotate.Items.Add(new SegmentedItem { Content = deg });
        _framing.Items.Add(new SegmentedItem { Content = "Fit" });
        _framing.Items.Add(new SegmentedItem { Content = "Fill" });
        AutomationProperties.SetName(_mirror, "Mirror");
        AutomationProperties.SetName(_rotate, "Rotate");
        AutomationProperties.SetName(_zoom, "Zoom");
        AutomationProperties.SetName(_framing, "Framing");
        AutomationProperties.SetName(_hide, "Hide video");

        _mirror.Toggled += (_, _) => Change(l => l with { Mirror = _mirror.IsOn });
        _rotate.SelectionChanged += (_, _) => { if (_rotate.SelectedIndex >= 0) Change(l => l with { RotationDegrees = _rotate.SelectedIndex * 90 }); };
        _zoom.ValueChanged += (_, e) =>
        {
            _zoomText.Text = $"{e.NewValue:0.0}×";
            Change(l => l with { Zoom = e.NewValue });
        };
        _framing.SelectionChanged += (_, _) => { if (_framing.SelectedIndex >= 0) Change(l => l with { Fill = _framing.SelectedIndex == 1 }); };
        _hide.Toggled += (_, _) => Change(l => l with { Hidden = _hide.IsOn });

        var zoomRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        zoomRow.Children.Add(_zoom);
        zoomRow.Children.Add(_zoomText);

        Cards =
        [
            Card("Mirror", "Flip the picture left to right", "", _mirror),
            Card("Rotate", "Turn the picture; adds to the phone's own rotation", "", _rotate),
            Card("Zoom", "Crop to the centre. From a 4K stream, 2× is still full 1080p detail", "", zoomRow),
            Card("Framing", "Fit shows the whole picture with bars; Fill crops to fill the frame", "", _framing),
            Card("Hide video", "Apps see a black picture; the stream keeps running", "", _hide),
        ];
        Refresh();
    }

    public IReadOnlyList<SettingsCard> Cards { get; }

    /// <summary>Show the controller's values (another page, or the other copy, may have changed them).</summary>
    public void Refresh()
    {
        _updating = true;
        try
        {
            var l = _app.Look;
            _mirror.IsOn = l.Mirror;
            _rotate.SelectedIndex = l.RotationDegrees / 90;
            if (Math.Abs(_zoom.Value - l.Zoom) > 0.001) _zoom.Value = l.Zoom;
            _zoomText.Text = $"{l.Zoom:0.0}×";
            _framing.SelectedIndex = l.Fill ? 1 : 0;
            _hide.IsOn = l.Hidden;
        }
        finally
        {
            _updating = false;
        }
    }

    private void Change(Func<VirtualCameraLook, VirtualCameraLook> change)
    {
        if (!_updating) _app.SetLook(change(_app.Look));
    }

    private static SettingsCard Card(string header, string description, string glyph, object content) => new()
    {
        Header = header,
        Description = description,
        HeaderIcon = new FontIcon { Glyph = glyph },
        Content = content,
    };
}
