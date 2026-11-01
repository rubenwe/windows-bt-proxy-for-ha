using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Tests.Ble;

public class ScannerSupervisorTests
{
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    private static RawAdvertisement Ad(ulong address = 0xA4C138000001) =>
        new(address, -60, 0, AdStructures.Build([(0x01, new byte[] { 0x06 })]));

    /// <summary>A supervisor over a factory that hands out controllable sources (configured by index) and remembers them.</summary>
    private sealed class Rig : IAsyncDisposable
    {
        private readonly Func<int, ControllableAdvertisementSource, ControllableAdvertisementSource> _configure;
        private readonly SemaphoreSlim _built = new(0);

        public Rig(Action<int, ControllableAdvertisementSource>? configure = null, Func<int, bool>? factoryThrows = null)
        {
            _configure = (i, s) =>
            {
                configure?.Invoke(i, s);
                return s;
            };
            Time = new TimerCountingTimeProvider(new FakeTimeProvider());
            var attempts = 0;
            Supervisor = new ScannerSupervisor(() =>
            {
                var attempt = ++attempts;
                if (factoryThrows?.Invoke(attempt) == true) throw new InvalidOperationException("factory failed");
                var source = _configure(Created.Count, new ControllableAdvertisementSource());
                Created.Add(source);
                _built.Release();
                return source;
            }, Time, NullLogger.Instance);
            Statuses = new StatusLog(Supervisor);
        }

        public TimerCountingTimeProvider Time { get; }

        public ScannerSupervisor Supervisor { get; }

        public List<ControllableAdvertisementSource> Created { get; } = [];

        public StatusLog Statuses { get; }

        /// <summary>Waits until the factory has built a source (StartAsync builds it on the pool, not before it returns).</summary>
        public async Task<ControllableAdvertisementSource> NextBuiltSourceAsync()
        {
            if (!await _built.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("The factory was not called.");
            return Created[^1];
        }

        public ValueTask DisposeAsync() => Supervisor.DisposeAsync();
    }

    private sealed class StatusLog
    {
        private readonly List<ScannerStatus> _seen = [];
        private readonly SemaphoreSlim _changed = new(0);

        public StatusLog(IAdvertisementSource source) => source.StatusChanged += status =>
        {
            lock (_seen) _seen.Add(status);
            _changed.Release();
        };

        public ScannerStatus[] Seen
        {
            get
            {
                lock (_seen) return [.. _seen];
            }
        }

        public async Task WaitForCountAsync(int count)
        {
            while (Seen.Length < count)
            {
                if (!await _changed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) throw new TimeoutException($"Only saw [{string.Join(", ", Seen)}].");
            }
        }
    }

    [Fact]
    public async Task Starts_inner_and_forwards_advertisements()
    {
        await using var rig = new Rig((_, s) => s.AdapterAddress = 0x1234);
        var received = new List<RawAdvertisement>();
        rig.Supervisor.Received += received.Add;

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);

        var inner = Assert.Single(rig.Created);
        Assert.Equal(1, inner.StartCalls);
        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);
        Assert.Equal([ScannerStatus.Running], rig.Statuses.Seen);
        Assert.Equal(0x1234UL, rig.Supervisor.AdapterAddress);

