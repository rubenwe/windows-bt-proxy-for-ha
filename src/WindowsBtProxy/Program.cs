using System.Reflection;
using WindowsBtProxy.Hosting;
using WindowsBtProxy.Setup;

// First, before anything touches Console: the manifest detaches this console-subsystem exe from any console it did not inherit
// (so `tray` shows no window); every other verb gets its console (or the parent's) back here.
if (ConsoleAttach.NeedsConsole(args))
{
    ConsoleAttach.Ensure();
}

// install, uninstall and key: handled (and elevated) by the setup commands.
if (SetupCommands.TryRunAsync(args, CancellationToken.None) is { } setup)
{
    return await setup;
}

var verb = args.Length > 0 ? args[0].ToLowerInvariant() : string.Empty;
switch (verb)
{
    case "version":
        Console.WriteLine(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
        return 0;

    case "run" or "service":
        if (!CommandLine.TryParseHostOptions(args[1..], asService: verb == "service", out var options, out var error))
        {
            Console.Error.WriteLine($"error: {error}");
            Console.Error.WriteLine(CommandLine.Usage);
            return 1;
        }
        return await ProxyHost.RunAsync(options!, CancellationToken.None);

    default:
        Console.WriteLine(CommandLine.Usage);
        return 1;
}
