using System.Security;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Setup;

/// <summary>
/// <c>install [--no-tray]</c>, <c>uninstall [--keep-settings]</c> and <c>key show|rotate|clear</c>. <c>install</c> and <c>uninstall</c>
/// relaunch themselves through a UAC prompt when not elevated; <c>key</c> commands do not (a relaunched console's output would be lost,
/// and the key file is unreadable to a normal user), they ask for an elevated terminal instead. A successful <c>install</c> also starts
/// the tray for the current user, from the least privileged process at hand: the non-elevated parent, never the elevated copy it waits for.
/// </summary>
public static class SetupCommands
{
    /// <summary>Appended to the arguments of the elevated copy; it keeps its console window open at the end.</summary>
    internal const string RelaunchedFlag = "--relaunched";

    /// <summary>Install without the tray: no autostart value (an existing one is removed) and no tray started now.</summary>
    internal const string NoTrayFlag = "--no-tray";

    private const string NotElevated = "Run this from an elevated terminal (Run as administrator).";

    private const string Usage =
        "Usage:\n" +
        "  windows-bt-proxy install [--no-tray]      copy to Program Files, register and start the service, open the firewall,\n" +
        "                                            start the tray icon at every sign-in (--no-tray: don't)\n" +
        "  windows-bt-proxy uninstall [--keep-settings]\n" +
        "  windows-bt-proxy key show|rotate|clear    manage the API encryption key (elevated terminal)";

    /// <summary>Null when <paramref name="args"/> is not a setup command (so the caller carries on); otherwise the running command and its exit code.</summary>
    public static Task<int>? TryRunAsync(string[] args, CancellationToken ct)
        => IsSetupVerb(args) ? RunAsync(args, SetupContext.CreateDefault(), ct) : null;

    internal static bool IsSetupVerb(string[] args)
        => args.Length > 0 && (Is(args[0], "install") || Is(args[0], "uninstall") || Is(args[0], "key"));

    internal static async Task<int> RunAsync(string[] args, SetupContext context, CancellationToken ct)
    {
        var relaunched = args.Contains(RelaunchedFlag, StringComparer.OrdinalIgnoreCase);
        var rest = args.Where(a => !string.Equals(a, RelaunchedFlag, StringComparison.OrdinalIgnoreCase)).ToArray();
        var outcome = new Outcome();
        try
        {
            return await DispatchAsync(rest, relaunched, context, outcome, ct);
        }
        finally
        {
            // An uninstall that is deleting its own folder must exit now: the window it would wait in holds the program file open.
            if (relaunched && !outcome.ExitNow)
            {
                context.WaitForKey?.Invoke();
            }
        }
    }

    private static async Task<int> DispatchAsync(string[] args, bool relaunched, SetupContext context, Outcome outcome, CancellationToken ct)
    {
        try
        {
            if (Is(args[0], "install") && (args.Length == 1 || (args.Length == 2 && Is(args[1], NoTrayFlag))))
            {
                var tray = args.Length == 1;
                // A relaunch passes --no-tray on to the elevated copy, which is the one that writes or removes the autostart value.
                var code = await ElevatedAsync(args, context, ct, async () =>
                {
                    await new Installer(context.Runner, context.Paths, context.Log, context.RunKey).InstallAsync(tray, ct);
                    return 0;
                });
                // The relaunched copy leaves this to the parent waiting for it, which runs as the user.
                if (code == 0 && tray && !relaunched)
                {
                    StartTray(context);
                }
                return code;
            }
            if (Is(args[0], "uninstall") && (args.Length == 1 || (args.Length == 2 && Is(args[1], "--keep-settings"))))
            {
                var keep = args.Length == 2;
                return await ElevatedAsync(args, context, ct, async () =>
                {
                    var installer = new Installer(context.Runner, context.Paths, context.Log, context.RunKey);
                    await installer.UninstallAsync(keep, ct);
                    // Only this program's own file keeps its waiting window from closing; a tray's is let go when the tray exits.
                    outcome.ExitNow = installer.SelfRemovalScheduled && installer.RunsFromProgramDir;
                    return 0;
                });
            }
            if (Is(args[0], "key") && args.Length == 2 && (Is(args[1], "show") || Is(args[1], "rotate") || Is(args[1], "clear")))
            {
                if (!context.Elevation.IsElevated)
                {
                    context.Out.WriteLine(NotElevated);
                    return 1;
                }
                return await new KeyCommands(context).RunAsync(args[1].ToLowerInvariant(), ct);
            }
        }
        catch (Exception e) when (e is SetupException or IOException or UnauthorizedAccessException or KeyStoreCorruptException)
        {
            context.Out.WriteLine($"error: {e.Message}");
            return 1;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Win32Exception, InvalidOperationException, ...: one clear line for the user, the detail for whoever debugs it.
            context.Log.LogDebug(e, "Unexpected error in {Command}.", args[0]);
            context.Out.WriteLine($"Unexpected error: {e.Message}");
            return 1;
        }

        context.Out.WriteLine(Usage);
        return 2;
    }

    private static async Task<int> ElevatedAsync(string[] args, SetupContext context, CancellationToken ct, Func<Task<int>> run)
    {
        if (context.Elevation.IsElevated)
        {
            return await run();
        }
        var code = await context.Elevation.RelaunchElevatedAsync(args, context.Out, ct);
        if (code is null)
        {
            return 1;
        }
        // The elevated copy printed into its own window, which is gone now; say at least how it ended.
        context.Out.WriteLine($"Elevated {args[0].ToLowerInvariant()} finished (exit code {code}).");
        return code.Value;
    }

    /// <summary>
    /// Starts the installed tray now rather than at the next sign-in, through ShellExecute like the Run value, so it inherits none of this
    /// process's handles (see <see cref="ProcessRunner.StartShellDetached"/>). Whatever goes wrong is only a warning: the install itself worked.
    /// </summary>
    private static void StartTray(SetupContext context)
    {
        try
        {
            // Install goes on without the autostart value when the registry refuses it, and a tray without that value quits at its first
            // lifecycle check: starting one would only show an icon that vanishes within 30 s. The elevated install already said why.
            if (AutostartMissing(context.RunKey))
            {
                context.Log.LogWarning("Not starting the tray icon: its autostart entry is missing.");
                return;
            }
            context.Runner.StartShell(context.Paths.ExePath, "tray");
            context.Out.WriteLine("Started the tray icon.");
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            context.Log.LogWarning(e, "Could not start the tray icon now; it starts at the next sign-in.");
        }
    }

    /// <summary>The tray's own rule: a value that cannot be read counts as present.</summary>
    private static bool AutostartMissing(IRunKey runKey)
    {
        try
        {
            return runKey.Read() is null;
        }
        catch (Exception e) when (e is IOException or SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Is(string arg, string verb) => string.Equals(arg, verb, StringComparison.OrdinalIgnoreCase);

    private sealed class Outcome
    {
        public bool ExitNow { get; set; }
    }
}
