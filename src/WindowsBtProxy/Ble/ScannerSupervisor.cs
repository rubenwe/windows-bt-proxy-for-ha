using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Ble;

/// <summary>
/// Keeps an advertisement source alive: starts it, forwards its advertisements and status, and while the source reports
/// <see cref="ScannerStatus.NoAdapter"/>, <see cref="ScannerStatus.RadioOff"/> or <see cref="ScannerStatus.Failed"/> retries
/// <see cref="IAdvertisementSource.StartAsync"/> every <see cref="RetryInterval"/>.
/// </summary>
/// <remarks>
/// After <see cref="ScannerStatus.Failed"/> (or a throwing <c>StartAsync</c>) the source is disposed and a fresh one is built
/// from the factory; after the other two states the same source is started again. <see cref="StartAsync"/> does not throw
/// because the scanner is unavailable: that is reported through <see cref="Status"/> and retried.
/// </remarks>
public sealed class ScannerSupervisor : IAdvertisementSource
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(10);

    private readonly Func<IAdvertisementSource> _factory;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Lock _gate = new();
    private Slot? _slot;
    private Task? _loop;
    private int _status = (int)ScannerStatus.Starting;
    private int _state; // 0 = new, 1 = started, 2 = disposed

    public ScannerSupervisor(Func<IAdvertisementSource> factory, TimeProvider time, ILogger log)
    {
        _factory = factory;
        _time = time;
        _log = log;
    }

    public event Action<RawAdvertisement>? Received;

    public event Action<ScannerStatus>? StatusChanged;

    public ScannerStatus Status => (ScannerStatus)Volatile.Read(ref _status);

    public ulong AdapterAddress => Volatile.Read(ref _slot)?.Source.AdapterAddress ?? 0;

    /// <summary>Makes the first start attempt and then supervises in the background until disposed.</summary>
    /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled (or the supervisor disposed) during the first attempt.</exception>
    public async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var firstAttempt = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_state == 2, this);
            if (_state != 0) throw new InvalidOperationException("Already started.");

            // Published under the same lock DisposeAsync reads it under, and handed to the pool so nothing (factory, inner start)
            // runs before the loop is visible: a dispose can never find "started" with no loop to wait for.
            _state = 1;
            _loop = Task.Run(() => RunAsync(ct, firstAttempt), CancellationToken.None);
        }

        await firstAttempt.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationToken startCt, TaskCompletionSource firstAttempt)
    {
        try
        {
            var ct = _stop.Token;
            ct.ThrowIfCancellationRequested();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(startCt, ct))
            {
                await AttemptAsync(linked.Token).ConfigureAwait(false);
            }

            firstAttempt.TrySetResult();

            while (!ct.IsCancellationRequested)
            {
                if (Status is ScannerStatus.Running or ScannerStatus.Starting)
                {
                    await _wake.WaitAsync(ct).ConfigureAwait(false);
                    continue;
                }

                _log.LogDebug("Scanner is {Status}; retrying in {Seconds:0} s", Status, RetryInterval.TotalSeconds);
                await Task.Delay(RetryInterval, _time, ct).ConfigureAwait(false);
                if (Status is ScannerStatus.Running or ScannerStatus.Starting) continue;

                await AttemptAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException e)
        {
            // Stopped, or the caller cancelled the first attempt.
            firstAttempt.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            // AttemptAsync handles everything a source can throw, so this is a bug here; the task must still never fault unobserved.
            _log.LogError(e, "Scanner supervision stopped unexpectedly");
            firstAttempt.TrySetException(e);
        }
        finally
        {
            // However the loop ends (disposed, cancelled, crashed), a caller still waiting on the first attempt is released.
            firstAttempt.TrySetCanceled();
        }
    }

    private async Task AttemptAsync(CancellationToken ct)
    {
        Slot slot;
        try
        {
            slot = await ObtainSlotAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || Volatile.Read(ref _state) == 2)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Could not create a scanner source");
            SetStatus(Volatile.Read(ref _slot), ScannerStatus.Failed);
            return;
        }

        try
        {
            _log.LogDebug("Starting the scanner source");
            await slot.Source.StartAsync(ct).ConfigureAwait(false);
            SyncAfterStart(slot);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Scanner source failed to start");
            SetStatus(slot, ScannerStatus.Failed);
        }
    }

    /// <summary>The current source, or a fresh one when there is none yet or the current one failed.</summary>
    private async Task<Slot> ObtainSlotAsync()
    {
        var current = Volatile.Read(ref _slot);
        if (current is not null && Status != ScannerStatus.Failed) return current;

        var fresh = new Slot(_factory());
        fresh.OnReceived = ad => Forward(fresh, ad);
        fresh.OnStatus = status => SetStatus(fresh, status);
        fresh.Source.Received += fresh.OnReceived;
        fresh.Source.StatusChanged += fresh.OnStatus;

        bool disposed;
        lock (_gate)
        {
            disposed = _state == 2;
            if (!disposed) Volatile.Write(ref _slot, fresh);
        }

        if (disposed)
        {
            // Disposed while the factory ran: nobody will ever own this source, so it must not outlive us.
            await DetachAsync(fresh).ConfigureAwait(false);
            throw new OperationCanceledException();
        }

        if (current is not null)
        {
            _log.LogDebug("Replacing the failed scanner source");
            await DetachAsync(current).ConfigureAwait(false);
        }

        return fresh;
    }

    private async Task DetachAsync(Slot slot)
    {
        slot.Source.Received -= slot.OnReceived;
        slot.Source.StatusChanged -= slot.OnStatus;
        try
        {
            await slot.Source.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Disposing a scanner source failed");
        }
    }

    private void Forward(Slot slot, RawAdvertisement ad)
    {
        if (!ReferenceEquals(Volatile.Read(ref _slot), slot)) return;

        try
        {
            Received?.Invoke(ad);
        }
        catch (Exception e)
        {
            _log.LogError(e, "A Received handler threw");
        }
    }

    /// <summary>Takes over the source's own status when it announced none while starting (or announced it before we subscribed).</summary>
    private void SyncAfterStart(Slot slot)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_slot, slot)) return;
            if (Status is not (ScannerStatus.Starting or ScannerStatus.Failed)) return;
            ApplyStatus(slot.Source.Status);
        }
    }

    private void SetStatus(Slot? slot, ScannerStatus status)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_slot, slot)) return;
            ApplyStatus(status);
        }
    }

    // Raised under the lock so subscribers see transitions in order; nothing here calls back into a source.
    private void ApplyStatus(ScannerStatus status)
    {
        var old = Status;
        if (old == status) return;

        Volatile.Write(ref _status, (int)status);
        _log.LogInformation("Scanner status {Old} -> {New}", old, status);
        _wake.Release();
        try
        {
            StatusChanged?.Invoke(status);
        }
        catch (Exception e)
        {
            _log.LogError(e, "A StatusChanged handler threw");
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task? loop;
        lock (_gate)
        {
            if (_state == 2) return;

            _state = 2;
            loop = _loop;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        if (loop is not null) await loop.ConfigureAwait(false);

        Slot? slot;
        lock (_gate)
        {
            slot = _slot;
            _slot = null;
        }

        if (slot is not null) await DetachAsync(slot).ConfigureAwait(false);
        _stop.Dispose();
    }

    private sealed class Slot(IAdvertisementSource source)
    {
        public IAdvertisementSource Source { get; } = source;

        public Action<RawAdvertisement> OnReceived { get; set; } = null!;

        public Action<ScannerStatus> OnStatus { get; set; } = null!;
    }
}
