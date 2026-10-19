using AirLens.Core.Protocol;

namespace AirLens.Core.Status;

/// <summary>What a notification-area menu item does when chosen.</summary>
public enum TrayCommand
{
    None,
    Open,
    Connect,
    Disconnect,
    VirtualCamera,
    /// <summary>Switch to the lens in <see cref="TrayItem.Argument"/>.</summary>
    Lens,
    Exit,
}

/// <summary>One entry of the notification-area menu; an empty <see cref="Text"/> is a separator.</summary>
public sealed record TrayItem(string Text, TrayCommand Command = TrayCommand.None)
{
    public string? Argument { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Checked { get; init; }
    /// <summary>A choice among its siblings (a dot rather than a tick when checked).</summary>
    public bool Radio { get; init; }
    /// <summary>Shown in bold; what a click on the icon does.</summary>
    public bool IsDefault { get; init; }
    public IReadOnlyList<TrayItem> Children { get; init; } = [];

    public bool IsSeparator => Text.Length == 0;
    public static TrayItem Separator { get; } = new("");
}

/// <summary>What the menu depends on, so it can be built (and tested) in one place.</summary>
public sealed record TrayInputs(ConnectionStatus Status, bool Looking, bool Connected, bool VirtualCameraOn, DeviceState? Device);

/// <summary>
/// The notification-area icon's menu and tooltip: the same Phone and Stream lines as the pages
/// (<see cref="ConnectionStatus"/>), the Phone page's connection button, the virtual camera switch
/// and the phone's lenses.
/// </summary>
public static class TrayMenu
{
    /// <summary>Windows shows at most 127 characters of a notification-area tooltip.</summary>
    public const int TooltipMax = 127;

    public static IReadOnlyList<TrayItem> Build(TrayInputs i)
    {
        var items = new List<TrayItem>
        {
            new($"{i.Status.Phone.Title} · {i.Status.Stream.Title}") { Enabled = false },
            TrayItem.Separator,
            new("Open AirLens", TrayCommand.Open) { IsDefault = true },
            // The Phone page's button: Connect, or stop whatever is going on.
            !i.Looking ? new("Connect", TrayCommand.Connect)
                : i.Connected ? new("Disconnect", TrayCommand.Disconnect)
                : new("Stop looking", TrayCommand.Disconnect),
            new("Virtual camera", TrayCommand.VirtualCamera) { Checked = i.VirtualCameraOn },
        };
        if (i.Connected && i.Device is { Lenses.Count: > 0 } device)
        {
            items.Add(new TrayItem("Lens")
            {
                Children = device.Lenses
                    .Select(l => new TrayItem(l.Label, TrayCommand.Lens) { Argument = l.Key, Radio = true, Checked = l.Key == device.LensKey })
                    .ToList(),
            });
        }
        items.Add(TrayItem.Separator);
        items.Add(new TrayItem("Exit", TrayCommand.Exit));
        return items;
    }

    public static string Tooltip(ConnectionStatus status)
    {
        string tip = $"AirLens\n{status.Phone.Title}\n{status.Stream.Title}";
        return tip.Length <= TooltipMax ? tip : tip[..(TooltipMax - 1)] + "…";
    }
}
