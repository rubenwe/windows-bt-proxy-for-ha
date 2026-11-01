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

    /// <summary>Raised when <see cref="Status"/> changes.</summary>
    /// <remarks>
    /// Implementations raise this on whichever thread caused the change and may hold an internal lock while doing so (so that
    /// subscribers see transitions in order). Handlers must therefore be quick, must not block, and must not dispose the source.
    /// </remarks>
    event Action<ScannerStatus>? StatusChanged;

    ScannerStatus Status { get; }

    /// <summary>Address of the local Bluetooth adapter (0 if there is none).</summary>
    ulong AdapterAddress { get; }

    Task StartAsync(CancellationToken ct);
}
