namespace WindowsBtProxy.Ble;

/// <summary>Emits three fixed BTHome advertisements per tick, for running the service without a Bluetooth adapter.</summary>
public sealed class FakeAdvertisementSource : IAdvertisementSource
{
    private readonly TimeSpan _interval;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _stop = new();
    private PeriodicTimer? _timer;
    private Task? _loop;
    private int _status = (int)ScannerStatus.Starting;

    public FakeAdvertisementSource(TimeSpan interval, TimeProvider? time = null)
    {
        _interval = interval;
        _time = time ?? TimeProvider.System;
    }

    public event Action<RawAdvertisement>? Received;

    public event Action<ScannerStatus>? StatusChanged;

    public ScannerStatus Status => (ScannerStatus)Volatile.Read(ref _status);

    public ulong AdapterAddress => 0x0000DEADBEEF01;

    public Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_timer is not null) throw new InvalidOperationException("Already started.");

        // Create the timer before the loop task starts so a tick is never missed between StartAsync returning and the loop running.
        _timer = new PeriodicTimer(_interval, _time);
        _loop = RunAsync(_timer, _stop.Token);
        Volatile.Write(ref _status, (int)ScannerStatus.Running);
        StatusChanged?.Invoke(ScannerStatus.Running);
        return Task.CompletedTask;
    }

    private async Task RunAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                // BTHome v2 service data (UUID 0xFCD2): device info 0x40, then temperature (object 0x02, sint16 * 0.01) = 0x09C4 = 25.00 C.
                Emit(0xA4C138000001, 0);
                Emit(0xC0FFEE000002, 1);
                Emit(0xA4C138000003, 0);
            }
        }
        catch (OperationCanceledException)
        {
            // disposed
        }
    }

    private void Emit(ulong address, uint addressType) =>
        Received?.Invoke(new RawAdvertisement(address, -60, addressType, AdStructures.Build([
            (0x01, new byte[] { 0x06 }),
            (0x16, new byte[] { 0xD2, 0xFC, 0x40, 0x02, 0xC4, 0x09 }),
        ])));

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _timer?.Dispose();
        if (_loop is not null) await _loop.ConfigureAwait(false);
        _stop.Dispose();
    }
}