        var ad = Ad();
        inner.Emit(ad);
        Assert.Equal(ad, Assert.Single(received));
    }

    [Fact]
    public async Task Retries_every_10s_while_no_adapter()
    {
        await using var rig = new Rig((_, s) => s.StartBehavior = (_, _) =>
        {
            s.Set(ScannerStatus.NoAdapter);
            return Task.CompletedTask;
        });

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = Assert.Single(rig.Created);
        await inner.NextStartCallAsync();
        await rig.Time.NextTimerAsync();
        Assert.Equal(1, inner.StartCalls);
        Assert.Equal(ScannerStatus.NoAdapter, rig.Supervisor.Status);

        rig.Time.Advance(Retry);
        await inner.NextStartCallAsync();
        await rig.Time.NextTimerAsync();
        Assert.Equal(2, inner.StartCalls);

        rig.Time.Advance(Retry);
        await inner.NextStartCallAsync();

        Assert.Equal(3, inner.StartCalls);
        Assert.Single(rig.Created);
        Assert.Equal(0, inner.DisposeCalls);
        Assert.Equal([ScannerStatus.NoAdapter], rig.Statuses.Seen);
    }

    [Fact]
    public async Task Recreates_source_after_failed()
    {
        await using var rig = new Rig((i, s) =>
        {
            if (i == 0)
            {
                s.StartBehavior = (_, _) =>
                {
                    s.Set(ScannerStatus.Failed);
                    return Task.CompletedTask;
                };
            }
        });
        var received = new List<RawAdvertisement>();
        rig.Supervisor.Received += received.Add;

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var first = Assert.Single(rig.Created);
        Assert.Equal(ScannerStatus.Failed, rig.Supervisor.Status);
        await rig.Time.NextTimerAsync();

        rig.Time.Advance(Retry);
        await rig.Statuses.WaitForCountAsync(2);

        Assert.Equal(2, rig.Created.Count);
        var second = rig.Created[1];
        Assert.Equal(1, second.StartCalls);
        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);
        Assert.Equal([ScannerStatus.Failed, ScannerStatus.Running], rig.Statuses.Seen);

        // The failed source is disposed and no longer wired to the supervisor.
        Assert.Equal(1, first.DisposeCalls);
        Assert.Equal(0, first.ReceivedSubscribers);
        Assert.Equal(0, first.StatusSubscribers);
        Assert.Equal(0, second.DisposeCalls);
        Assert.Equal(1, second.ReceivedSubscribers);
        Assert.Equal(1, second.StatusSubscribers);

        first.Emit(Ad(1));
        second.Emit(Ad(2));
        Assert.Equal(2UL, Assert.Single(received).Address);
    }

    [Fact]
    public async Task Status_follows_inner_through_radio_off_and_back()
    {
        await using var rig = new Rig();

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = Assert.Single(rig.Created);
        await inner.NextStartCallAsync();
        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);

        inner.Set(ScannerStatus.RadioOff);
        Assert.Equal(ScannerStatus.RadioOff, rig.Supervisor.Status);

        // The same source is restarted (not replaced) once the retry interval has passed.
        await rig.Time.NextTimerAsync();
        rig.Time.Advance(Retry);
        await rig.Statuses.WaitForCountAsync(3);

        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);
        Assert.Equal([ScannerStatus.Running, ScannerStatus.RadioOff, ScannerStatus.Running], rig.Statuses.Seen);
        Assert.Equal(2, inner.StartCalls);
        Assert.Single(rig.Created);
        Assert.Equal(0, inner.DisposeCalls);
    }

    [Fact]
    public async Task Does_not_retry_before_the_interval_has_passed()
    {
        await using var rig = new Rig((_, s) => s.StartBehavior = (_, _) =>
        {
            s.Set(ScannerStatus.RadioOff);
            return Task.CompletedTask;
        });

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = Assert.Single(rig.Created);
        await rig.Time.NextTimerAsync();

        rig.Time.Advance(Retry - TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, inner.StartCalls);
    }

    [Fact]
    public async Task Stops_retrying_once_running()
    {
        await using var rig = new Rig((_, s) => s.StartBehavior = (call, _) =>
        {
            s.Set(call == 1 ? ScannerStatus.NoAdapter : ScannerStatus.Running);
            return Task.CompletedTask;
        });

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = Assert.Single(rig.Created);
        await rig.Time.NextTimerAsync();
        rig.Time.Advance(Retry);
        await rig.Statuses.WaitForCountAsync(2);

        rig.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(2, inner.StartCalls);
        Assert.Equal([ScannerStatus.NoAdapter, ScannerStatus.Running], rig.Statuses.Seen);
    }

    [Fact]
    public async Task A_start_that_throws_counts_as_failed_and_gets_a_fresh_source()
    {
        await using var rig = new Rig((i, s) =>
        {
            if (i == 0) s.StartBehavior = (_, _) => throw new InvalidOperationException("boom");
        });

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScannerStatus.Failed, rig.Supervisor.Status);
        await rig.Time.NextTimerAsync();
        rig.Time.Advance(Retry);
        await rig.Statuses.WaitForCountAsync(2);

        Assert.Equal(2, rig.Created.Count);
        Assert.Equal(1, rig.Created[0].DisposeCalls);
        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);
        Assert.Equal([ScannerStatus.Failed, ScannerStatus.Running], rig.Statuses.Seen);
    }

    [Fact]
    public async Task A_factory_that_throws_is_retried()
    {
        await using var rig = new Rig(factoryThrows: attempt => attempt == 1);

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ScannerStatus.Failed, rig.Supervisor.Status);
        Assert.Equal(0UL, rig.Supervisor.AdapterAddress);
        Assert.Empty(rig.Created);

        await rig.Time.NextTimerAsync();
        rig.Time.Advance(Retry);
        await rig.Statuses.WaitForCountAsync(2);

        Assert.Single(rig.Created);
        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);
    }

    [Fact]
    public async Task Disposing_stops_the_retry_loop_and_the_inner_source()
    {
        await using var rig = new Rig((_, s) => s.StartBehavior = (_, _) =>
        {
            s.Set(ScannerStatus.NoAdapter);
            return Task.CompletedTask;
        });
        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = Assert.Single(rig.Created);
        await rig.Time.NextTimerAsync();

        await rig.Supervisor.DisposeAsync();
        rig.Time.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(1, inner.StartCalls);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(0, inner.ReceivedSubscribers);
        Assert.Equal(0, inner.StatusSubscribers);

        await rig.Supervisor.DisposeAsync();
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task Disposing_cancels_a_start_in_flight()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var rig = new Rig((_, s) => s.StartBehavior = async (call, ct) =>
        {
            if (call == 1)
            {
                s.Set(ScannerStatus.RadioOff);
                return;
            }

            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                cancelled.SetResult();
                throw;
            }
        });
        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = Assert.Single(rig.Created);
        await inner.NextStartCallAsync();
        await rig.Time.NextTimerAsync();
        rig.Time.Advance(Retry);
        await inner.NextStartCallAsync();

        await rig.Supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(1, inner.DisposeCalls);
        Assert.Equal(2, inner.StartCalls);
    }

    [Fact]
    public async Task Cancelling_the_first_start_propagates_and_ends_supervision()
    {
        using var cts = new CancellationTokenSource();
        await using var rig = new Rig((_, s) => s.StartBehavior = (_, ct) => Task.Delay(Timeout.Infinite, ct));

        var start = rig.Supervisor.StartAsync(cts.Token);
        var inner = await rig.NextBuiltSourceAsync();
        await inner.NextStartCallAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        rig.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, inner.StartCalls);
    }

    [Fact]
    public async Task Cannot_be_started_twice_or_after_disposal()
    {
        await using var rig = new Rig();
        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Supervisor.StartAsync(TestContext.Current.CancellationToken));

        await rig.Supervisor.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => rig.Supervisor.StartAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_throwing_subscriber_does_not_stop_supervision()
    {
        await using var rig = new Rig((_, s) => s.StartBehavior = (call, _) =>
        {
            s.Set(call == 1 ? ScannerStatus.NoAdapter : ScannerStatus.Running);
            return Task.CompletedTask;
        });
        rig.Supervisor.StatusChanged += _ => throw new InvalidOperationException("subscriber bug");
        rig.Supervisor.Received += _ => throw new InvalidOperationException("subscriber bug");

        await rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        await rig.Time.NextTimerAsync();
        rig.Time.Advance(Retry);
        await rig.Statuses.WaitForCountAsync(2);
        rig.Created.Single().Emit(Ad());

        Assert.Equal(ScannerStatus.Running, rig.Supervisor.Status);
    }

    [Fact]
    public async Task Disposing_while_the_first_start_is_in_flight_releases_the_caller_and_disposes_the_source()
    {
        await using var rig = new Rig((_, s) => s.StartBehavior = (_, ct) => Task.Delay(Timeout.Infinite, ct));

        var start = rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
        var inner = await rig.NextBuiltSourceAsync();
        await inner.NextStartCallAsync();
        await rig.Supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, inner.DisposeCalls);
    }

    [Fact]
    public async Task A_source_built_while_disposing_is_disposed_too()
    {
        using var factoryEntered = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var created = new List<ControllableAdvertisementSource>();
        await using var supervisor = new ScannerSupervisor(() =>
        {
            factoryEntered.Set();
            releaseFactory.Wait(TimeSpan.FromSeconds(10));
            var source = new ControllableAdvertisementSource();
            created.Add(source);
            return source;
        }, TimeProvider.System, NullLogger.Instance);

        var start = supervisor.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var dispose = supervisor.DisposeAsync().AsTask();
        releaseFactory.Set();

        await dispose.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var source = Assert.Single(created);
        Assert.Equal(0, source.StartCalls);
        Assert.Equal(1, source.DisposeCalls);
        Assert.Equal(0, source.ReceivedSubscribers + source.StatusSubscribers);
    }

    [Fact]
    public async Task Disposing_right_after_starting_never_leaks_a_source_or_hangs_the_caller()
    {
        for (var i = 0; i < 300; i++)
        {
            var rig = new Rig();
            var start = rig.Supervisor.StartAsync(TestContext.Current.CancellationToken);
            await rig.Supervisor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            try
            {
                await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            }
            catch (OperationCanceledException)
            {
                // dispose won the race
            }

            Assert.All(rig.Created, source => Assert.Equal(1, source.DisposeCalls));
        }
    }
}
