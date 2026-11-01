using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsBtProxy.Tray;

/// <summary>
/// The small window the tray icon opens: the same status text the <c>status</c> command prints, in a read-only edit box so it can be
/// selected and copied, and a Close button. At most one exists; <see cref="TrayWindow"/> refreshes it every 2 seconds while it is open.
/// </summary>
/// <remarks>
/// Per-monitor DPI aware (the manifest says PerMonitorV2): sizes are in 96-DPI units scaled by the window's DPI, and fonts are
/// re-created when the window moves to a monitor with a different scale. The bottom row leaves room for a second button left of
/// Close.
/// </remarks>
internal sealed unsafe class StatusWindow
{
    public const string Title = "Windows BT Proxy for Home Assistant";
    private const string ClassName = "WindowsBtProxyForHA.Status";

    private const uint Style = Native.WS_CAPTION | Native.WS_SYSMENU;
    private const uint ExStyle = Native.WS_EX_TOOLWINDOW | Native.WS_EX_CONTROLPARENT;
    private const int EditId = 100;

    // Layout in 96-DPI units (the Windows dialog spacing guidelines: 11 px margins, 7 px between controls).
    private const int ClientWidth = 560;
    private const int ClientHeight = 300;
    private const int Margin = 11;
    private const int ButtonWidth = 88;
    private const int ButtonHeight = 26;

    private static StatusWindow? s_open;
    private static bool s_registered;

    private readonly Action _closed;
    private nint _hwnd;
    private nint _edit;
    private nint _close;
    private nint _uiFont;
    private nint _textFont;
    private string? _text;

    private StatusWindow(Action closed) => _closed = closed;

    /// <summary>The window's handle, for <c>IsDialogMessage</c> in the message loop (Tab, Enter and Esc).</summary>
    public nint Hwnd => _hwnd;

    /// <summary>Opens the window centred on the monitor under the mouse, showing <paramref name="text"/>.</summary>
    /// <param name="text">The first status text.</param>
    /// <param name="closed">Called once the window is gone (closed by the user or by <see cref="Close"/>).</param>
    public static StatusWindow Open(string text, Action closed)
    {
        if (s_open is not null)
        {
            throw new InvalidOperationException("The status window is already open.");
        }
        var instance = Native.GetModuleHandleW(null);
        Register(instance);

        var monitor = MonitorUnderCursor();
        var window = new StatusWindow(closed);
        s_open = window;
        try
        {
            // Created on the target monitor first, so GetDpiForWindow below already answers for that monitor.
            window._hwnd = Native.CreateWindowExW(
                ExStyle, ClassName, Title, Style, monitor.RcWork.Left, monitor.RcWork.Top, 1, 1, 0, 0, instance, 0);
            if (window._hwnd == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
            }
            window._edit = Native.CreateWindowExW(
                Native.WS_EX_CLIENTEDGE, "EDIT", string.Empty,
                Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_VSCROLL | Native.WS_TABSTOP | Native.ES_MULTILINE | Native.ES_READONLY | Native.ES_AUTOVSCROLL,
                0, 0, 0, 0, window._hwnd, EditId, instance, 0);
            window._close = Native.CreateWindowExW(
                0, "BUTTON", "Close", Native.WS_CHILD | Native.WS_VISIBLE | Native.WS_TABSTOP | Native.BS_DEFPUSHBUTTON,
                0, 0, 0, 0, window._hwnd, (nint)Native.IDCANCEL, instance, 0);

            var dpi = Native.GetDpiForWindow(window._hwnd);
            window.ApplyDpi(dpi);
            var frame = new Native.RECT { Right = Scale(ClientWidth, dpi), Bottom = Scale(ClientHeight, dpi) };
            _ = Native.AdjustWindowRectExForDpi(ref frame, Style, false, ExStyle, dpi);
            var width = frame.Right - frame.Left;
            var height = frame.Bottom - frame.Top;
            var work = monitor.RcWork;
            _ = Native.SetWindowPos(
                window._hwnd, 0,
                work.Left + Math.Max(0, (work.Right - work.Left - width) / 2),
                work.Top + Math.Max(0, (work.Bottom - work.Top - height) / 2),
                width, height, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);

            window.SetText(text);
            _ = Native.ShowWindow(window._hwnd, Native.SW_SHOWNORMAL);
            _ = Native.SetForegroundWindow(window._hwnd);
            _ = Native.SetFocus(window._close);
            return window;
        }
        catch
        {
            if (window._hwnd != 0)
            {
                _ = Native.DestroyWindow(window._hwnd);
            }
            s_open = null;
            throw;
        }
    }

    /// <summary>Brings the window back to the front (it may be behind others).</summary>
    public void Focus()
    {
        _ = Native.ShowWindow(_hwnd, Native.SW_RESTORE);
        _ = Native.SetForegroundWindow(_hwnd);
    }

