using System.Runtime.InteropServices;
using AirLens.Core.Status;

namespace AirLens.App.Tray;

/// <summary>
/// The notification-area icon in plain Win32: Shell_NotifyIcon for the icon, tooltip and notices,
/// and a native popup menu, all owned by a hidden window on the UI thread (WinUI's message loop
/// pumps it). A click opens AirLens, a right-click shows the <see cref="TrayMenu"/>. When Explorer
/// restarts, the icon is added again (TaskbarCreated). Create, use and dispose on the UI thread.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;

    private readonly Func<IReadOnlyList<TrayItem>> _menu;
    private readonly Action<TrayItem> _invoke;
    private readonly Action _open;
    private readonly WndProcDelegate _wndProc; // a field, so it lives as long as the window calls it
    private readonly IntPtr _hwnd;
    private readonly IntPtr _icon;
    private readonly IntPtr _largeIcon;
    private readonly uint _taskbarCreated;
    private string _tooltip = "AirLens";
    private bool _disposed;

    /// <summary>The icon is in the notification area (false while Explorer is not running).</summary>
    public bool IsShown { get; private set; }

    public TrayIcon(string iconPath, Func<IReadOnlyList<TrayItem>> menu, Action<TrayItem> invoke, Action open)
    {
        _menu = menu;
        _invoke = invoke;
        _open = open;
        _wndProc = WndProc;
        AllowDarkMenus();

        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = hInstance,
            lpszClassName = ClassName,
        };
        if (RegisterClassExW(ref wc) == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastWin32Error()})");
        // A top-level window that is never shown, not a message-only one: the menu needs an owner
        // that can take the foreground, or it would not close when the user clicks elsewhere.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW, ClassName, "AirLens notification area", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastWin32Error()})");
        try { AllowDarkModeForWindow(_hwnd, true); } catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException) { }

        uint dpi = GetDpiForSystem();
        _icon = LoadIcon(iconPath, GetSystemMetricsForDpi(SM_CXSMICON, dpi));
        _largeIcon = LoadIcon(iconPath, GetSystemMetricsForDpi(SM_CXICON, dpi));
        _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
        Add();
    }

    public void SetTooltip(string text)
    {
        if (_disposed || text == _tooltip) return;
        _tooltip = text;
        var data = Data(NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    /// <summary>A notification next to the icon (Windows shows it as a toast); clicking it opens AirLens.</summary>
    public void ShowNotice(string title, string text)
    {
        if (_disposed || !IsShown) return;
        var data = Data(NIF_INFO);
        data.szInfoTitle = Clip(title, 63);
        data.szInfo = Clip(text, 255);
        data.dwInfoFlags = NIIF_USER | NIIF_LARGE_ICON | NIIF_RESPECT_QUIET_TIME;
        data.hBalloonIcon = _largeIcon;
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Removed explicitly: an icon whose process ended stays until the mouse passes over it.
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, ref data);
        IsShown = false;
        DestroyWindow(_hwnd);
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
        if (_largeIcon != IntPtr.Zero) DestroyIcon(_largeIcon);
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        IsShown = Shell_NotifyIconW(NIM_ADD, ref data);
        if (!IsShown) return;
        data.uVersion = NOTIFYICON_VERSION_4; // NIN_SELECT for clicks, WM_CONTEXTMENU with the anchor point
        Shell_NotifyIconW(NIM_SETVERSION, ref data);
    }

    private NOTIFYICONDATAW Data(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = Clip(_tooltip, 127),
        szInfo = "",
        szInfoTitle = "",
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage && !_disposed)
            {
                switch ((uint)(lParam.ToInt64() & 0xFFFF))
                {
                    case NIN_SELECT:
                    case NIN_KEYSELECT:
                    case NIN_BALLOONUSERCLICK:
                        _open();
                        break;
                    case WM_CONTEXTMENU:
                        long anchor = wParam.ToInt64();
                        ShowMenu((short)(anchor & 0xFFFF), (short)((anchor >> 16) & 0xFFFF));
                        break;
                }
                return IntPtr.Zero;
            }
            if (msg == _taskbarCreated && _taskbarCreated != 0 && !_disposed)
            {
                Add(); // Explorer restarted and forgot its icons
                return IntPtr.Zero;
            }
        }
        catch (Exception e)
        {
            // An exception must not unwind into Windows' window procedure.
            AppController.Log($"tray: {e}");
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void ShowMenu(int x, int y)
    {
        var items = _menu();
        var commands = new List<TrayItem>();
        var menu = Build(items, commands);
        try
        {
            // The documented dance for notification-area menus: own the foreground while the menu
            // is up, and post a message afterwards so a second right-click opens it again.
            SetForegroundWindow(_hwnd);
            uint align = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN;
            int chosen = TrackPopupMenuEx(menu, TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | align, x, y, _hwnd, IntPtr.Zero);
            PostMessageW(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (chosen > 0 && chosen <= commands.Count) _invoke(commands[chosen - 1]);
        }
        finally
        {
            DestroyMenu(menu); // and its submenus
        }
    }

    /// <summary>A native menu for <paramref name="items"/>; command id N is <c>commands[N - 1]</c>.</summary>
    private static IntPtr Build(IReadOnlyList<TrayItem> items, List<TrayItem> commands)
    {
        var menu = CreatePopupMenu();
        foreach (var item in items)
        {
            if (item.IsSeparator)
            {
                AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
                continue;
            }
            uint flags = MF_STRING | (item.Enabled ? 0 : MF_GRAYED);
            string text = item.Text.Replace("&", "&&"); // '&' marks a menu shortcut letter
            if (item.Children.Count > 0)
            {
                var sub = Build(item.Children, commands);
                AppendMenuW(menu, flags | MF_POPUP, (UIntPtr)(ulong)sub.ToInt64(), text);
                continue;
            }
            commands.Add(item);
            uint id = (uint)commands.Count;
            AppendMenuW(menu, flags | (item.Checked && !item.Radio ? MF_CHECKED : 0), id, text);
            if (item.Radio && item.Checked) CheckMenuRadioItem(menu, id, id, id, MF_BYCOMMAND);
            if (item.IsDefault) SetMenuDefaultItem(menu, id, 0);
        }
        return menu;
    }

    private static IntPtr LoadIcon(string path, int size) =>
        File.Exists(path) ? LoadImageW(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE) : IntPtr.Zero;

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Native menus are light unless the app opts in to dark ones. uxtheme has no documented call
    /// for it; these ordinals (Windows 10 1903 and later) are what File Explorer itself uses, and on
    /// any Windows without them the menu just stays light.
    /// </summary>
    private static void AllowDarkMenus()
    {
        try
        {
            SetPreferredAppMode(AllowDark);
            FlushMenuThemes();
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    // ---- Win32 -----------------------------------------------------------------------------------

    private const string ClassName = "AirLens.TrayIcon";
    private const int ErrorClassAlreadyExists = 1410;
    private const uint WM_NULL = 0x0000, WM_CONTEXTMENU = 0x007B, WM_APP = 0x8000;
    private const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401, NIN_BALLOONUSERCLICK = 0x0405;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4, NOTIFYICON_VERSION_4 = 4;
    private const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_INFO = 0x10, NIF_SHOWTIP = 0x80;
    private const uint NIIF_USER = 0x04, NIIF_LARGE_ICON = 0x20, NIIF_RESPECT_QUIET_TIME = 0x80;
    private const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_POPUP = 0x0010, MF_SEPARATOR = 0x0800, MF_BYCOMMAND = 0x0000;
    private const uint TPM_LEFTALIGN = 0x0000, TPM_RIGHTALIGN = 0x0008, TPM_BOTTOMALIGN = 0x0020, TPM_RIGHTBUTTON = 0x0002, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    private const uint WS_EX_TOOLWINDOW = 0x00000080, IMAGE_ICON = 1, LR_LOADFROMFILE = 0x0010;
    private const int SM_CXICON = 11, SM_CXSMICON = 49, SM_MENUDROPALIGNMENT = 40;
    private const int AllowDark = 1;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion; // a union with uTimeout
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint message, ref NOTIFYICONDATAW data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string? text);
    private static bool AppendMenuW(IntPtr menu, uint flags, uint id, string text) => AppendMenuW(menu, flags, (UIntPtr)id, text);
    [DllImport("user32.dll")] private static extern bool CheckMenuRadioItem(IntPtr menu, uint first, uint last, uint check, uint flags);
    [DllImport("user32.dll")] private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);
    [DllImport("user32.dll")] private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr instance, string name, uint type, int cx, int cy, uint load);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("uxtheme.dll", EntryPoint = "#133")] private static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
    [DllImport("uxtheme.dll", EntryPoint = "#135")] private static extern int SetPreferredAppMode(int mode);
    [DllImport("uxtheme.dll", EntryPoint = "#136")] private static extern void FlushMenuThemes();
}
