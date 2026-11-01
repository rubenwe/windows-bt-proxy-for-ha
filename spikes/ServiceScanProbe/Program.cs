using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Windows.Devices.Bluetooth.Advertisement;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = "BtProbe");
builder.Services.AddHostedService<ProbeWorker>();
await builder.Build().RunAsync();

internal sealed class ProbeWorker : BackgroundService
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BtProbe", "probe.log");

    private int _ads;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
        Log(CryptoRoundTrip() ? "crypto ok" : "crypto FAILED");

        var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Passive };
        watcher.Received += (_, _) => Interlocked.Increment(ref _ads);
        watcher.Stopped += (_, e) => Log($"watcher stopped: {e.Error}");
        watcher.Start();
        Log($"watcher started: {watcher.Status}");

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var count = Interlocked.Exchange(ref _ads, 0);
                Log($"{DateTime.UtcNow:o} ads={count} session={Process.GetCurrentProcess().SessionId}");
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            watcher.Stop();
        }
    }

    private static bool CryptoRoundTrip()
    {
        try
        {
            using var a = X25519DiffieHellman.GenerateKey();
            using var b = X25519DiffieHellman.GenerateKey();
            var secretA = new byte[32];
            var secretB = new byte[32];
            a.DeriveRawSecretAgreement(b.ExportPublicKey(), secretA);
            b.DeriveRawSecretAgreement(a.ExportPublicKey(), secretB);
            if (!secretA.AsSpan().SequenceEqual(secretB))
            {
                return false;
            }

            var nonce = new byte[12];
            var plain = "hello esphome"u8.ToArray();
            var cipher = new byte[plain.Length];
            var tag = new byte[16];
            using (var enc = new ChaCha20Poly1305(secretA))
            {
                enc.Encrypt(nonce, plain, cipher, tag);
            }

            var back = new byte[plain.Length];
            using (var dec = new ChaCha20Poly1305(secretB))
            {
                dec.Decrypt(nonce, cipher, tag, back);
            }

            return back.AsSpan().SequenceEqual(plain);
        }
        catch (Exception ex)
        {
            Log($"crypto exception: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static void Log(string line)
    {
        try
        {
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch
        {
            // Probe: logging must never take the service down.
        }
        Console.WriteLine(line);
    }
}
