using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace AirLens.App.Tray;

/// <summary>
/// One AirLens per Windows session, and two ways for other programs to reach it, as named events:
/// <list type="bullet">
/// <item><see cref="ShowEvent"/>: a second launch (Start menu, shortcut) sets it and exits, and the
/// running copy shows its window, also when it is hidden in the notification area.</item>
/// <item><see cref="QuitEvent"/>: the installer and uninstaller set it, and the running copy exits
/// as through Exit. They can't ask a window hidden in the notification area to close.</item>
/// </list>
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string ShowEvent = @"Local\net.silow.airlens.show";
    /// <summary>Also named in windows/installer/install.ps1 and uninstall.ps1.</summary>
    public const string QuitEvent = @"Local\net.silow.airlens.quit";

    private readonly EventWaitHandle _show;
    private readonly EventWaitHandle _quit;
    private readonly RegisteredWaitHandle _showWait;
    private readonly RegisteredWaitHandle _quitWait;
    private bool _disposed;

    private SingleInstance(EventWaitHandle show, DispatcherQueue ui, Action onShow, Action onQuit)
    {
        _show = show;
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEvent);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_show, (_, _) => ui.TryEnqueue(() => onShow()), null, Timeout.Infinite, false);
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quit, (_, _) => ui.TryEnqueue(() => onQuit()), null, Timeout.Infinite, false);
    }

    /// <summary>Makes this process the running AirLens; null if another one already is.</summary>
    public static SingleInstance? Claim(DispatcherQueue ui, Action onShow, Action onQuit)
    {
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent, out bool createdNew);
        if (createdNew) return new SingleInstance(show, ui, onShow, onQuit);
        show.Dispose();
        return null;
    }

    /// <summary>Asks the AirLens that is already running to show its window.</summary>
    public static void ShowRunning()
    {
        // This launch has the foreground; let the running copy take it to come to the front.
        AllowSetForegroundWindow(ASFW_ANY);
        if (EventWaitHandle.TryOpenExisting(ShowEvent, out var show))
        {
            using (show) show.Set();
        }
    }

    /// <summary>Releases the names at once, so a new launch during a slow shutdown starts afresh.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _showWait.Unregister(null);
        _quitWait.Unregister(null);
        _show.Dispose();
        _quit.Dispose();
    }

    private const uint ASFW_ANY = unchecked((uint)-1);

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
