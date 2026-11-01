using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Tray;

/// <summary>Why the tray's message loop ended.</summary>
internal enum TrayExit
{
    /// <summary>Quit from the menu, the session is ending, the window was closed, or the tray is no longer wanted.</summary>
    Quit,

    /// <summary>The installed exe is a different version: start it and quit.</summary>
    RestartInto,
}

/// <summary>
/// The tray's hidden window: owns the notify icon, its menu, the timers and the status window, and turns their messages into calls on
/// <see cref="TrayController"/>. One per process (the mutex in <see cref="TrayCommand"/> makes it one per session).
/// </summary>
/// <remarks>
/// A hidden top-level window rather than a message-only one (<c>HWND_MESSAGE</c>), because only top-level windows hear the
/// <c>TaskbarCreated</c> broadcast that says Explorer restarted and forgot every icon. <c>WS_EX_TOOLWINDOW</c> keeps it off the taskbar
/// and Alt+Tab.
/// </remarks>
internal sealed unsafe class TrayWindow : IDisposable
{
    private const string ClassName = "WindowsBtProxyForHA.Tray";

    /// <summary>What the shell sends on clicks on the icon.</summary>
    private const uint CallbackMessage = Native.WM_APP + 1;

    /// <summary>What <see cref="Post"/> sends to run queued work on this thread.</summary>
    private const uint DispatchMessage = Native.WM_APP + 2;

    private const nuint PollTimer = 1;
    private const nuint LifecycleTimer = 2;
    private const uint PollMs = 5_000;
    private const uint PollMsWhileStatusOpen = 2_000;
    private const uint LifecycleMs = 30_000;

    private const nuint MenuStatus = 1;
    private const nuint MenuOpenLogs = 2;
    private const nuint MenuQuit = 3;
    private const nuint MenuShowKey = 4;

    private static TrayWindow? s_current;
    private static uint s_taskbarCreated;

    private readonly string _pipeName;
    private readonly Func<LifecycleAction> _checkLifecycle;
    private readonly Action<nint> _showKey;
    private readonly ConcurrentQueue<Action> _posted = new();
    private readonly nint[] _icons = new nint[3];
    private nint _hwnd;
    private TrayController? _controller;
    private Win32NotifyIcon? _notifyIcon;
    private StatusWindow? _status;
    private TrayExit _exit = TrayExit.Quit;

    private TrayWindow(string pipeName, Func<LifecycleAction> checkLifecycle, Action<nint> showKey)
    {
        _pipeName = pipeName;
        _checkLifecycle = checkLifecycle;
        _showKey = showKey;
    }