    /// <summary>
    /// Shows <paramref name="text"/>. Does nothing when it equals what is shown, because replacing the text would drop the user's
    /// selection every 2 seconds.
    /// </summary>
    public void SetText(string text)
    {
        if (string.Equals(text, _text, StringComparison.Ordinal))
        {
            return;
        }
        _text = text;
        _ = Native.SetWindowTextW(_edit, text);
    }

    /// <summary>Closes the window; the <c>closed</c> callback runs as usual.</summary>
    public void Close() => _ = Native.DestroyWindow(_hwnd);

    private static void Register(nint instance)
    {
        if (s_registered)
        {
            return;
        }
        fixed (char* className = ClassName)
        {
            var windowClass = new Native.WNDCLASSEXW
            {
                CbSize = (uint)sizeof(Native.WNDCLASSEXW),
                LpfnWndProc = &WndProc,
                HInstance = instance,
                HCursor = Native.LoadCursorW(0, Native.IDC_ARROW),
                HbrBackground = Native.COLOR_BTNFACE + 1,
                LpszClassName = className,
            };
            if (Native.RegisterClassExW(windowClass) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
        }
        s_registered = true;
    }

    private static Native.MONITORINFO MonitorUnderCursor()
    {
        _ = Native.GetCursorPos(out var cursor);
        var info = new Native.MONITORINFO { CbSize = (uint)sizeof(Native.MONITORINFO) };
        _ = Native.GetMonitorInfoW(Native.MonitorFromPoint(cursor, Native.MONITOR_DEFAULTTONEAREST), ref info);
        return info;
    }

    private static int Scale(int value, uint dpi) => (int)((value * (long)dpi + (Native.USER_DEFAULT_SCREEN_DPI / 2)) / Native.USER_DEFAULT_SCREEN_DPI);

    /// <summary>Creates the fonts for <paramref name="dpi"/> and lays the controls out for it.</summary>
    /// <remarks>
    /// Buttons use the system message font. The status text uses a fixed-pitch font of the same size, because
    /// <see cref="Ipc.StatusText"/> lines its values up with spaces, which only works when every character is as wide as the next.
    /// </remarks>
    private void ApplyDpi(uint dpi)
    {
        var metrics = new Native.NONCLIENTMETRICSW { CbSize = (uint)sizeof(Native.NONCLIENTMETRICSW) };
        _ = Native.SystemParametersInfoForDpi(Native.SPI_GETNONCLIENTMETRICS, metrics.CbSize, &metrics, 0, dpi);
        var textFont = metrics.LfMessageFont;
        textFont.LfPitchAndFamily = Native.FIXED_PITCH | Native.FF_MODERN;
        "Consolas\0".CopyTo(new Span<char>(textFont.LfFaceName, 32));

        DeleteFonts();
        _uiFont = Native.CreateFontIndirectW(metrics.LfMessageFont);
        _textFont = Native.CreateFontIndirectW(textFont);
        _ = Native.SendMessageW(_close, Native.WM_SETFONT, _uiFont, 1);
        _ = Native.SendMessageW(_edit, Native.WM_SETFONT, _textFont, 1);

        var margin = Scale(Margin, dpi);
        var width = Scale(ClientWidth, dpi);
        var height = Scale(ClientHeight, dpi);
        var buttonWidth = Scale(ButtonWidth, dpi);
        var buttonHeight = Scale(ButtonHeight, dpi);
        var buttonTop = height - margin - buttonHeight;
        _ = Native.SetWindowPos(_edit, 0, margin, margin, width - (2 * margin), buttonTop - (2 * margin), Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        _ = Native.SetWindowPos(_close, 0, width - margin - buttonWidth, buttonTop, buttonWidth, buttonHeight, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    private void DeleteFonts()
    {
        if (_uiFont != 0)
        {
            _ = Native.DeleteObject(_uiFont);
            _uiFont = 0;
        }
        if (_textFont != 0)
        {
            _ = Native.DeleteObject(_textFont);
            _textFont = 0;
        }
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            var self = s_open;
            if (self is not null && self._hwnd == hwnd && self.Handle(message, wParam, lParam) is { } handled)
            {
                return handled;
            }
        }
        catch (Exception e)
        {
            // An exception must never unwind into user32: it would take the process down.
            Debug.WriteLine($"status window: {e}");
        }
        return Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Handles one message; null passes it on to <c>DefWindowProc</c>.</summary>
    private nint? Handle(uint message, nint wParam, nint lParam)
    {
        switch (message)
        {
            case Native.WM_COMMAND when Native.LoWord(wParam) is Native.IDCANCEL or Native.IDOK:
                Close();
                return 0;

            case Native.WM_DPICHANGED:
                var suggested = (Native.RECT*)lParam;
                _ = Native.SetWindowPos(
                    _hwnd, 0, suggested->Left, suggested->Top, suggested->Right - suggested->Left, suggested->Bottom - suggested->Top,
                    Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
                ApplyDpi(Native.LoWord(wParam));
                return 0;

            case Native.WM_DESTROY:
                DeleteFonts();
                s_open = null;
                _closed();
                return 0;

            default:
                return null;
        }
    }
}
