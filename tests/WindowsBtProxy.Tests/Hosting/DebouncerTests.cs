using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Hosting;

namespace WindowsBtProxy.Tests.Hosting;

public sealed class DebouncerTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task Repeated_triggers_run_the_action_once_after_the_last_one()
    {
        var time = new FakeTimeProvider();
        var runs = 0;
        await using var debouncer = new Debouncer(Delay, () => Interlocked.Increment(ref runs), time, NullLogger.Instance);

        debouncer.Trigger();
        time.Advance(TimeSpan.FromSeconds(1));
        debouncer.Trigger();
        time.Advance(TimeSpan.FromSeconds(1));
        debouncer.Trigger();
        time.Advance(TimeSpan.FromSeconds(1.9));
        Assert.Equal(0, runs);

        time.Advance(TimeSpan.FromSeconds(0.2));
        Assert.Equal(1, runs);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, runs);
    }

    [Fact]
    public async Task A_trigger_during_a_run_causes_one_more_run_afterwards_never_an_overlapping_one()
    {
        var time = new FakeTimeProvider();
        using var started = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var runs = 0;
        var concurrent = 0;
        var maxConcurrent = 0;
        await using var debouncer = new Debouncer(Delay, () =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = Math.Max(maxConcurrent, now);
            Interlocked.Increment(ref runs);
            started.Release();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            Interlocked.Decrement(ref concurrent);
        }, time, NullLogger.Instance);

        debouncer.Trigger();
        var firstRun = Task.Run(() => time.Advance(Delay), TestContext.Current.CancellationToken); // FakeTimeProvider runs the callback on the advancing thread
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        debouncer.Trigger();
        debouncer.Trigger();
        release.Set();
        await firstRun;

        time.Advance(Delay);
        Assert.Equal(2, runs);
        Assert.Equal(1, maxConcurrent);
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Dispose_cancels_a_pending_run()
    {
        var time = new FakeTimeProvider();
        var runs = 0;
        var debouncer = new Debouncer(Delay, () => Interlocked.Increment(ref runs), time, NullLogger.Instance);

        debouncer.Trigger();
        await debouncer.DisposeAsync();
        debouncer.Trigger();
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(0, runs);
    }

    [Fact]
    public async Task Dispose_waits_for_a_run_in_progress()
    {
        var time = new FakeTimeProvider();
        using var started = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim();
        var finished = false;
        var debouncer = new Debouncer(Delay, () =>
        {
            started.Release();
            release.Wait(TimeSpan.FromSeconds(10));
            finished = true;
        }, time, NullLogger.Instance);

        debouncer.Trigger();
        var advancing = Task.Run(() => time.Advance(Delay), TestContext.Current.CancellationToken);
        Assert.True(await started.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        var disposing = debouncer.DisposeAsync().AsTask();
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted);

        release.Set();
        await disposing.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await advancing;
        Assert.True(finished);
    }

    [Fact]
    public async Task A_throwing_action_does_not_stop_later_runs()
    {
        var time = new FakeTimeProvider();
        var runs = 0;
        await using var debouncer = new Debouncer(Delay, () =>
        {
            if (Interlocked.Increment(ref runs) == 1)
            {
                throw new InvalidOperationException("boom");
            }
        }, time, NullLogger.Instance);

        debouncer.Trigger();
        time.Advance(Delay);
        debouncer.Trigger();
        time.Advance(Delay);

        Assert.Equal(2, runs);
    }
}