    /// <summary>Creates the hidden window, adds the icon (red until the first answer) and starts polling.</summary>
    /// <param name="pipeName">The service's pipe; only tests use another one.</param>
    /// <param name="request">Asks the service for its status (see <see cref="TrayController"/>).</param>
    /// <param name="checkLifecycle">The 30-second check against the installed exe and the Run value.</param>
    /// <param name="showKey">
    /// "Show encryption key…", from the menu or the status window's button; gets the window the UAC prompt belongs to.
    /// </param>
    public static TrayWindow Create(
        string pipeName, Func<CancellationToken, Task<PipeResponse?>> request, Func<LifecycleAction> checkLifecycle, Action<nint> showKey)
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("A tray window already exists.");
        }
        var instance = Native.GetModuleHandleW(null);
        fixed (char* className = ClassName)
        {
            var windowClass = new Native.WNDCLASSEXW
            {
                CbSize = (uint)sizeof(Native.WNDCLASSEXW),
                LpfnWndProc = &WndProc,
                HInstance = instance,
                LpszClassName = className,
            };
            if (Native.RegisterClassExW(windowClass) == 0)
            {
                throw new InvalidOperationException($"RegisterClassEx failed ({Marshal.GetLastPInvokeError()}).");
            }
        }
        s_taskbarCreated = Native.RegisterWindowMessageW("TaskbarCreated");

        var window = new TrayWindow(pipeName, checkLifecycle, showKey);
        s_current = window;
        try
        {
            window._hwnd = Native.CreateWindowExW(Native.WS_EX_TOOLWINDOW, ClassName, StatusWindow.Title, Native.WS_POPUP, 0, 0, 0, 0, 0, 0, instance, 0);
            if (window._hwnd == 0)
            {
                throw new InvalidOperationException($"CreateWindowEx failed ({Marshal.GetLastPInvokeError()}).");
            }

            // An elevated tray (UAC off, or started from an admin terminal) would otherwise never hear these from the unelevated Explorer.
            _ = Native.ChangeWindowMessageFilterEx(window._hwnd, s_taskbarCreated, Native.MSGFLT_ALLOW, 0);
            _ = Native.ChangeWindowMessageFilterEx(window._hwnd, CallbackMessage, Native.MSGFLT_ALLOW, 0);

            window.CreateIcons();
            window._notifyIcon = new Win32NotifyIcon(
                window._hwnd, Win32NotifyIcon.GuidFor(Environment.ProcessPath ?? ClassName), CallbackMessage, light => window._icons[(int)light]);
            window._controller = new TrayController(window._notifyIcon, request, TimeProvider.System, window.Post);
            window._controller.Changed += window.OnChanged;

            _ = Native.SetTimer(window._hwnd, PollTimer, PollMs, 0);
            _ = Native.SetTimer(window._hwnd, LifecycleTimer, LifecycleMs, 0);
            window._controller.Start();
            return window;
        }
        catch
        {
            window.Dispose();
            throw;
        }
    }

    /// <summary>Runs the message loop until the window is gone, and says why it ended.</summary>
    public TrayExit Run()
    {
        while (Native.GetMessageW(out var message, 0, 0, 0) > 0)
        {
            // Tab, Enter and Esc in the status window, as in a dialog.
            if (_status is { } status && Native.IsDialogMessageW(status.Hwnd, message))
            {
                continue;
            }
            _ = Native.TranslateMessage(message);
            _ = Native.DispatchMessageW(message);
        }
        return _exit;
    }

    /// <summary>Removes the icon and destroys the window, if that has not happened yet, and frees the icon handles.</summary>
    public void Dispose()
    {
        _notifyIcon?.Remove();
        if (_hwnd != 0 && Native.IsWindow(_hwnd))
        {
            _ = Native.DestroyWindow(_hwnd);
        }
        _controller?.Dispose();
        DestroyIcons();
        s_current = null;
    }

    /// <summary>Runs <paramref name="action"/> on this window's thread (from the request continuation, on a pool thread).</summary>
    private void Post(Action action)
    {
        _posted.Enqueue(action);
        _ = Native.PostMessageW(_hwnd, DispatchMessage, 0, 0);
    }

    /// <summary>The notification-area icon size at the DPI of the monitor the (hidden) window is on, which is the primary one.</summary>
    private void CreateIcons()
    {
        DestroyIcons();
        var size = Native.GetSystemMetricsForDpi(Native.SM_CXSMICON, Native.GetDpiForWindow(_hwnd));
        foreach (var light in Enum.GetValues<Light>())
        {
            _icons[(int)light] = IconFactory.Create(light, size);
        }
    }

    private void DestroyIcons()
    {
        for (var i = 0; i < _icons.Length; i++)
        {
            if (_icons[i] != 0)
            {
                _ = Native.DestroyIcon(_icons[i]);
                _icons[i] = 0;
            }
        }
    }

    private void OnChanged() => _status?.SetText(StatusTextFor(_controller!.LastResponse));

    private string StatusTextFor(PipeResponse? response) => response switch
    {
        null => StatusText.NotRunning(_pipeName),
        { Status: { } status } => StatusText.Format(status),
        _ => $"The service answered with an error: {response.Error ?? "no status in the answer"}.",
    };

    private void ShowStatus()
    {
        if (_status is not null)
        {
            _status.Focus();
            return;
        }
        _status = StatusWindow.Open(StatusTextFor(_controller!.LastResponse), _showKey, OnStatusClosed);
        _ = Native.SetTimer(_hwnd, PollTimer, PollMsWhileStatusOpen, 0);
        _controller.Tick();
    }

    private void OnStatusClosed()
    {
        _status = null;
        if (Native.IsWindow(_hwnd))
        {
            _ = Native.SetTimer(_hwnd, PollTimer, PollMs, 0);
        }
    }

    private void ShowMenu(int x, int y)
    {
        var menu = Native.CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }
        try
        {
            _ = Native.AppendMenuW(menu, Native.MF_STRING, MenuStatus, "Status…");
            _ = Native.AppendMenuW(menu, Native.MF_STRING, MenuShowKey, "Show encryption key…");
            _ = Native.AppendMenuW(menu, Native.MF_STRING, MenuOpenLogs, "Open log folder");
            _ = Native.AppendMenuW(menu, Native.MF_SEPARATOR, 0, null);
            _ = Native.AppendMenuW(menu, Native.MF_STRING, MenuQuit, "Quit");
            _ = Native.SetMenuDefaultItem(menu, (uint)MenuStatus, 0);

            // Without the foreground window the menu would not close when the user clicks elsewhere; the WM_NULL afterwards makes the
            // next click on the icon work (KB135788).
            var flags = Native.TPM_RETURNCMD | Native.TPM_NONOTIFY | Native.TPM_RIGHTBUTTON
                | (Native.GetSystemMetrics(Native.SM_MENUDROPALIGNMENT) != 0 ? Native.TPM_RIGHTALIGN : 0);
            _ = Native.SetForegroundWindow(_hwnd);
            var command = (nuint)Native.TrackPopupMenuEx(menu, flags, x, y, _hwnd, 0);
            _ = Native.PostMessageW(_hwnd, Native.WM_NULL, 0, 0);

            switch (command)
            {
                case MenuStatus:
                    ShowStatus();
                    break;
                case MenuShowKey:
                    // This window was made the foreground one for the menu, so the UAC prompt can come up in front.
                    _showKey(_hwnd);
                    break;
                case MenuOpenLogs:
                    OpenLogFolder();
                    break;
                case MenuQuit:
                    Quit(TrayExit.Quit);
                    break;
            }
        }
        finally
        {
            _ = Native.DestroyMenu(menu);
        }
    }

    private static void OpenLogFolder()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo("explorer.exe", $"\"{DataPaths.Default.LogDir}\"") { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            DebugOutput.Write($"tray: could not open the log folder: {e.Message}");
        }
    }

    private void CheckLifecycle()
    {
        switch (_checkLifecycle())
        {
            case LifecycleAction.Exit:
                Quit(TrayExit.Quit);
                break;
            case LifecycleAction.RestartInto:
                Quit(TrayExit.RestartInto);
                break;
        }
    }

    /// <summary>Ends the message loop: destroying the window removes the icon and posts WM_QUIT.</summary>
    private void Quit(TrayExit exit)
    {
        _exit = exit;
        _ = Native.DestroyWindow(_hwnd);
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            var self = s_current;
            if (self is not null && self._hwnd == hwnd && self.Handle(message, wParam, lParam) is { } handled)
            {
                return handled;
            }
        }
        catch (Exception e)
        {
            // An exception must never unwind into user32: it would take the process down.
            DebugOutput.Write($"tray window: {e}");
        }
        return Native.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>Handles one message; null passes it on to <c>DefWindowProc</c>.</summary>
    private nint? Handle(uint message, nint wParam, nint lParam)
    {
        if (message == s_taskbarCreated && s_taskbarCreated != 0)
        {
            // Explorer restarted (or the taskbar's scale changed): icons at the new size, then the icon again.
            CreateIcons();
            _controller!.OnTaskbarCreated();
            return 0;
        }

        switch (message)
        {
            case CallbackMessage:
                switch (Native.LoWord(lParam))
                {
                    case Native.NIN_SELECT or Native.NIN_KEYSELECT:
                        ShowStatus();
                        break;
                    case Native.WM_CONTEXTMENU:
                        // NOTIFYICON_VERSION_4 puts the anchor point in wParam.
                        ShowMenu(Native.SignedLoWord(wParam), Native.SignedHiWord(wParam));
                        break;
                }
                return 0;

            case DispatchMessage:
                while (_posted.TryDequeue(out var action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception e)
                    {
                        // One failed piece of work must not strand the rest of the queue until the next post.
                        DebugOutput.Write($"tray window: {e}");
                    }
                }
                return 0;

            case Native.WM_TIMER when wParam == (nint)PollTimer:
                _controller!.Tick();
                return 0;

            case Native.WM_TIMER when wParam == (nint)LifecycleTimer:
                CheckLifecycle();
                return 0;

            case Native.WM_ENDSESSION when wParam != 0:
                Quit(TrayExit.Quit);
                return 0;

            case Native.WM_DESTROY:
                // The status window first: closing it sets the poll timer back, which must happen before the timers go.
                try
                {
                    _status?.Close();
                    _ = Native.KillTimer(_hwnd, PollTimer);
                    _ = Native.KillTimer(_hwnd, LifecycleTimer);
                    _notifyIcon?.Remove();
                    _controller?.Dispose();
                }
                finally
                {
                    // Whatever failed above, the loop must end: a tray lingering without a window would hold the mutex for the session.
                    Native.PostQuitMessage(0);
                }
                return 0;

            default:
                return null;
        }
    }
}
