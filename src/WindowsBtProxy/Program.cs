using WindowsBtProxy.Hosting;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Setup;
using WindowsBtProxy.Tray;

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
        Console.WriteLine(ProxyHost.Version);
        return 0;

    case "tray":
        // Synchronous on purpose: the tray's window, message loop and mutex all belong to this (the main) thread.
        return TrayCommand.Run(args);

    case "status":
        if (!CommandLine.TryParseStatusArgs(args[1..], out var pipeName, out var statusError))
        {
            Console.Error.WriteLine($"error: {statusError}");
            Console.Error.WriteLine(CommandLine.Usage);
            return 1;
        }
        return await StatusCommandAsync(pipeName);

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

// Asks the running service for its status and prints it as the tray's status window shows it.
static async Task<int> StatusCommandAsync(string pipeName)
{
    var response = await StatusPipeClient.RequestAsync(pipeName, PipeRequestTypes.Status, TimeSpan.FromSeconds(3), CancellationToken.None);
    if (response is null)
    {
        Console.WriteLine(StatusText.NotRunning(pipeName));
        return 1;
    }
    if (response.Error is not null || response.Status is null)
    {
        // A service from another release may refuse this request's version; its error code says so.
        Console.Error.WriteLine($"error: {response.Error ?? "no status in the answer"}");
        return 1;
    }
    Console.WriteLine(StatusText.Format(response.Status));
    return 0;
}
