using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Tests.Ble;

public class FakeAdvertisementSourceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task Is_starting_until_started_then_running()
    {
        var time = new FakeTimeProvider();
        await using var source = new FakeAdvertisementSource(Interval, time);
        var statuses = new List<ScannerStatus>();
        source.StatusChanged += statuses.Add;

        Assert.Equal(ScannerStatus.Starting, source.Status);
        await source.StartAsync(CancellationToken.None);

        Assert.Equal(ScannerStatus.Running, source.Status);
        Assert.Equal([ScannerStatus.Running], statuses);
        Assert.Equal(0x0000DEADBEEF01UL, source.AdapterAddress);
    }

    [Fact]
    public async Task Each_tick_emits_the_three_fixed_advertisements()
    {
        var time = new FakeTimeProvider();
        await using var source = new FakeAdvertisementSource(Interval, time);
        var received = new List<RawAdvertisement>();
        var three = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Received += ad =>
        {
            lock (received)
            {
                received.Add(ad);
                if (received.Count == 3) three.TrySetResult();
            }
        };

        await source.StartAsync(CancellationToken.None);
        lock (received) Assert.Empty(received);
        time.Advance(Interval);
        await three.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        var expectedData = AdStructures.Build([(0x01, new byte[] { 0x06 }), (0x16, new byte[] { 0xD2, 0xFC, 0x40, 0x02, 0xC4, 0x09 })]);
        lock (received)
        {
            Assert.Equal([0xA4C138000001UL, 0xC0FFEE000002UL, 0xA4C138000003UL], received.Select(a => a.Address));
            Assert.Equal([0u, 1u, 0u], received.Select(a => a.AddressType));
            Assert.All(received, a => Assert.Equal(expectedData, a.Data));
        }
    }

    [Fact]
    public async Task Stops_emitting_after_dispose()
    {
        var time = new FakeTimeProvider();
        var source = new FakeAdvertisementSource(Interval, time);
        var count = 0;
        source.Received += _ => Interlocked.Increment(ref count);
        await source.StartAsync(CancellationToken.None);

        await source.DisposeAsync();
        time.Advance(Interval * 3);
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref count));
    }
}
