using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using WindowsBtProxy.Hosting;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tray;

/// <summary>
/// The <c>tray</c> verb: the per-user notification-area icon that the HKLM Run value starts at every sign-in. Needs no admin rights;
/// it only reads the service's status pipe, the Run value and the installed exe's version. <c>tray --show-key</c> is the copy of it
/// that "Show encryption key…" starts through UAC: it asks the service for the key, shows the answer in a message box and exits.
/// </summary>
internal static class TrayCommand
{
    /// <summary>One tray per session (<c>Local\</c>), whoever started it.</summary>
    public const string MutexName = @"Local\WindowsBtProxyForHA.Tray";

    private const string ShowKeyFlag = "--show-key";
    private const string PipeNameOption = "--pipe-name";

    /// <summary>The pipe client's own timeout for one request (the same as the <c>status</c> command's).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs the tray until Quit, the end of the session, or the lifecycle check says to stop. Returns 0 then, and also at once when
    /// another tray already runs in this session; 1 for arguments it does not know. With <c>--show-key</c>, shows the key's message
    /// box and returns 0 whatever the answer.
    /// </summary>
    /// <param name="args">The whole command line, starting with <c>tray</c>. <c>--pipe-name</c> is for tests and not in the usage text.</param>
    public static int Run(string[] args)
    {
        // Before 24H2 the manifest's consoleAllocationPolicy is ignored and a launch from the Run key (or through UAC) gets a console
        // window: drop it.
        ConsoleAttach.ReleaseIfAlone();

        if (!TryParse(args.AsSpan(1), out var pipeName, out var showKey))
        {
            return 1;
        }
        if (showKey)
        {
            // No mutex and no icon: this copy runs beside the tray that started it.
            ShowKey(pipeName);
            return 0;
        }

        var instance = SingleInstance.TryAcquire(MutexName);
        if (instance is null)
        {
            return 0;
        }

        TrayExit exit;
        using (instance)
        {
            var runningFromInstall = RunningFromInstall();
            using var window = TrayWindow.Create(
                pipeName,
                ct => Task.Run(() => StatusPipeClient.RequestAsync(pipeName, PipeRequestTypes.Status, RequestTimeout, ct), ct),
                () => CheckLifecycle(runningFromInstall),
                owner => StartShowKeyElevated(owner, pipeName));
            exit = window.Run();
        }

        // The icon is gone (disposing the window removed it) and the mutex is released and closed, so the tray started below can
        // take both over, however fast it starts.
        if (exit == TrayExit.RestartInto)
        {
            StartInstalledTray();
        }
        return 0;
    }

