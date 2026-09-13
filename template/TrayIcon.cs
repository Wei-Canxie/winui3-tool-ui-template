using System;
using System.Runtime.InteropServices;

namespace UiTemplate;

/// <summary>
/// Tray icon built directly on <c>Shell_NotifyIcon</c>.
///
/// The WinForms <c>NotifyIcon</c> needs a WinForms message loop, which does not exist in
/// a WinUI 3 app; the shell API works under the WinUI dispatcher because it only needs an
/// HWND to post messages to, which this class creates as a hidden message-only window.
///
/// Creation is best effort: a shell that is not ready must not stop the tool from
/// running, so every failure is logged and the rest of the app carries on.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0x00000000, NIM_DELETE = 0x00000002, NIM_SETVERSION = 0x00000004;
    private const uint NIF_MESSAGE = 0x00000001, NIF_ICON = 0x00000002, NIF_TIP = 0x00000004;
    private const uint NIF_GUID = 0x00000020, NIF_SHOWTIP = 0x40000000;
    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int WM_USER = 0x0400;
    private const int WM_TRAYICON = WM_USER + 100;
    private const int WM_LBUTTONDBLCLK = 0x0203, WM_RBUTTONUP = 0x0205, WM_CONTEXTMENU = 0x007B;

    private const uint MF_SEPARATOR = 0x0800, TPM_RIGHTBUTTON = 0x0002, TPM_RETURNCMD = 0x0100;
    private const uint IDM_SHOW = 1000, IDM_TOGGLE = 1001, IDM_EXIT = 1002;
    private const int IDI_APPLICATION = 32512;

    private static readonly IntPtr HwndMessage = new(-3);

    // A stable GUID means a second instance replaces this icon instead of stacking a
    // duplicate one next to it.
    private static readonly Guid TrayGuid = new("B1E2F3A4-5C6D-7E8F-9A0B-C1D2E3F4A5B6");

    /// <summary>The instance the static window procedure forwards to.</summary>
    private static TrayIcon? _active;

    // Kept in a static field: the delegate must outlive every call, or the GC collects the
    // thunk the window class points at and the next message crashes the process.
    private static readonly WindowProcDelegate StaticWindowProc = WindowProc;

    private readonly IntPtr _hwnd;
    private readonly bool _ownsIcon;
    private readonly IntPtr _hIcon;
    private NOTIFYICONDATA _data;
    private bool _disposed;
    private bool _sampleEnabled;

    /// <summary>Raised when the user asks for the window (double click, or the menu).</summary>
    internal event Action? ShowWindowRequested;

    /// <summary>Raised when the user toggles the demo feature from the menu.</summary>
    internal event Action? ToggleSampleRequested;

    /// <summary>Raised when the user chooses Exit.</summary>
    internal event Action? ExitRequested;

    /// <summary>False when the shell rejected the icon; every other call then becomes a no-op.</summary>
    internal bool IsAvailable { get; private set; }

    internal TrayIcon()
    {
        try
        {
            _hwnd = CreateMessageWindow();
            _hIcon = LoadTrayIcon(out _ownsIcon);

            if (_hwnd == IntPtr.Zero || _hIcon == IntPtr.Zero)
            {
                AppLog.Log($"TrayIcon: hwnd={_hwnd} hIcon={_hIcon}, giving up");
                return;
            }

            _active = this;

            _data = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_GUID | NIF_SHOWTIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = _hIcon,
                szTip = "UI Template",
                guidItem = TrayGuid,
            };

            IsAvailable = Shell_NotifyIcon(NIM_ADD, ref _data);

            // Version 4 is what gives the menu its modern right-click behaviour.
            var version = new NOTIFYICONDATA
            {
                cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uFlags = NIF_GUID,
                guidItem = TrayGuid,
                uTimeoutOrVersion = NOTIFYICON_VERSION_4,
            };
            Shell_NotifyIcon(NIM_SETVERSION, ref version);

            AppLog.Log($"TrayIcon: added={IsAvailable} err={Marshal.GetLastWin32Error()}");
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            AppLog.Log($"TrayIcon init failed: {ex}");
        }
    }

    /// <summary>Tell the menu which label the demo toggle should show.</summary>
    internal void SetSampleEnabled(bool enabled) => _sampleEnabled = enabled;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (IsAvailable) Shell_NotifyIcon(NIM_DELETE, ref _data);

            // Only an icon we created may be destroyed; the shared IDI_APPLICATION handle
            // belongs to the system.
            if (_ownsIcon && _hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        }
        catch (Exception ex) { AppLog.Log($"TrayIcon dispose failed: {ex.Message}"); }

        _active = null;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        var self = _active;
        if (self is not null && message == WM_TRAYICON)
        {
            var mouseMessage = lParam.ToInt32();
            if (mouseMessage is WM_LBUTTONDBLCLK)
            {
                self.ShowWindowRequested?.Invoke();
                return IntPtr.Zero;
            }

            if (mouseMessage is WM_RBUTTONUP or WM_CONTEXTMENU)
            {
                self.ShowContextMenu();
                return IntPtr.Zero;
            }
        }

        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        try
        {
            var menu = CreatePopupMenu();
            AppendMenu(menu, 0, IDM_SHOW, "Show window");
            AppendMenu(menu, 0, IDM_TOGGLE, _sampleEnabled ? "Disable sample feature" : "Enable sample feature");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, 0, IDM_EXIT, "Exit");

            GetCursorPos(out var point);

            // The menu must be owned by a foreground window or it stays on screen after the
            // next click somewhere else.
            SetForegroundWindow(_hwnd);
            var command = TrackPopupMenu(
                menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, point.X, point.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, 0 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            switch (command)
            {
                case IDM_SHOW: ShowWindowRequested?.Invoke(); break;
                case IDM_TOGGLE: ToggleSampleRequested?.Invoke(); break;
                case IDM_EXIT: ExitRequested?.Invoke(); break;
            }

            DestroyMenu(menu);
        }
        catch (Exception ex) { AppLog.Log($"TrayIcon menu failed: {ex.Message}"); }
    }

    /// <summary>Create the hidden window that receives the shell callbacks.</summary>
    private static IntPtr CreateMessageWindow()
    {
        var instance = GetModuleHandle(null);
        var className = "UiTemplateTray_" + Guid.NewGuid().ToString("N");

        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(StaticWindowProc),
            hInstance = instance,
            lpszClassName = className,
        };
        RegisterClassEx(ref windowClass);

        return CreateWindowEx(
            0, className, "UiTemplateTray", 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
    }

    /// <summary>
    /// Use the exe's own icon when it has one, otherwise the generic application icon.
    /// The template ships no .ico, so the fallback is the normal path here.
    /// </summary>
    private static IntPtr LoadTrayIcon(out bool owns)
    {
        owns = false;

        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = ExtractIconW(IntPtr.Zero, exe, 0);
                if (icon != IntPtr.Zero && icon != new IntPtr(1))
                {
                    owns = true;
                    return icon;
                }
            }
        }
        catch (Exception ex) { AppLog.Log($"TrayIcon: exe icon failed: {ex.Message}"); }

        return LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
    }

    private delegate IntPtr WindowProcDelegate(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "ExtractIconW")]
    private static extern IntPtr ExtractIconW(IntPtr instance, string exePath, int iconIndex);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, uint newItemId, string? newItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(
        IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
}
