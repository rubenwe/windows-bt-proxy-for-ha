using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Setup;

/// <summary>
/// <c>install</c>, <c>uninstall [--keep-settings]</c> and <c>key show|rotate|clear</c>. <c>install</c> and <c>uninstall</c> relaunch
/// themselves through a UAC prompt when not elevated; <c>key</c> commands do not (a relaunched console's output would be lost, and
/// the key file is unreadable to a normal user), they ask for an elevated terminal instead.
/// </summary>
public static class SetupCommands
{
    /// <summary>Appended to the arguments of the elevated copy; it keeps its console window open at the end.</summary>
    internal const string RelaunchedFlag = "--relaunched";

    private const string NotElevated = "Run this from an elevated terminal (Run as administrator).";

    private const string Usage =
        "Usage:\n" +
        "  windows-bt-proxy install                  copy to Program Files, register and start the service, open the firewall\n" +
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
        try
        {
            return await DispatchAsync(rest, context, ct);
        }
        finally
        {
            if (relaunched)
            {
                context.WaitForKey?.Invoke();
            }
        }
    }

    private static async Task<int> DispatchAsync(string[] args, SetupContext context, CancellationToken ct)
    {
        try
        {
            if (Is(args[0], "install") && args.Length == 1)
            {
                return await ElevatedAsync(args, context, ct, async () =>
                {
                    await new Installer(context.Runner, context.Paths, context.Log).InstallAsync(ct);
                    return 0;
                });
            }
            if (Is(args[0], "uninstall") && (args.Length == 1 || (args.Length == 2 && Is(args[1], "--keep-settings"))))
            {
                var keep = args.Length == 2;
                return await ElevatedAsync(args, context, ct, async () =>
                {
                    await new Installer(context.Runner, context.Paths, context.Log).UninstallAsync(keep, ct);
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

        context.Out.WriteLine(Usage);
        return 2;
    }

    private static Task<int> ElevatedAsync(string[] args, SetupContext context, CancellationToken ct, Func<Task<int>> run)
        => context.Elevation.IsElevated ? run() : context.Elevation.RelaunchElevatedAsync(args, context.Out, ct);

    private static bool Is(string arg, string verb) => string.Equals(arg, verb, StringComparison.OrdinalIgnoreCase);
}
