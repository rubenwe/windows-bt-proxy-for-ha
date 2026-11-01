using System.Security.Cryptography;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Setup;

/// <summary>
/// <c>key show|rotate|clear</c>: local management of the Noise key in <c>noise.key</c>. The file is readable only by SYSTEM and
/// Administrators, so the caller has to be elevated (checked by <see cref="SetupCommands"/>). Rotate and clear restart a running
/// service, because it only reads the key file when it starts.
/// </summary>
public sealed class KeyCommands(SetupContext context)
{
    private readonly KeyStore _store = new(context.Paths.Data.KeyFile, context.ApplyKeyAcl);

    public Task<int> RunAsync(string subcommand, CancellationToken ct)
    {
        // Same rule as install: do not read from or write into a data folder a normal user could have prepared.
        DataDirGuard.Verify(context.Paths.Data.Root, context.Paths.DataInspector);
        return Dispatch(subcommand, ct);
    }

    private Task<int> Dispatch(string subcommand, CancellationToken ct) => subcommand switch
    {
        "show" => Task.FromResult(Show()),
        "rotate" => RotateAsync(ct),
        "clear" => ClearAsync(ct),
        _ => Task.FromResult(2),
    };

    private int Show()
    {
        byte[]? key;
        try
        {
            key = _store.Load();
        }
        catch (KeyStoreCorruptException e)
        {
            context.Out.WriteLine($"error: {e.Message} Run \"key rotate\" to replace it or \"key clear\" to remove it.");
            return 1;
        }

        if (key is null)
        {
            context.Out.WriteLine("no key - HA will provision one when you add the proxy");
            return 0;
        }
        try
        {
            context.Out.WriteLine(Convert.ToBase64String(key));
            return 0;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private async Task<int> RotateAsync(CancellationToken ct)
    {
        if (!Directory.Exists(context.Paths.Data.Root))
        {
            context.Out.WriteLine($"error: {context.Paths.Data.Root} does not exist. Run \"install\" first.");
            return 1;
        }

        var key = RandomNumberGenerator.GetBytes(KeyStore.KeySize);
        try
        {
            _store.Save(key);
            context.Out.WriteLine($"New key: {Convert.ToBase64String(key)}");
            context.Out.WriteLine("Home Assistant will ask for this key the next time it connects (Settings > Devices & services > ESPHome > the proxy > enter the encryption key).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Out.WriteLine($"error: could not save the key: {e.Message}");
            return 1;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        await RestartServiceIfRunningAsync(ct);
        return 0;
    }

    private async Task<int> ClearAsync(CancellationToken ct)
    {
        if (!File.Exists(_store.Path))
        {
            context.Out.WriteLine("There is no key file, so nothing was changed: Home Assistant can already provision a key.");
            return 0;
        }

        try
        {
            File.Delete(_store.Path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            context.Out.WriteLine($"error: could not delete {_store.Path}: {e.Message}");
            return 1;
        }

        context.Out.WriteLine("Key removed. The proxy accepts unencrypted connections again, and Home Assistant can provision a new key when you add it.");
        await RestartServiceIfRunningAsync(ct);
        return 0;
    }

    /// <summary>net.exe stop waits until the service has stopped and fails if it was not running; only a service that was running is started again.</summary>
    private async Task RestartServiceIfRunningAsync(CancellationToken ct)
    {
        if ((await context.Runner.RunAsync("net.exe", $"stop {Installer.ServiceName}", ct)).ExitCode != 0)
        {
            context.Out.WriteLine("The service is not running; the change applies the next time it starts.");
            return;
        }
        var start = await context.Runner.RunAsync("net.exe", $"start {Installer.ServiceName}", ct);
        if (start.ExitCode == 0)
        {
            context.Out.WriteLine("The service was restarted, so open Home Assistant sessions were dropped and reconnect using the current key setting.");
        }
        else
        {
            var said = string.IsNullOrWhiteSpace(start.Output) ? string.Empty : $" net.exe said: {start.Output.Replace("\n", " ", StringComparison.Ordinal)}";
            context.Out.WriteLine($"error: the service was stopped but did not start again (net.exe exit code {start.ExitCode}).{said} Start it with: net start {Installer.ServiceName}");
        }
    }
}
