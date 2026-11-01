using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Hosting;

/// <summary>Parses the arguments of <c>run</c>, <c>service</c> and <c>status</c>.</summary>
public static class CommandLine
{
    public const string Usage =
        "Usage:\n" +
        "  windows-bt-proxy run [--data-dir <dir>] [--fake-scanner] [--no-mdns] [--port <n>]   run in this console (Ctrl+C stops)\n" +
        "  windows-bt-proxy service                                                            what the Windows service starts\n" +
        "  windows-bt-proxy status                                                             what the running service is doing\n" +
        "  windows-bt-proxy tray                                                               the notification-area icon (started at sign-in)\n" +
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
        var pipeName = PipeProtocol.DefaultPipeName;
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
            else if (Is(arg, "--pipe-name"))
            {
                // Not in the usage text either: tests run their own proxies beside the installed service, which owns the default name.
                if (!TryReadPipeName(args, ref i, out pipeName, out error))
                {
                    return false;
                }
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
        if (!protectKeyFile && (dataDir is null || SamePath(paths.Root, DataPaths.Default.Root)))
        {
            // The real key must never be created without its ACL: only a throwaway data dir may skip it.
            error = "--no-key-acl is only allowed with a non-default --data-dir";
            return false;
        }
        if (fakeScanner && (dataDir is null || SamePath(paths.Root, DataPaths.Default.Root)))
        {
            // The fake scanner would make the real settings, key and logs belong to a proxy that sends invented advertisements.
            error = "--fake-scanner is only allowed with a non-default --data-dir";
            return false;
        }
        // A fake proxy must never announce itself to the network (and so to Home Assistant).
        options = new ProxyHostOptions(paths, asService, fakeScanner, noMdns || fakeScanner, port, protectKeyFile, pipeName);
        return true;
    }

    /// <summary>
    /// Parses what follows <c>status</c>: only <c>--pipe-name</c>, which tests use to ask their own proxy. On failure
    /// <paramref name="error"/> says what is wrong.
    /// </summary>
    public static bool TryParseStatusArgs(IReadOnlyList<string> args, out string pipeName, out string? error)
    {
        pipeName = PipeProtocol.DefaultPipeName;
        error = null;
        for (var i = 0; i < args.Count; i++)
        {
            if (!Is(args[i], "--pipe-name"))
            {
                error = $"Unknown argument \"{args[i]}\".";
                return false;
            }
            if (!TryReadPipeName(args, ref i, out pipeName, out error))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Reads the value after <c>--pipe-name</c> at <paramref name="i"/> and moves <paramref name="i"/> onto it.</summary>
    private static bool TryReadPipeName(IReadOnlyList<string> args, ref int i, out string pipeName, out string? error)
    {
        if (i + 1 >= args.Count || string.IsNullOrWhiteSpace(args[i + 1]))
        {
            pipeName = PipeProtocol.DefaultPipeName;
            error = "--pipe-name needs a name.";
            return false;
        }
        pipeName = args[++i];
        error = null;
        return true;
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);

    private static bool Is(string arg, string flag) => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase);
}
