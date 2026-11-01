using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Config;

namespace WindowsBtProxy.Setup;

/// <summary>
/// Installs, upgrades and removes the Windows service, its firewall rule and its files (spec 5.7). Every machine change goes
/// through <see cref="IProcessRunner"/> or the folders in <see cref="InstallPaths"/>, so tests can run it against temp folders and
/// a fake runner. Both operations can be repeated: install over an install is an upgrade, uninstall of nothing is a no-op.
/// </summary>
public sealed class Installer(IProcessRunner runner, InstallPaths paths, ILogger log)
{
    public const string ServiceName = "WindowsBtProxyForHA";
    public const string DisplayName = "Windows BT Proxy for Home Assistant";
    public const string Description = "Passive Bluetooth LE proxy for Home Assistant (ESPHome-compatible API).";

    /// <summary>Exit 1: a connected network is Public. Exit 2: the check itself failed. Exit 0: all fine.</summary>
    internal const string PublicNetworkCheckArgs =
        "-NoProfile -NonInteractive -Command \"try { if (Get-NetConnectionProfile -ErrorAction Stop | Where-Object NetworkCategory -eq 'Public') { exit 1 } else { exit 0 } } catch { exit 2 }\"";

    private const int ErrorServiceDoesNotExist = 1060;
    private const int ErrorServiceMarkedForDeletion = 1072;
    private const int ErrorServiceExists = 1073;
    private const int ErrorServiceAlreadyRunning = 1056;

    private static readonly char[] UnsafeInCmdLine = ['"', '%', '&', '^', '|', '<', '>'];

    /// <summary>True after <see cref="UninstallAsync"/> left the running program's folder to be removed by a helper once this process has exited.</summary>
    public bool SelfRemovalScheduled { get; private set; }

    public static string FirewallRuleName(int port) => $"Windows BT Proxy for Home Assistant (TCP {port.ToString(CultureInfo.InvariantCulture)})";

