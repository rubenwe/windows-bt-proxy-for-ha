using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Tests.Ble;

/// <summary>An inner source whose status, start behavior and advertisements a test drives by hand.</summary>
internal sealed class ControllableAdvertisementSource : IAdvertisementSource
{
    private readonly SemaphoreSlim _startSignal = new(0);
    private int _status = (int)ScannerStatus.Starting;
    private int _startCalls;
    private int _disposeCalls;

    /// <summary>What <see cref="StartAsync"/> does, given the 1-based call number. Default: become <see cref="ScannerStatus.Running"/>.</summary>
    public Func<int, CancellationToken, Task>? StartBehavior { get; set; }

    public event Action<RawAdvertisement>? Received;

    public event Action<ScannerStatus>? StatusChanged;

    public ScannerStatus Status => (ScannerStatus)Volatile.Read(ref _status);

    public ulong AdapterAddress { get; set; }

    public int StartCalls => Volatile.Read(ref _startCalls);

    public int DisposeCalls => Volatile.Read(ref _disposeCalls);

    public int ReceivedSubscribers => Received?.GetInvocationList().Length ?? 0;

    public int StatusSubscribers => StatusChanged?.GetInvocationList().Length ?? 0;

    public async Task StartAsync(CancellationToken ct)
    {
        var call = Interlocked.Increment(ref _startCalls);
        _startSignal.Release();
        if (StartBehavior is { } behavior)
        {
            await behavior(call, ct);
        }
        else
        {
            Set(ScannerStatus.Running);
        }
    }

    /// <summary>Completes once <see cref="StartAsync"/> has been called one more time than the last wait consumed.</summary>
    public async Task NextStartCallAsync() => await _startSignal.WaitAsync(TimeSpan.FromSeconds(10));

    /// <summary>Changes the status the way a real source would: record it, then announce it.</summary>
    public void Set(ScannerStatus status)
    {
        Volatile.Write(ref _status, (int)status);
        StatusChanged?.Invoke(status);
    }

    public void Emit(RawAdvertisement ad) => Received?.Invoke(ad);

    public ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCalls);
        return ValueTask.CompletedTask;
    }
}
