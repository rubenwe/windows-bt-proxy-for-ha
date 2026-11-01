using WindowsBtProxy.Config;

namespace WindowsBtProxy.Hosting;

/// <summary>Parses the arguments of <c>run</c> and <c>service</c>.</summary>
public static class CommandLine
{
    public const string Usage =
        "Usage:\n" +
        "  windows-bt-proxy run [--data-dir <dir>] [--fake-scanner] [--no-mdns] [--port <n>]   run in this console (Ctrl+C stops)\n" +
        "  windows-bt-proxy service                                                            what the Windows service starts\n" +
        "  windows-bt-proxy install | uninstall [--keep-settings] | key show|rotate|clear      see the README\n" +
        "  windows-bt-proxy version";

    /// <summary>
    /// Parses what follows <c>run</c> or <c>service</c>. On failure <paramref name="error"/> says what is wrong and
    /// <paramref name="options"/> is null.
    /// </summary>
    public static bool TryParseHostOptions(IReadOnlyList<string> args, bool asService, out ProxyHostOptions? options, out string? error)
    {
        options = null;
        error = null;
        string? dataDir = null;
        var fakeScanner = false;
        var noMdns = false;
        var protectKeyFile = true;
        int? port = null;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (Is(arg, "--fake-scanner"))
            {
                fakeScanner = true;
            }
            else if (Is(arg, "--no-mdns"))
            {
                noMdns = true;
            }
            else if (Is(arg, "--no-key-acl"))
            {
                // Deliberately not in the usage text: only for the interop tests, which run as a normal user.
                protectKeyFile = false;
            }
            else if (Is(arg, "--data-dir"))
            {
                if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
                {
                    error = "--data-dir needs a directory.";
                    return false;
                }
                dataDir = args[++i];
            }
            else if (Is(arg, "--port"))
            {
                if (i + 1 >= args.Count || !int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) || value is < 1 or > 65535)
                {
                    error = "--port needs a number from 1 to 65535.";
                    return false;
                }
                port = value;
                i++;
            }
            else
            {
                error = $"Unknown argument \"{arg}\".";
                return false;
            }
        }

        var paths = dataDir is null ? DataPaths.Default : new DataPaths(Path.GetFullPath(dataDir));
        options = new ProxyHostOptions(paths, asService, fakeScanner, noMdns, port, protectKeyFile);
        return true;
    }

    private static bool Is(string arg, string flag) => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);
}
