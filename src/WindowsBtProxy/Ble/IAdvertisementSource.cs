namespace WindowsBtProxy.Ble;

public enum ScannerStatus
{
    Starting,
    Running,
    NoAdapter,
    RadioOff,
    Failed,
}

/// <summary>A producer of BLE advertisements (the Windows watcher, or a fake for tests and demos).</summary>
/// <remarks><see cref="Received"/> and <see cref="StatusChanged"/> may be raised from any thread.</remarks>
public interface IAdvertisementSource : IAsyncDisposable
{
    event Action<RawAdvertisement>? Received;

    event Action<ScannerStatus>? StatusChanged;

    ScannerStatus Status { get; }

    /// <summary>Address of the local Bluetooth adapter (0 if there is none).</summary>
    ulong AdapterAddress { get; }

    Task StartAsync(CancellationToken ct);
}