    /// <summary>Parses what follows <c>tray</c>: <c>[--show-key] [--pipe-name n]</c>, in any order, each at most once.</summary>
    internal static bool TryParse(ReadOnlySpan<string> args, out string pipeName, out bool showKey)
    {
        pipeName = PipeProtocol.DefaultPipeName;
        showKey = false;
        var pipeNameSeen = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], ShowKeyFlag, StringComparison.OrdinalIgnoreCase) && !showKey)
            {
                showKey = true;
            }
            else if (string.Equals(args[i], PipeNameOption, StringComparison.OrdinalIgnoreCase) && !pipeNameSeen
                && i + 1 < args.Length && !string.IsNullOrWhiteSpace(args[i + 1]))
            {
                pipeNameSeen = true;
                pipeName = args[++i];
            }
            else
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>What the show-key message box says.</summary>
    /// <param name="Text">The message.</param>
    /// <param name="OfferCopy">The message holds the key and asks (Yes/No) whether to copy it to the clipboard.</param>
    /// <param name="Warning">Something stood in the way (warning icon), rather than an answer (information icon).</param>
    internal readonly record struct KeyMessageBox(string Text, bool OfferCopy, bool Warning);

    /// <summary>The message for the service's answer to <c>key</c>; null means no answer, so the service is not running.</summary>
    internal static KeyMessageBox KeyMessage(PipeResponse? response) => response switch
    {
        null => new("The Windows BT Proxy service is not running.", false, true),
        { Key.Key: { } key } => new(
            $"Encryption key:\n\n{key}\n\nHome Assistant asks for it only if you add the proxy by hand with a key. Copy it to the clipboard?",
            true, false),
        { Key: not null } => new("No key yet. Home Assistant provisions one when it adds the proxy.", false, false),
        { Error: PipeErrors.Denied } => new("The service only shows the key to an administrator.", false, true),
        // A service from another release may refuse this request's version; its error code says so.
        _ => new($"The service answered with an error: {response.Error ?? "no key in the answer"}.", false, true),
    };

    /// <summary>
    /// The arguments for the elevated copy: <c>tray --show-key</c>, plus the tray's own pipe name when it is not the default one
    /// (quoted the way <c>CommandLineToArgvW</c> and the runtime split a command line).
    /// </summary>
    internal static string ShowKeyArguments(string pipeName)
        => pipeName == PipeProtocol.DefaultPipeName
            ? $"tray {ShowKeyFlag}"
            : $"tray {ShowKeyFlag} {PipeNameOption} {QuoteArgument(pipeName)}";

    private static string QuoteArgument(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\"") < 0)
        {
            return argument;
        }
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            // Backslashes escape only right before a quote: there they are doubled, and one more escapes the quote itself.
            _ = quoted.Append('\\', c == '"' ? (2 * backslashes) + 1 : backslashes).Append(c);
            backslashes = 0;
        }
        // The closing quote follows, so trailing backslashes are doubled too.
        return quoted.Append('\\', 2 * backslashes).Append('"').ToString();
    }

    /// <summary>
    /// "Show encryption key…" in the tray: starts this exe again as <c>tray --show-key</c> through UAC and returns at once; the tray
    /// does not wait for it. A declined UAC prompt shows nothing; any other failure shows a short message.
    /// </summary>
    /// <param name="owner">The window the UAC prompt belongs to; with it in the foreground the prompt comes up in front, not flashing in the taskbar.</param>
    /// <param name="pipeName">The tray's pipe, passed on when it is not the default.</param>
    /// <remarks>
    /// On a short-lived STA thread of its own: <c>ShellExecuteEx</c> wants COM initialized as an STA, and it blocks until the user
    /// answers the UAC prompt, which must not stall the tray's message loop.
    /// </remarks>
    public static void StartShowKeyElevated(nint owner, string pipeName)
    {
        var arguments = ShowKeyArguments(pipeName);
        var thread = new Thread(() => RelaunchElevated(owner, arguments)) { IsBackground = true, Name = "tray: show key" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static unsafe void RelaunchElevated(nint owner, string arguments)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            _ = ShowMessage("Cannot tell where this program is running from, so it cannot ask for administrator rights.", Native.MB_OK | Native.MB_ICONWARNING);
            return;
        }

        int error;
        fixed (char* verb = "runas")
        fixed (char* file = exe)
        fixed (char* parameters = arguments)
        {
            var info = new Native.SHELLEXECUTEINFOW
            {
                CbSize = (uint)sizeof(Native.SHELLEXECUTEINFOW),
                // NOASYNC: this thread ends right after the call. FLAG_NO_UI: failures are reported below, not by the shell.
                FMask = Native.SEE_MASK_NOASYNC | Native.SEE_MASK_FLAG_NO_UI,
                Hwnd = owner,
                LpVerb = verb,
                LpFile = file,
                LpParameters = parameters,
                NShow = Native.SW_SHOWNORMAL,
            };
            if (Native.ShellExecuteExW(ref info))
            {
                return;
            }
            error = Marshal.GetLastPInvokeError();
        }

        if (error != Native.ERROR_CANCELLED)
        {
            _ = ShowMessage($"Could not ask for administrator rights: {new Win32Exception(error).Message}", Native.MB_OK | Native.MB_ICONWARNING);
        }
    }

    /// <summary>
    /// <c>tray --show-key</c>: asks the service for the key and shows the answer. Does not check for elevation itself: the service is
    /// the authority, and a copy started without elevation hears so from its <c>denied</c>.
    /// </summary>
    /// <remarks>The key goes only into the message box and, on Yes, the clipboard: never into a log, a file or the console.</remarks>
    private static void ShowKey(string pipeName)
    {
        // No window and no message loop on this thread, so blocking on the request is fine.
        var response = StatusPipeClient.RequestAsync(pipeName, PipeRequestTypes.Key, RequestTimeout, CancellationToken.None).GetAwaiter().GetResult();
        var box = KeyMessage(response);
        var type = box.OfferCopy ? Native.MB_YESNO | Native.MB_ICONINFORMATION
            : box.Warning ? Native.MB_OK | Native.MB_ICONWARNING
            : Native.MB_OK | Native.MB_ICONINFORMATION;
        if (ShowMessage(box.Text, type) != Native.IDYES || response?.Key?.Key is not { } key)
        {
            return;
        }

        var owner = Native.CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, Native.GetModuleHandleW(null), 0);
        try
        {
            if (owner == 0 || !Clipboard.SetText(owner, key))
            {
                _ = ShowMessage("The key could not be copied to the clipboard; another program may be using it. Try again.", Native.MB_OK | Native.MB_ICONWARNING);
            }
        }
        finally
        {
            if (owner != 0)
            {
                _ = Native.DestroyWindow(owner);
            }
        }
    }

    /// <summary>A message box without an owner window, on top and in the foreground so it does not open behind other windows.</summary>
    private static int ShowMessage(string text, uint type)
        => Native.MessageBoxW(0, text, StatusWindow.Title, type | Native.MB_TOPMOST | Native.MB_SETFOREGROUND);

    /// <summary>
    /// Decided once at start: the program file can be renamed while it runs (an upgrade moves a locked exe aside), and the question is
    /// where this tray was started from.
    /// </summary>
    private static bool RunningFromInstall()
        => Environment.ProcessPath is { } path
            && string.Equals(Path.GetFullPath(path), Path.GetFullPath(InstallPaths.Default().ExePath), StringComparison.OrdinalIgnoreCase);

    private static LifecycleAction CheckLifecycle(bool runningFromInstall)
        => TrayLifecycle.Decide(runningFromInstall, RunValuePresent(), ProxyHost.Version, InstalledVersion());

    /// <summary>A registry that cannot be read counts as "present": a passing problem must not make the tray quit.</summary>
    private static bool RunValuePresent()
    {
        try
        {
            return new HklmRunKey().Read() is not null;
        }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>The installed exe's version, or null while it is missing, mid-copy or unreadable (the lifecycle check then waits).</summary>
    private static string? InstalledVersion()
    {
        try
        {
            return FileVersionInfo.GetVersionInfo(InstallPaths.Default().ExePath).ProductVersion;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The same start as install's, and the Run value's: ShellExecute, in the system folder, so this tray's folder is not inherited.</summary>
    private static void StartInstalledTray()
    {
        try
        {
            ProcessRunner.StartShellDetached(InstallPaths.Default().ExePath, "tray");
        }
        catch (Win32Exception e)
        {
            // Nothing to show it in; the Run value starts the new tray at the next sign-in.
            DebugOutput.Write($"tray: could not start the installed tray: {e.Message}");
        }
    }
}
