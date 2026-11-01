using System.Threading.Channels;
using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Api;

/// <summary>
/// Collects advertisements into batches for one subscriber. A batch is flushed as soon as it holds <c>maxBatch</c> ads,
/// or <c>interval</c> after its first ad was taken off the queue, whichever comes first. A slow <c>flush</c> backs ads
/// up in a bounded queue; once that is full the oldest ads are dropped and counted.
/// </summary>
/// <remarks>
/// <para><c>flush</c> takes no cancellation token, so <see cref="DisposeAsync"/> waits for a flush in progress to return. The owner
/// must close the transport <c>flush</c> writes to before disposing: a closed stream makes the pending flush throw, which ends the loop.</para>
/// <para>If <c>flush</c> throws, the loop ends, <see cref="Completion"/> faults and further <see cref="Enqueue"/> calls are ignored
/// (the owner should watch <see cref="Completion"/>). Disposing never flushes ads that are still pending and never rethrows a flush fault.</para>
/// </remarks>
public sealed class AdvertisementBatcher : IAsyncDisposable
{
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMilliseconds(100);

    private readonly Func<IReadOnlyList<RawAdvertisement>, ValueTask> _flush;
    private readonly TimeProvider _time;
    private readonly int _maxBatch;
    private readonly TimeSpan _interval;
    private readonly Channel<RawAdvertisement> _queue;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _dropped;
    private int _disposed;

    public AdvertisementBatcher(
        Func<IReadOnlyList<RawAdvertisement>, ValueTask> flush,
        TimeProvider time,
        int maxBatch = 16,
        int maxQueued = 1024,
        TimeSpan? interval = null)
    {
        _flush = flush;
        _time = time;
        _maxBatch = maxBatch;
        _interval = interval ?? DefaultInterval;
        _queue = Channel.CreateBounded<RawAdvertisement>(
            new BoundedChannelOptions(maxQueued)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => Interlocked.Increment(ref _dropped));
        _loop = Task.Run(RunAsync);
    }

    /// <summary>Number of ads discarded because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Completes when the batcher stops (disposed, or <c>flush</c> threw, in which case it faults with that exception).</summary>
    public Task Completion => _loop;

    /// <summary>Queues an ad; never blocks. Ignored once the batcher has stopped.</summary>
    public void Enqueue(RawAdvertisement ad) => _queue.Writer.TryWrite(ad);

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        var reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var batch = new List<RawAdvertisement>(_maxBatch);
                Drain(batch);
                if (batch.Count < _maxBatch)
                {
                    await FillUntilDeadlineAsync(batch, ct).ConfigureAwait(false);
                }

                if (batch.Count > 0)
                {
                    ct.ThrowIfCancellationRequested(); // disposed while the batch was filling: drop it, do not flush
                    await _flush(batch).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // disposed
        }
        finally
        {
            // Stop accepting ads whether we were disposed or flush threw, so a dead loop cannot keep counting drops.
            _queue.Writer.TryComplete();
        }
    }

    private void Drain(List<RawAdvertisement> batch)
    {
        while (batch.Count < _maxBatch && _queue.Reader.TryRead(out var ad))
        {
            batch.Add(ad);
        }
    }

    /// <summary>Tops up a partial batch until it is full or the interval has elapsed.</summary>
    private async Task FillUntilDeadlineAsync(List<RawAdvertisement> batch, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(_interval, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        try
        {
            while (batch.Count < _maxBatch && await _queue.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                Drain(batch);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // interval elapsed: flush what we have
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Cancel before completing the queue: completing first would let a parked reader see "no more items" and flush its partial batch.
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch
        {
            // A flush fault is reported through Completion, not rethrown from dispose.
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
