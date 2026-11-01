using System.ComponentModel;
using System.Diagnostics;
using System.Security;
using WindowsBtProxy.Hosting;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tray;

/// <summary>
/// The <c>tray</c> verb: the per-user notification-area icon that the HKLM Run value starts at every sign-in. Needs no admin rights;
/// it only reads the service's status pipe, the Run value and the installed exe's version.
/// </summary>
internal static class TrayCommand
{
    /// <summary>One tray per session (<c>Local\</c>), whoever started it.</summary>
    public const string MutexName = @"Local\WindowsBtProxyForHA.Tray";

    /// <summary>The pipe client's own timeout for one status request (the same as the <c>status</c> command's).</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Runs the tray until Quit, the end of the session, or the lifecycle check says to stop. Returns 0 then, and also at once when
    /// another tray already runs in this session; 1 for arguments it does not know.
    /// </summary>
    /// <param name="args">The whole command line, starting with <c>tray</c>. <c>--pipe-name</c> is for tests and not in the usage text.</param>
    public static int Run(string[] args)
    {
        // Before 24H2 the manifest's consoleAllocationPolicy is ignored and a launch from the Run key gets a console window: drop it.
        ConsoleAttach.ReleaseIfAlone();

        if (!TryParse(args.AsSpan(1), out var pipeName))
        {
            return 1;
        }

        using var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            return 0;
        }

        TrayExit exit;
        try
        {
            var runningFromInstall = RunningFromInstall();
            using var window = TrayWindow.Create(
                pipeName,
                ct => Task.Run(() => StatusPipeClient.RequestAsync(pipeName, PipeRequestTypes.Status, RequestTimeout, ct), ct),
                () => CheckLifecycle(runningFromInstall));
            exit = window.Run();
        }
        finally
        {
            // The icon is gone by now (disposing the window removes it), so the next tray can take over cleanly.
            mutex.ReleaseMutex();
        }

        if (exit == TrayExit.RestartInto)
        {
            StartInstalledTray();
        }
        return 0;
    }

    private static bool TryParse(ReadOnlySpan<string> args, out string pipeName)
    {
        pipeName = PipeProtocol.DefaultPipeName;
        for (var i = 0; i < args.Length; i++)
        {
            if (!string.Equals(args[i], "--pipe-name", StringComparison.OrdinalIgnoreCase) || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            {
                return false;
            }
            pipeName = args[++i];
        }
        return true;
    }

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

    private static void StartInstalledTray()
    {
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(InstallPaths.Default().ExePath, "tray") { UseShellExecute = true });
        }
        catch (Win32Exception e)
        {
            // Nothing to show it in; the Run value starts the new tray at the next sign-in.
            Debug.WriteLine($"tray: could not start the installed tray: {e.Message}");
        }
    }
}
