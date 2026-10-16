using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace AirLens.App.Pages;

/// <summary>Everything the receiver measures, as text, plus the way to the log file.</summary>
public sealed partial class DiagnosticsPage : Page
{
    private readonly AppController _app = App.Controller;

    public DiagnosticsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _app.Updated += Refresh;
            Refresh();
        };
        Unloaded += (_, _) => _app.Updated -= Refresh;
    }

    private void Refresh()
    {
        if (_app.Stats is not { } s)
        {
            StatsText.Text = _app.StartError ?? "Disconnected. Nothing to measure.";
            return;
        }
        var d = s.Device;
        StatsText.Text =
            $"state       {s.State}: {s.Status}\n" +
            $"link        {s.Endpoint?.ToString() ?? "-"}   round trip {s.RttMs:0.0} ms   jitter {s.JitterMs:0.0} ms   hold {s.HoldMs:0.0} ms\n" +
            $"phone       {d?.Device ?? "-"}   lens {d?.ActiveLens?.Label ?? "-"}   quality {d?.ActivePreset?.Title ?? d?.Quality ?? "-"}\n" +
            $"stream      {s.Stream?.ToString() ?? "no stream yet"}\n" +
            $"decoder     {s.Decoder}{(s.Gpu != null ? " on " + s.Gpu : "")}\n" +
            $"rates       {s.DecodedFps:0.0} fps   {s.ReceivedMbps:0.0} Mbps   decode {s.DecodeMs:0.0} ms   last byte to decoded {s.EndToEndMs:0.0} ms\n" +
            $"drops       queue {s.QueueDrops}   superseded {s.Superseded}   decode errors {s.DecodeErrors}   keyframe requests {s.KeyframeRequests}\n" +
            $"memory      pool {s.PoolOutstanding}   leases {s.LiveLeases}   leaked {s.LeakedLeases}\n" +
            $"camera      {(_app.VirtualCameraOn ? "on" : "off")}   {(s.VirtualCameraConsumer ? "in use" : "not in use")}   {s.VirtualCameraFrames} frames delivered\n" +
            $"log         {AppController.LogFolder}";
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(StatsText.Text);
        Clipboard.SetContent(package);
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppController.LogFolder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{AppController.LogFolder}\"") { UseShellExecute = true });
    }
}
