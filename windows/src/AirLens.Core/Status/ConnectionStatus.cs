using AirLens.Core.Pipeline;
using AirLens.Core.Protocol;
using AirLens.Core.Transport;

namespace AirLens.Core.Status;

/// <summary>What ADB says is on the USB cable (<see cref="AdbForwarder.QueryDevices"/>).</summary>
public enum UsbPresence
{
    /// <summary>Not looked yet.</summary>
    Unknown,
    /// <summary>No adb.exe on this PC, so USB cannot work (Wi-Fi still can).</summary>
    NoAdb,
    NoPhone,
    /// <summary>A phone is attached but has not allowed USB debugging for this PC yet.</summary>
    Unauthorized,
    /// <summary>A phone is attached but its USB debugging does not answer ("offline").</summary>
    Unresponsive,
    /// <summary>A phone is attached and ready.</summary>
    Ready,
}

/// <summary>Green: all good. Busy: on its way. Attention: the user has to do something. Idle: nothing going on.</summary>
public enum StatusTone
{
    Good,
    Busy,
    Attention,
    Idle,
}

public sealed record StatusLine(string Title, string Detail, StatusTone Tone);

/// <summary>Everything the status depends on, so it can be worked out (and tested) in one place.</summary>
public sealed record ConnectionInputs(
    ReceiverStats? Stats,
    bool Looking,
    string? StartError,
    UsbPresence Usb,
    IReadOnlyList<DiscoveredPhone> WifiPhones,
    string RememberedPhoneId,
    string RememberedPhoneName,
    TimeSpan? StreamingFor);

/// <summary>
/// The two answers every page shows, worked out once so the pages never disagree:
/// <list type="bullet">
/// <item><b>Phone</b>: is a phone there, and how (connected, on the USB cable, found on Wi-Fi, none)?</item>
/// <item><b>Stream</b>: is it live, starting, waiting for Start streaming on the phone, or off?</item>
/// </list>
/// A phone can be on the cable and not streaming; that is the case this split makes visible.
/// </summary>
public sealed record ConnectionStatus(StatusLine Phone, StatusLine Stream)
{
    public static ConnectionStatus From(ConnectionInputs i)
    {
        var s = i.Stats;
        bool live = s?.State == ReceiverState.Streaming;
        bool connected = live || s?.State == ReceiverState.Connected;
        var mine = i.WifiPhones.FirstOrDefault(p => p.Id == i.RememberedPhoneId && i.RememberedPhoneId.Length > 0);
        return new ConnectionStatus(PhoneLine(i, connected, mine), StreamLine(i, live, connected, mine));
    }

    private static StatusLine PhoneLine(ConnectionInputs i, bool connected, DiscoveredPhone? mine)
    {
        var s = i.Stats;
        if (connected && s is { } st)
        {
            string name = st.Device?.Device is { Length: > 0 } d ? d : Fallback(i.RememberedPhoneName);
            string via = st.Endpoint?.Kind == EndpointKind.Wifi ? "Wi-Fi" : "USB";
            var parts = new List<string> { st.Endpoint?.Kind == EndpointKind.Wifi ? $"Wi-Fi {st.Endpoint.Host}" : "USB cable" };
            if (st.RttMs > 0) parts.Add($"round trip {st.RttMs:0} ms");
            if (st.Device?.Heat is { TempC: { } temp } heat) parts.Add($"{temp:0} °C" + (heat.IsHot ? ", hot" : ""));
            return new StatusLine($"{name}, connected over {via}", string.Join(" · ", parts), StatusTone.Good);
        }
        switch (i.Usb)
        {
            case UsbPresence.Ready:
                return new StatusLine("Phone on the USB cable", "USB debugging is on.", StatusTone.Good);
            case UsbPresence.Unauthorized:
                return new StatusLine("Phone on the USB cable", "Allow USB debugging: accept the prompt on the phone's screen.", StatusTone.Attention);
            case UsbPresence.Unresponsive:
                return new StatusLine("Phone on the USB cable", "Its USB debugging isn't answering. Unplug the cable and plug it back in.", StatusTone.Attention);
        }
        if (mine != null)
            return new StatusLine($"{mine.Name} found on Wi-Fi", mine.Busy ? $"At {mine.Address}, in use by another PC" : $"At {mine.Address}", StatusTone.Good);
        if (i.WifiPhones.Count > 0)
            return new StatusLine(i.WifiPhones.Count == 1 ? $"{i.WifiPhones[0].Name} found on Wi-Fi" : $"{i.WifiPhones.Count} phones found on Wi-Fi",
                "Press Connect on the one to use.", StatusTone.Attention);
        if (i.Usb == UsbPresence.NoAdb)
            return new StatusLine("No phone found", "USB needs Android's platform tools (adb) on this PC. Wi-Fi works without them.", StatusTone.Idle);
        return new StatusLine("No phone found",
            i.Looking ? "Plug it in with USB debugging on, or start streaming on a phone on this Wi-Fi." : "AirLens isn't looking for one.",
            StatusTone.Idle);
    }

    private static StatusLine StreamLine(ConnectionInputs i, bool live, bool connected, DiscoveredPhone? mine)
    {
        if (i.StartError is { } error) return new StatusLine("Can't start", error, StatusTone.Attention);
        if (!i.Looking) return new StatusLine("Off", "Press Connect to look for your phone.", StatusTone.Idle);
        if (live && i.Stats is { Stream: { } info })
        {
            string codec = info.Codec == VideoCodec.Hevc ? "H.265" : "H.264";
            string since = i.StreamingFor is { } t ? $" · for {Elapsed(t)}" : "";
            return new StatusLine("Live", $"{info.Width} × {info.Height} at {info.Fps} fps · {codec}{since}", StatusTone.Good);
        }
        if (live || connected) return new StatusLine("Starting", "Waiting for the first picture.", StatusTone.Busy);
        if (i.Usb == UsbPresence.Ready)
            return new StatusLine("Not streaming", "Press Start streaming in AirLens on the phone. It connects by itself.", StatusTone.Attention);
        if (i.Usb is UsbPresence.Unauthorized or UsbPresence.Unresponsive)
            return new StatusLine("Not streaming", "Waiting for the phone's USB debugging.", StatusTone.Attention);
        if (mine is { Busy: true })
            return new StatusLine("Not streaming here", "The phone is streaming to another PC.", StatusTone.Attention);
        if (mine != null) return new StatusLine("Connecting", $"Reaching {mine.Name} over Wi-Fi.", StatusTone.Busy);
        return new StatusLine("Waiting for your phone", "Open AirLens on the phone and press Start streaming. It connects by itself.", StatusTone.Busy);
    }

    private static string Fallback(string remembered) => remembered.Length > 0 ? remembered : "Your phone";

    private static string Elapsed(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
}
