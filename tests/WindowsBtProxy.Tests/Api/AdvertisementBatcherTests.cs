using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Api;
using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Tests.Api;

public class AdvertisementBatcherTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static RawAdvertisement Ad(int n) => new((ulong)n, -60, 0, [(byte)n]);

    /// <summary>Records flushed batches and optionally blocks each flush until released.</summary>
    private sealed class Sink
    {
        private readonly Channel<IReadOnlyList<RawAdvertisement>> _batches = Channel.CreateUnbounded<IReadOnlyList<RawAdvertisement>>();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Sink(bool blocked = false)
        {
            if (!blocked) _gate.SetResult();
        }

        public void Release() => _gate.TrySetResult();

        public async ValueTask Flush(IReadOnlyList<RawAdvertisement> batch)
        {
            // Copy: the batcher may reuse its list once flush returns.
            _batches.Writer.TryWrite(batch.ToArray());
            await _gate.Task;
        }

        public async Task<IReadOnlyList<RawAdvertisement>> NextAsync() => await _batches.Reader.ReadAsync().AsTask().WaitAsync(Timeout);

        public bool TryNext(out IReadOnlyList<RawAdvertisement>? batch) => _batches.Reader.TryRead(out batch);
    }

    /// <summary>
    /// Advances fake time until the next flush shows up. Looping is needed because the batcher's reader loop may not have
    /// armed its timer yet when the test first advances the clock.
    /// </summary>
    private static async Task<IReadOnlyList<RawAdvertisement>> NextAfterAdvancingAsync(Sink sink, FakeTimeProvider time)
    {
        var next = sink.NextAsync();
        var deadline = DateTime.UtcNow + Timeout;
        while (!next.IsCompleted)
        {
            Assert.True(DateTime.UtcNow < deadline, "no flush after advancing time");
            time.Advance(Interval);
            await Task.Delay(1, TestContext.Current.CancellationToken);
        }
        return await next;
    }

    [Fact]
    public async Task Flushes_full_batch_immediately()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink();
        await using var batcher = new AdvertisementBatcher(sink.Flush, time);

        for (var i = 0; i < 16; i++) batcher.Enqueue(Ad(i));

        var batch = await sink.NextAsync(); // time never advances
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (ulong)i), batch.Select(a => a.Address));
        Assert.False(sink.TryNext(out _));
    }

    [Fact]
    public async Task Flushes_partial_batch_after_interval()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink();
        await using var batcher = new AdvertisementBatcher(sink.Flush, time);

        for (var i = 0; i < 3; i++) batcher.Enqueue(Ad(i));
        await Task.Delay(50, TestContext.Current.CancellationToken); // real time: a partial batch must not flush while the fake clock stands still
        Assert.False(sink.TryNext(out _));

        var batch = await NextAfterAdvancingAsync(sink, time);
        Assert.Equal([0UL, 1UL, 2UL], batch.Select(a => a.Address));
        Assert.False(sink.TryNext(out _));
    }

    [Fact]
    public async Task Splits_large_backlog_into_batches_of_16()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink();
        await using var batcher = new AdvertisementBatcher(sink.Flush, time);

        for (var i = 0; i < 40; i++) batcher.Enqueue(Ad(i));

        var first = await sink.NextAsync();
        var second = await sink.NextAsync();
        var third = await NextAfterAdvancingAsync(sink, time);

        Assert.Equal([16, 16, 8], [first.Count, second.Count, third.Count]);
        Assert.Equal(Enumerable.Range(0, 40).Select(i => (ulong)i), first.Concat(second).Concat(third).Select(a => a.Address));
    }

    [Fact]
    public async Task Overflow_drops_oldest_and_counts()
    {
        var time = new FakeTimeProvider();
        var sink = new Sink(blocked: true);
        await using var batcher = new AdvertisementBatcher(sink.Flush, time);
        try
        {
            // Park the reader inside a blocked flush holding ads 0..15, so the queue itself is empty.
            for (var i = 0; i < 16; i++) batcher.Enqueue(Ad(i));
            var held = await sink.NextAsync();
            Assert.Equal(16, held.Count);

            // 1030 more into a 1024-deep queue: the oldest overflow.
            const int total = 16 + 1030;
            for (var i = 16; i < total; i++) batcher.Enqueue(Ad(i));
            Assert.InRange(batcher.Dropped, 6, 22);

            sink.Release();
            var flushed = held.ToList();
            while (flushed.Count + batcher.Dropped < total) flushed.AddRange(await sink.NextAsync());

            Assert.Equal(total, flushed.Count + batcher.Dropped);
            var addresses = flushed.Select(a => a.Address).ToList();
            Assert.Contains((ulong)(total - 1), addresses);          // newest survived and was flushed
            Assert.DoesNotContain((ulong)16, addresses);             // oldest queued ad was the one dropped
            Assert.Equal(addresses.Order(), addresses);              // order preserved
        }
        finally
        {
            sink.Release(); // a failed assertion must not leave a flush blocked, or DisposeAsync would hang the test host
        }
    }

    /// <summary>Wraps a <see cref="FakeTimeProvider"/> and signals when the batcher arms a timer (i.e. its reader is waiting out a partial batch).</summary>
    private sealed class TimerSignalingTimeProvider(FakeTimeProvider inner) : TimeProvider
    {
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task TimerCreated => _timerCreated.Task;

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            _timerCreated.TrySetResult();
            return timer;
        }
    }

    [Fact]
    public async Task Dispose_drops_a_partial_batch_instead_of_flushing_it()
    {
        var fake = new FakeTimeProvider();
        var time = new TimerSignalingTimeProvider(fake);
        var sink = new Sink();
        var batcher = new AdvertisementBatcher(sink.Flush, time);

        for (var i = 0; i < 3; i++) batcher.Enqueue(Ad(i));
        await time.TimerCreated.WaitAsync(Timeout, TestContext.Current.CancellationToken); // reader is now parked holding a partial batch

        await batcher.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
        fake.Advance(Interval * 5);
        batcher.Enqueue(Ad(9)); // must not throw after dispose

        Assert.False(sink.TryNext(out _));
        Assert.True(batcher.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Dispose_is_idempotent()
    {
        var batcher = new AdvertisementBatcher(new Sink().Flush, new FakeTimeProvider());

        await batcher.DisposeAsync();
        await batcher.DisposeAsync();

        Assert.True(batcher.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_throwing_flush_faults_Completion_stops_queueing_and_dispose_does_not_throw()
    {
        var time = new FakeTimeProvider();
        var batcher = new AdvertisementBatcher(_ => throw new InvalidOperationException("transport closed"), time);

        for (var i = 0; i < 16; i++) batcher.Enqueue(Ad(i));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => batcher.Completion.WaitAsync(Timeout, TestContext.Current.CancellationToken));
        Assert.Equal("transport closed", ex.Message);

        for (var i = 0; i < 2000; i++) batcher.Enqueue(Ad(i)); // more than the queue holds
        Assert.Equal(0, batcher.Dropped); // ignored, not queued

        await batcher.DisposeAsync().AsTask().WaitAsync(Timeout, TestContext.Current.CancellationToken);
    }
}
