using AirLens.Core.Protocol;
using AirLens.Core.Status;
using Xunit;

namespace AirLens.Core.Tests;

public class TrayMenuTests
{
    private static readonly ConnectionStatus Live = new(
        new StatusLine("HUAWEI P30, connected over USB", "USB cable", StatusTone.Good),
        new StatusLine("Live", "1920 × 1080 at 30 fps", StatusTone.Good));

    private static readonly ConnectionStatus Off = new(
        new StatusLine("No phone found", "AirLens isn't looking for one.", StatusTone.Idle),
        new StatusLine("Off", "Press Connect to look for your phone.", StatusTone.Idle));

    private static DeviceState Device() => DeviceState.TryParse("""
        {"device":"HUAWEI P30","lens":"main","lenses":[
          {"key":"wide","label":"Ultra wide (0.6x)","kind":"ultra_wide"},
          {"key":"main","label":"Main (1x)","kind":"main"}]}
        """u8.ToArray())!;

    private static IReadOnlyList<TrayItem> Menu(ConnectionStatus status, bool looking = true, bool connected = true,
        bool vcam = false, DeviceState? device = null) =>
        TrayMenu.Build(new TrayInputs(status, looking, connected, vcam, device));

    [Fact]
    public void LiveMenuHasStatusControlsAndExit()
    {
        var menu = Menu(Live, vcam: true, device: Device());
        Assert.Equal(["HUAWEI P30, connected over USB · Live", "", "Open AirLens", "Disconnect", "Virtual camera", "Lens", "", "Exit"],
            menu.Select(i => i.Text));

        Assert.False(menu[0].Enabled); // the status is information, not a command
        Assert.True(menu[1].IsSeparator);
        Assert.Equal(TrayCommand.Open, menu[2].Command);
        Assert.True(menu[2].IsDefault);
        Assert.Equal(TrayCommand.Disconnect, menu[3].Command);
        Assert.Equal(TrayCommand.VirtualCamera, menu[4].Command);
        Assert.True(menu[4].Checked);
        Assert.Equal(TrayCommand.Exit, menu[7].Command);
        Assert.Single(menu, i => i.IsDefault);
    }

    [Fact]
    public void LensSubmenuMarksTheActiveLens()
    {
        var lens = Menu(Live, device: Device()).Single(i => i.Text == "Lens");
        Assert.Equal(["Ultra wide (0.6x)", "Main (1x)"], lens.Children.Select(c => c.Text));
        Assert.All(lens.Children, c => Assert.True(c.Radio));
        Assert.All(lens.Children, c => Assert.Equal(TrayCommand.Lens, c.Command));
        Assert.Equal(["wide", "main"], lens.Children.Select(c => c.Argument));
        Assert.Equal([false, true], lens.Children.Select(c => c.Checked));
    }

    [Fact]
    public void NotLookingOffersConnectAndNoLens()
    {
        var menu = Menu(Off, looking: false, connected: false);
        var connect = menu.Single(i => i.Text == "Connect");
        Assert.Equal(TrayCommand.Connect, connect.Command);
        Assert.DoesNotContain(menu, i => i.Text == "Lens");
        Assert.Equal("No phone found · Off", menu[0].Text);
    }

    [Fact]
    public void LookingWithoutAPhoneOffersStopLooking()
    {
        var menu = Menu(Off with { Stream = new StatusLine("Waiting for your phone", "", StatusTone.Busy) }, connected: false);
        Assert.Equal(TrayCommand.Disconnect, menu.Single(i => i.Text == "Stop looking").Command);
        Assert.DoesNotContain(menu, i => i.Text == "Lens");
    }

    [Fact]
    public void NoLensItemUntilThePhoneListsLenses()
    {
        Assert.DoesNotContain(Menu(Live), i => i.Text == "Lens");
        var none = Device() with { Lenses = [] };
        Assert.DoesNotContain(Menu(Live, device: none), i => i.Text == "Lens");
    }

    [Fact]
    public void VirtualCameraUncheckedWhenOff()
    {
        Assert.False(Menu(Live, vcam: false).Single(i => i.Command == TrayCommand.VirtualCamera).Checked);
    }

    [Fact]
    public void TooltipShowsBothLines()
    {
        Assert.Equal("AirLens\nHUAWEI P30, connected over USB\nLive", TrayMenu.Tooltip(Live));
    }

    [Fact]
    public void TooltipFitsTheNotificationArea()
    {
        // Windows takes at most 127 characters; a longer tip must end visibly cut, not mid-word garbage.
        var longName = new ConnectionStatus(new StatusLine(new string('x', 200) + ", connected over Wi-Fi", "", StatusTone.Good), Live.Stream);
        string tip = TrayMenu.Tooltip(longName);
        Assert.True(tip.Length <= TrayMenu.TooltipMax);
        Assert.EndsWith("…", tip);
        Assert.StartsWith("AirLens\n", tip);
    }
}
