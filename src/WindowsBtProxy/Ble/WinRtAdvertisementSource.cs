using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Extensions.Logging;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Radios;
using Windows.Foundation;
using Windows.Storage.Streams;
using WindowsBtProxy.Config;

namespace WindowsBtProxy.Ble;

/// <summary>
/// The Windows <see cref="BluetoothLEAdvertisementWatcher"/> as an <see cref="IAdvertisementSource"/>.
/// </summary>
/// <remarks>
/// <see cref="StartAsync"/> may be called again after the source reported <see cref="ScannerStatus.NoAdapter"/>,
/// <see cref="ScannerStatus.RadioOff"/> or <see cref="ScannerStatus.Failed"/>: it looks the adapter up again and builds a new
/// watcher (a watcher that was stopped by the system cannot be trusted to restart). When the radio is switched back on while
/// the source is running it restarts itself, so recovery does not have to wait for a retry. It never changes the radio's state.
/// </remarks>
public sealed class WinRtAdvertisementSource : IAdvertisementSource
{
    private readonly ScanMode _mode;
    private readonly ILogger _log;
    private readonly TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> _onReceived;
    private readonly TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> _onStopped;
    private readonly TypedEventHandler<Radio, object> _onRadioStateChanged;

    // Serializes StartAsync, the radio-on restart and DisposeAsync. Never disposed: it holds no OS resources unless a wait handle is asked for.
    private readonly SemaphoreSlim _operation = new(1, 1);
    private readonly CancellationTokenSource _disposing = new();

    // Guards the fields below and the status, so handlers on WinRT threads and the start path agree on which watcher is current.
    private readonly Lock _gate = new();
    private BluetoothLEAdvertisementWatcher? _watcher;
    private Radio? _radio;
    private bool _wantRunning;
    private bool _disposed;
    private int _disposeStarted;
    private ulong _adapterAddress;
    private int _status = (int)ScannerStatus.Starting;

    public WinRtAdvertisementSource(ScanMode mode, ILogger log)
    {
        _mode = mode;
        _log = log;
        _onReceived = OnReceived;
        _onStopped = OnStopped;
        _onRadioStateChanged = OnRadioStateChanged;
    }

    public event Action<RawAdvertisement>? Received;

    public event Action<ScannerStatus>? StatusChanged;

    public ScannerStatus Status => (ScannerStatus)Volatile.Read(ref _status);

    public ulong AdapterAddress => Volatile.Read(ref _adapterAddress);

