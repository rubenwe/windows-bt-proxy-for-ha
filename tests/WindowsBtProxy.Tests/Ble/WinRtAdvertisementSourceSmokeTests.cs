using Microsoft.Extensions.Logging.Abstractions;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;

namespace WindowsBtProxy.Tests.Ble;

/// <summary>Needs a working Bluetooth LE adapter with devices around, so it only runs when <c>WBP_SMOKE=1</c> is set.</summary>
public class WinRtAdvertisementSourceSmokeTests(ITestOutputHelper output)
{
    public static bool Enabled => Environment.GetEnvironmentVariable("WBP_SMOKE") == "1";

    [Fact(SkipUnless = nameof(Enabled), SkipType = typeof(WinRtAdvertisementSourceSmokeTests), Skip = "Set WBP_SMOKE=1 to scan with the real adapter.")]
    public async Task Real_adapter_reports_running_and_delivers_well_formed_advertisements()
    {
        await using var source = new WinRtAdvertisementSource(ScanMode.Passive, NullLogger.Instance);
        var transitions = new List<string>();
        var count = 0;
        var malformed = 0;
        var random = 0;
        RawAdvertisement? sample = null;
        source.StatusChanged += s => { lock (transitions) transitions.Add($"{DateTime.Now:HH:mm:ss.fff} {s}"); };
        source.Received += ad =>
        {
            Interlocked.Increment(ref count);
            if (ad.AddressType == 1) Interlocked.Increment(ref random);
            if (!WellFormed(ad.Data)) Interlocked.Increment(ref malformed);
            sample ??= ad;
        };

        await source.StartAsync(TestContext.Current.CancellationToken);
        output.WriteLine($"after StartAsync: status={source.Status} adapter=0x{source.AdapterAddress:X12}");
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        output.WriteLine($"transitions: {string.Join(" | ", transitions)}");
        output.WriteLine($"ads in 10 s: {count} (random-address: {random}, malformed: {malformed})");
        if (sample is { } s) output.WriteLine($"sample: addr={s.Address:X12} rssi={s.Rssi} type={s.AddressType} data={Convert.ToHexString(s.Data)}");

        Assert.Equal(ScannerStatus.Running, source.Status);
        Assert.True(count > 0, "no advertisements received");
        Assert.Equal(0, malformed);
    }

    private static bool WellFormed(byte[] data)
    {
        var i = 0;
        while (i < data.Length)
        {
            var len = data[i];
            if (len == 0 || i + 1 + len > data.Length) return false;
            i += 1 + len;
        }

        return true;
    }
}
