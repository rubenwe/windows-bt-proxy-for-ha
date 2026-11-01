using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Api;
using WindowsBtProxy.Discovery;

namespace WindowsBtProxy.Tests.Discovery;

/// <summary>
/// Registers a real service with the Windows DNS-SD API. Off by default because it touches the network stack; run it with
/// <c>WBP_DNSSD_SMOKE=1 dotnet test --filter DnsSdAdvertiserManualTests</c>.
/// </summary>
public class DnsSdAdvertiserManualTests(ITestOutputHelper output)
{
    public static bool Enabled => Environment.GetEnvironmentVariable("WBP_DNSSD_SMOKE") == "1";

    [Fact(SkipUnless = nameof(Enabled), Skip = "Manual: set WBP_DNSSD_SMOKE=1 to register with the real Windows DNS-SD API")]
    public async Task Registers_and_deregisters_with_success_status()
    {
        var log = new CollectingLogger();
        var id = new DeviceIdentity("wbp-smoke-test", "WBP smoke test", 0x0200AABBCCDD, "TEST", "now");
        var interfaces = InterfaceSelector.Select(NetInterface.FromSystem(), null);
        output.WriteLine($"Interfaces: {string.Join(", ", interfaces.Select(i => $"{i.Name}={i.IPv4}"))}");
        Assert.NotEmpty(interfaces);

        using (var advertiser = new DnsSdAdvertiser(log))
        {
            advertiser.Publish(id, 6053, hasKey: false, interfaces);
            await Task.Delay(2500, TestContext.Current.CancellationToken);
            advertiser.Publish(id, 6053, hasKey: true, interfaces); // re-publish replaces
            await Task.Delay(2500, TestContext.Current.CancellationToken);
        }

        foreach (var line in log.Lines)
        {
            output.WriteLine(line);
        }

        Assert.Contains(log.Lines, l => l.Contains("DNS-SD: registered on") && l.Contains("status 0"));
        Assert.Contains(log.Lines, l => l.Contains("DNS-SD: deregistered on") && l.Contains("status 0"));
        Assert.DoesNotContain(log.Lines, l => l.StartsWith("Warning"));
    }

    private sealed class CollectingLogger : ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
    }
}