    public async Task StartAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposing.Token);
        await _operation.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _wantRunning = true;
            await StartCoreAsync(linked.Token).ConfigureAwait(false);
        }
        finally
        {
            _operation.Release();
        }
    }

    /// <summary>Looks the adapter and radio up and starts a fresh watcher. Reports every outcome through the status; throws only when cancelled. Caller holds <see cref="_operation"/>.</summary>
    private async Task StartCoreAsync(CancellationToken ct)
    {
        try
        {
            if (Status == ScannerStatus.Running && Volatile.Read(ref _watcher) is not null) return;

            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask(ct).ConfigureAwait(false);
            if (adapter is null || !adapter.IsLowEnergySupported)
            {
                Volatile.Write(ref _adapterAddress, 0);
                Teardown(ScannerStatus.NoAdapter);
                return;
            }

            Volatile.Write(ref _adapterAddress, adapter.BluetoothAddress);
            var radio = await adapter.GetRadioAsync().AsTask(ct).ConfigureAwait(false);
            if (radio is null)
            {
                Teardown(ScannerStatus.NoAdapter);
                return;
            }

            // Subscribe before reading the state so a switch between the two cannot be missed.
            AttachRadio(radio);
            if (radio.State != RadioState.On)
            {
                Teardown(ScannerStatus.RadioOff);
                return;
            }

            var watcher = new BluetoothLEAdvertisementWatcher
            {
                ScanningMode = _mode == ScanMode.Active ? BluetoothLEScanningMode.Active : BluetoothLEScanningMode.Passive,
            };
            lock (_gate)
            {
                DetachWatcher();
                _watcher = watcher;
                watcher.Received += _onReceived;
                watcher.Stopped += _onStopped;
            }

            watcher.Start();
            SetStatus(watcher, ScannerStatus.Running);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "Starting the Bluetooth LE watcher failed");
            Teardown(ScannerStatus.Failed);
        }
    }

    /// <summary>Drops the watcher (if any) and reports <paramref name="status"/>.</summary>
    private void Teardown(ScannerStatus status)
    {
        lock (_gate)
        {
            DetachWatcher();
            ApplyStatus(status);
        }
    }

    /// <summary>Unsubscribes and stops the current watcher. Caller holds <see cref="_gate"/>.</summary>
    private void DetachWatcher()
    {
        var watcher = _watcher;
        if (watcher is null) return;

        _watcher = null;
        watcher.Received -= _onReceived;
        watcher.Stopped -= _onStopped;
        try
        {
            if (watcher.Status is BluetoothLEAdvertisementWatcherStatus.Started) watcher.Stop();
        }
        catch (Exception e)
        {
            _log.LogDebug(e, "Stopping the Bluetooth LE watcher failed");
        }
    }

    private void AttachRadio(Radio radio)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_radio, radio)) return;

            if (_radio is not null) _radio.StateChanged -= _onRadioStateChanged;
            _radio = radio;
            radio.StateChanged += _onRadioStateChanged;
        }
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
    {
        // This runs on a WinRT thread: anything thrown here would take the process down.
        try
        {
            if (!ReferenceEquals(Volatile.Read(ref _watcher), sender)) return;

            var sections = e.Advertisement.DataSections;
            var parsed = new List<(byte Type, ReadOnlyMemory<byte> Data)>(sections.Count);
            foreach (var section in sections)
            {
                parsed.Add((section.DataType, ReadBuffer(section.Data)));
            }

            var ad = new RawAdvertisement(
                e.BluetoothAddress,
                e.RawSignalStrengthInDBm,
                e.BluetoothAddressType == BluetoothAddressType.Random ? 1u : 0u,
                AdStructures.Build(parsed));
            Received?.Invoke(ad);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Dropped an advertisement");
        }
    }

    private static byte[] ReadBuffer(IBuffer buffer)
    {
        return buffer.Length == 0 ? [] : buffer.ToArray();
    }

    private void OnStopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
    {
        try
        {
            // Only unexpected stops get here: Teardown unsubscribes before it stops the watcher itself.
            var error = e.Error;
            var status = error switch
            {
                BluetoothError.RadioNotAvailable or BluetoothError.DisabledByUser => ScannerStatus.RadioOff,
                BluetoothError.DeviceNotConnected => ScannerStatus.NoAdapter,
                _ => ScannerStatus.Failed,
            };

            lock (_gate)
            {
                if (!ReferenceEquals(_watcher, sender)) return;

                _log.LogInformation("The Bluetooth LE watcher stopped ({Error}); reporting {Status}", error, status);
                DetachWatcher();
                ApplyStatus(status);
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handling the watcher stopping failed");
        }
    }

    private void OnRadioStateChanged(Radio sender, object args)
    {
        try
        {
            var state = sender.State;
            _log.LogDebug("Bluetooth radio state is now {State}", state);
            if (state == RadioState.On)
            {
                // Observed inside; the task never faults.
                _ = RestartAfterRadioOnAsync();
            }
            else
            {
                lock (_gate)
                {
                    if (!ReferenceEquals(_radio, sender)) return;

                    DetachWatcher();
                    ApplyStatus(ScannerStatus.RadioOff);
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Handling the radio state change failed");
        }
    }

    private async Task RestartAfterRadioOnAsync()
    {
        try
        {
            await _operation.WaitAsync(_disposing.Token).ConfigureAwait(false);
            try
            {
                if (_disposed || !_wantRunning || Status == ScannerStatus.Running) return;

                await StartCoreAsync(_disposing.Token).ConfigureAwait(false);
            }
            finally
            {
                _operation.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // disposing
        }
        catch (Exception e)
        {
            _log.LogError(e, "Restarting the Bluetooth LE watcher failed");
        }
    }

    private void SetStatus(BluetoothLEAdvertisementWatcher watcher, ScannerStatus status)
    {
        lock (_gate)
        {
            // The watcher may have been stopped (and the status set) between Start returning and here.
            if (!ReferenceEquals(_watcher, watcher)) return;
            ApplyStatus(status);
        }
    }

    // Raised under the lock so subscribers see transitions in order.
    private void ApplyStatus(ScannerStatus status)
    {
        var old = Status;
        if (old == status) return;

        Volatile.Write(ref _status, (int)status);
        _log.LogInformation("Bluetooth scanner status {Old} -> {New}", old, status);
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
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;

        await _disposing.CancelAsync().ConfigureAwait(false);
        await _operation.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _disposed = true;
                DetachWatcher();
                if (_radio is not null) _radio.StateChanged -= _onRadioStateChanged;
                _radio = null;
            }

            // _disposing is left undisposed on purpose: a restart racing with this must still be able to read its (cancelled) token.
        }
        finally
        {
            _operation.Release();
        }
    }
}