    public async Task InstallAsync(CancellationToken ct)
    {
        var source = paths.RunningExe ?? throw new SetupException("Cannot tell where this program is running from, so there is nothing to copy.");
        if (!File.Exists(source))
        {
            throw new SetupException($"The program file {source} does not exist.");
        }

        // Before anything changes: an existing data folder must not have been prepared by a normal user.
        DataDirGuard.Verify(paths.Data.Root, paths.DataInspector);

        // An existing install means the service may be running the very file we are about to overwrite (an upgrade).
        // net.exe waits until the service is stopped; a non-zero exit just means it was not installed or not running.
        log.LogInformation("Stopping the service if it is running.");
        await runner.RunAsync("net.exe", $"stop {ServiceName}", ct);

        await CopyProgramAsync(source, paths.ExePath, ct);

        var storedPort = ReadPortOrDefault();
        var port = PrepareDataDir().Port;
        var exe = paths.ExePath;

        // The service control manager takes the whole command line as one string; the program path is quoted inside it.
        var serviceArgs = $"binPath= \"\\\"{exe}\\\" service\" start= auto DisplayName= \"{DisplayName}\"";
        var create = await runner.RunAsync("sc.exe", $"create {ServiceName} {serviceArgs}", ct);
        if (create.ExitCode == ErrorServiceExists)
        {
            log.LogInformation("Service {Service} already exists; updating it.", ServiceName);
            await RequireAsync("sc.exe", $"config {ServiceName} {serviceArgs}", "update the service", ct);
        }
        else if (create.ExitCode != 0)
        {
            throw Failed("sc.exe", create, "create the service");
        }
        log.LogInformation("Service {Service} is registered (starts automatically).", ServiceName);

        var describe = await runner.RunAsync("sc.exe", $"description {ServiceName} \"{Description}\"", ct);
        if (describe.ExitCode != 0)
        {
            log.LogWarning("Could not set the service description (sc.exe exit code {Code}); continuing.", describe.ExitCode);
        }
        await RequireAsync("sc.exe", $"failure {ServiceName} reset= 86400 actions= restart/5000/restart/5000/restart/30000", "set the service to restart on failure", ct);

        // Replace, never append: a second rule with the same name would pile up on every upgrade. Rules left by an earlier port
        // (the stored one, or the default) go too. Exit 1 from netsh just means "no such rule".
        foreach (var oldPort in new[] { port, storedPort, ProxySettings.DefaultPort }.Distinct())
        {
            await runner.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRuleName(oldPort)}\"", ct);
        }
        var ruleName = FirewallRuleName(port);
        await RequireAsync(
            "netsh.exe",
            $"advfirewall firewall add rule name=\"{ruleName}\" dir=in action=allow protocol=TCP localport={port.ToString(CultureInfo.InvariantCulture)} profile=private,domain program=\"{exe}\"",
            "add the firewall rule",
            ct);
        log.LogInformation("Firewall rule \"{Rule}\" allows inbound TCP {Port} on Private and Domain networks.", ruleName, port);

        var start = await runner.RunAsync("sc.exe", $"start {ServiceName}", ct);

        await WarnIfPublicNetworkAsync(ct);

        if (start.ExitCode != 0 && start.ExitCode != ErrorServiceAlreadyRunning)
        {
            throw Failed("sc.exe", start, "start the service; it is installed, so check the log in " + paths.Data.LogDir);
        }
        log.LogInformation("Installed to {Dir}. Settings: {File}", paths.ProgramDir, paths.Data.SettingsFile);
    }

    public async Task UninstallAsync(bool keepSettings, CancellationToken ct)
    {
        var port = ReadPortOrDefault();

        log.LogInformation("Stopping the service if it is running.");
        await runner.RunAsync("net.exe", $"stop {ServiceName}", ct);

        var delete = await runner.RunAsync("sc.exe", $"delete {ServiceName}", ct);
        switch (delete.ExitCode)
        {
            case 0:
                log.LogInformation("Service {Service} removed.", ServiceName);
                break;
            case ErrorServiceDoesNotExist:
                log.LogInformation("Service {Service} is not installed.", ServiceName);
                break;
            case ErrorServiceMarkedForDeletion:
                log.LogInformation("Service {Service} is already marked for deletion; it disappears when nothing holds it open.", ServiceName);
                break;
            default:
                // Deleting the files of a service that still exists would leave it broken, so stop here.
                throw Failed("sc.exe", delete, "remove the service");
        }

        foreach (var rulePort in new[] { port, ProxySettings.DefaultPort }.Distinct())
        {
            await runner.RunAsync("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRuleName(rulePort)}\"", ct);
        }

        await RemoveProgramDirAsync(ct);

        if (keepSettings)
        {
            log.LogInformation("Kept the settings, key and logs in {Dir}.", paths.Data.Root);
        }
        else if (Directory.Exists(paths.Data.Root))
        {
            if (await TryDeleteDirectoryAsync(paths.Data.Root, ct))
            {
                log.LogInformation("Removed {Dir}.", paths.Data.Root);
            }
            else
            {
                log.LogWarning("Could not remove {Dir}; delete it yourself.", paths.Data.Root);
            }
        }
    }

    private async Task CopyProgramAsync(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.ProgramDir);
        if (SamePath(source, target))
        {
            log.LogInformation("Already running from {Path}; not copying.", target);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                log.LogInformation("Copied the program to {Path}.", target);
                return;
            }
            catch (IOException e) when (attempt < 5)
            {
                // The service was just stopped; Windows can take a moment to let go of the old file.
                log.LogDebug(e, "Copy attempt {Attempt} failed.", attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new SetupException($"Could not copy the program to {target}: {e.Message} Is the service still running? Stop it and try again.", e);
            }
        }
    }

    private ProxySettings PrepareDataDir()
    {
        if (paths.ApplyDataDirAcl)
        {
            DataDirAcl.Apply(paths.Data.Root);
        }
        else
        {
            Directory.CreateDirectory(paths.Data.Root);
        }
        return SettingsStore.LoadOrCreate(paths.Data, Environment.MachineName, log);
    }

    private async Task WarnIfPublicNetworkAsync(CancellationToken ct)
    {
        int code;
        try
        {
            code = (await runner.RunAsync("powershell.exe", PublicNetworkCheckArgs, ct)).ExitCode;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            // Only a courtesy warning: never fail the install over it.
            log.LogDebug(e, "Could not check the network profile.");
            code = 2;
        }

        if (code == 1)
        {
            log.LogWarning(
                "A connected network is set to Public. The firewall rule only covers Private and Domain networks, so Home Assistant cannot reach the proxy over a Public one. " +
                "To change a network to Private: Settings > Network & internet > Wi-Fi or Ethernet > the network > Network profile type > Private network " +
                "(or, in an elevated PowerShell: Set-NetConnectionProfile -InterfaceAlias <name> -NetworkCategory Private).");
        }
        else if (code != 0)
        {
            log.LogInformation("Could not check the network profile. If Home Assistant cannot reach the proxy, make sure the network is set to Private (the firewall rule only covers Private and Domain networks).");
        }
    }

    private async Task RemoveProgramDirAsync(CancellationToken ct)
    {
        if (!Directory.Exists(paths.ProgramDir))
        {
            return;
        }

        var running = paths.RunningExe;
        if (running is null || !IsInside(paths.ProgramDir, running))
        {
            if (await TryDeleteDirectoryAsync(paths.ProgramDir, ct))
            {
                log.LogInformation("Removed {Dir}.", paths.ProgramDir);
            }
            else
            {
                log.LogWarning("Could not remove {Dir}; delete it yourself.", paths.ProgramDir);
            }
            return;
        }

        // Windows will not delete a running program: delete everything else now, then hand the folder to a detached cmd.exe.
        foreach (var entry in Directory.EnumerateFileSystemEntries(paths.ProgramDir).ToList())
        {
            if (SamePath(entry, running))
            {
                continue;
            }
            try
            {
                if (Directory.Exists(entry))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else
                {
                    File.Delete(entry);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.LogWarning("Could not remove {Path}: {Message}", entry, e.Message);
            }
        }
        ScheduleSelfRemoval();
    }

    /// <summary>
    /// The detached helper retries <c>rmdir</c> once a second for up to a minute, because how long this process (and, when it was
    /// relaunched elevated, the process waiting for it) keeps the program file open is not predictable. It stops being busy as
    /// soon as the folder is gone. The folder name goes into a command line, so only the expected, harmless shape is accepted.
    /// </summary>
    private void ScheduleSelfRemoval()
    {
        var dir = Path.TrimEndingDirectorySeparator(paths.ProgramDir);
        if (!Path.IsPathRooted(dir) || Path.GetFileName(dir) != ServiceName || dir.IndexOfAny(UnsafeInCmdLine) >= 0)
        {
            log.LogWarning("Not scheduling removal of {Dir} (unexpected folder name); delete it yourself after this program exits.", paths.ProgramDir);
            return;
        }
        runner.StartDetached("cmd.exe", $"/c for /l %i in (1,1,60) do @if exist \"{dir}\" (rmdir /s /q \"{dir}\" 2>nul & ping -n 2 127.0.0.1 >nul)");
        SelfRemovalScheduled = true;
        log.LogInformation("{Dir} will be removed a moment after this program exits.", dir);
    }

    private async Task<bool> TryDeleteDirectoryAsync(string dir, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt == 5)
                {
                    log.LogDebug(e, "Could not delete {Dir}.", dir);
                    return false;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(300), ct);
            }
        }
    }

    /// <summary>The port in settings.json, without creating or rewriting the file; uninstall must not leave new files behind.</summary>
    private int ReadPortOrDefault()
    {
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(paths.Data.SettingsFile), JsonContext.Default.ProxySettings);
            if (settings is { Port: >= 1 and <= 65535 })
            {
                return settings.Port;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // No readable settings: the default port's rule is the best guess.
        }
        return ProxySettings.DefaultPort;
    }

    private async Task RequireAsync(string exe, string args, string what, CancellationToken ct)
    {
        var result = await runner.RunAsync(exe, args, ct);
        if (result.ExitCode != 0)
        {
            throw Failed(exe, result, what);
        }
    }

    /// <summary>Exit code, the Windows error text hint for tools that return Win32 codes (sc.exe, net.exe), and what the tool printed.</summary>
    internal static SetupException Failed(string exe, ProcessResult result, string what)
    {
        var code = result.ExitCode.ToString(CultureInfo.InvariantCulture);
        var message = $"Could not {what}: {exe} exited with code {code}";
        if (exe.Equals("sc.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("net.exe", StringComparison.OrdinalIgnoreCase))
        {
            message += $" (run \"net helpmsg {code}\" for the Windows error text)";
        }
        message += ".";
        if (!string.IsNullOrWhiteSpace(result.Output))
        {
            message += $"\n  {exe} said:\n    " + result.Output.Replace("\n", "\n    ", StringComparison.Ordinal);
        }
        return new SetupException(message);
    }

    private static bool SamePath(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsInside(string dir, string file)
        => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
