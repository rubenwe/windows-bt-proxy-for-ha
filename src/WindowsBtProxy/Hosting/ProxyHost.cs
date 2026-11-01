using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using WindowsBtProxy.Api;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Crypto;
using WindowsBtProxy.Discovery;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Hosting;

/// <param name="Paths">Where settings, key and logs live.</param>
/// <param name="AsService">Started by the service control manager: Windows service lifetime, Event Log, no console output.</param>
/// <param name="FakeScanner">Emit canned advertisements instead of scanning (tests, demos).</param>
/// <param name="NoMdns">Do not register with DNS-SD.</param>
/// <param name="PortOverride">TCP port to listen on instead of the one in settings.json.</param>
/// <param name="ProtectKeyFile">Create noise.key with an ACL of SYSTEM and Administrators only (always, in production). Off only for tests that run as a normal user (<c>--no-key-acl</c>).</param>
public sealed record ProxyHostOptions(DataPaths Paths, bool AsService, bool FakeScanner, bool NoMdns, int? PortOverride, bool ProtectKeyFile = true);

/// <summary>Process exit codes of <c>run</c> and <c>service</c>.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failed = 1;

    /// <summary>noise.key exists but cannot be used. The API was never started.</summary>
    public const int CorruptKey = 2;

    public const int PortInUse = 3;

    /// <summary>ChaCha20-Poly1305 or X25519 is not available from the operating system.</summary>
    public const int CryptoUnsupported = 4;
}

/// <summary>Startup cannot continue; <see cref="ExitCode"/> says why.</summary>
public sealed class HostFatalException(int exitCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>Runs the proxy: scanner, API server, DNS-SD and the periodic summary, under the Generic Host.</summary>
public static class ProxyHost
{
    /// <summary>Time the host gives <see cref="ProxyRuntime"/> to wind down (close clients, stop the scanner, deregister DNS-SD).</summary>
    internal static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Returns when the host has stopped (Ctrl+C, service stop, <paramref name="ct"/> cancelled, or a fatal error): the process exit code.</summary>
    public static async Task<int> RunAsync(ProxyHostOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        // The log level lives in settings.json, so the settings are read before the real loggers exist.
        ProxySettings settings;
        using (var bootstrap = LoggerFactory.Create(logging => ConfigureLogging(logging, options, LogLevel.Information)))
        {
            settings = SettingsStore.LoadOrCreate(options.Paths, Environment.MachineName, bootstrap.CreateLogger("Settings"));
        }

        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ContentRootPath = AppContext.BaseDirectory,
        });
        var level = RollingFileLogger.ParseLevel(settings.LogLevel);
        ConfigureLogging(builder.Logging, options, level);
        builder.Services.Configure<HostOptions>(o =>
        {
            o.ShutdownTimeout = ShutdownTimeout;
            o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore; // ProxyRuntime reports its own failures
        });
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        if (options.AsService)
        {
            builder.Services.AddWindowsService(o => o.ServiceName = Installer.ServiceName);
        }
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(settings);
        builder.Services.AddSingleton<ProxyRuntime>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ProxyRuntime>());

        using var host = builder.Build();
        var runtime = host.Services.GetRequiredService<ProxyRuntime>(); // host.RunAsync disposes the services when it ends
        await host.RunAsync(ct).ConfigureAwait(false);
        return runtime.ExitCode;
    }

    internal static void ConfigureLogging(ILoggingBuilder logging, ProxyHostOptions options, LogLevel level)
    {
        logging.ClearProviders();
        logging.SetMinimumLevel(level);
        logging.AddProvider(new RollingFileLogger(options.Paths.LogDir, level));
        if (options.AsService)
        {
            logging.AddEventLog(settings => settings.SourceName = Installer.ServiceName);
            logging.AddFilter<EventLogLoggerProvider>(logLevel => logLevel >= LogLevel.Error);
        }
        else
        {
            logging.AddSimpleConsole(console =>
            {
                console.SingleLine = true;
                console.TimestampFormat = "HH:mm:ss ";
            });
        }
    }

    /// <summary>A message when this machine cannot do the cryptography the API needs, otherwise null.</summary>
    internal static string? CryptoProblem(bool chaCha20Poly1305Supported, bool x25519Supported)
    {
        if (chaCha20Poly1305Supported && x25519Supported)
        {
            return null;
        }
        var missing = string.Join(" and ", new[] { chaCha20Poly1305Supported ? null : "ChaCha20-Poly1305", x25519Supported ? null : "X25519" }.OfType<string>());
        return $"This system does not support {missing}, which the encrypted ESPHome API needs. Windows 11 (build 22000 or later) is required.";
    }

    internal static int ExitCodeFor(Exception e) => e switch
    {
        HostFatalException fatal => fatal.ExitCode,
        KeyStoreCorruptException => ExitCodes.CorruptKey,
        _ => ExitCodes.Failed,
    };

    /// <summary>
    /// Loads the key. A key file that is merely locked for a moment (antivirus, a concurrent <c>key rotate</c>) gets one retry
    /// after <paramref name="retryDelay"/>; anything else, or a second failure, throws <see cref="KeyStoreCorruptException"/>.
    /// </summary>
    internal static async Task<EncryptionState> LoadEncryptionAsync(Func<EncryptionState> load, TimeSpan retryDelay, TimeProvider time, ILogger log, CancellationToken ct)
    {
        try
        {
            return load();
        }
        catch (KeyStoreCorruptException e) when (e.InnerException is IOException)
        {
            log.LogWarning("The key file could not be read ({Reason}); retrying once.", e.InnerException.Message);
        }
        await Task.Delay(retryDelay, time, ct).ConfigureAwait(false);
        return load();
    }

    /// <summary>The build time of the running exe in ESPHome's style (<c>Nov 01 2026, 12:00:00</c>), as reported to Home Assistant.</summary>
    internal static string CompilationTime()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null && File.Exists(exe))
            {
                return File.GetLastWriteTimeUtc(exe).ToString("MMM dd yyyy, HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return "unknown";
    }
}

/// <summary>The hosted service: owns the scanner, the API server and DNS-SD from start until the host stops.</summary>
internal sealed class ProxyRuntime(
    ProxyHostOptions options,
    ProxySettings settings,
    ILoggerFactory loggers,
    IHostApplicationLifetime lifetime,
    TimeProvider time) : BackgroundService
{
    internal static readonly TimeSpan SummaryInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan RepublishDelay = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan KeyRetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ApiStopTimeout = TimeSpan.FromSeconds(10);

    private readonly ILogger _log = loggers.CreateLogger("WindowsBtProxy");
    private long _advertisements;

    public int ExitCode { get; private set; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // let the host finish starting; everything below may take a while
        try
        {
            await RunAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // stopping
        }
        catch (Exception e)
        {
            ExitCode = ProxyHost.ExitCodeFor(e);
            switch (e)
            {
                case HostFatalException:
                    _log.LogError("{Message}", e.Message);
                    break;
                case KeyStoreCorruptException:
                    _log.LogError(e, "{Message} The API is not started; fix or remove the key file (windows-bt-proxy key clear) and start again.", e.Message);
                    break;
                default:
                    _log.LogError(e, "The proxy stopped because of an unexpected error");
                    break;
            }
        }
        finally
        {
            // Also covers a fatal error: the host has nothing left to run.
            lifetime.StopApplication();
        }
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        if (ProxyHost.CryptoProblem(ChaCha20Poly1305.IsSupported, X25519DiffieHellman.IsSupported) is { } problem)
        {
            throw new HostFatalException(ExitCodes.CryptoUnsupported, problem);
        }

        // Before anything listens or scans: a corrupt key must fail closed, never fall back to plaintext.
        var store = new KeyStore(options.Paths.KeyFile, applyAcl: options.ProtectKeyFile);
        var encryption = await ProxyHost.LoadEncryptionAsync(() => EncryptionState.Load(store), KeyRetryDelay, time, _log, stopping).ConfigureAwait(false);

        var port = options.PortOverride ?? settings.Port;
        _log.LogInformation("Starting {Name} ({Mode} scanner, port {Port}, mDNS {Mdns}, data in {Dir})", settings.Name,
            options.FakeScanner ? "fake" : settings.ScanMode.ToString().ToLowerInvariant(), port, options.NoMdns ? "off" : "on", options.Paths.Root);

        // Whatever happens below, stop everything in the same order: API (no new clients, close the old ones), scanner, DNS-SD.
        using var running = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        ScannerSupervisor? scanner = null;
        ApiServer? server = null;
        Task? api = null;
        Task? summary = null;
        MdnsPublisher? mdns = null;
        Action<RawAdvertisement> onAdvertisement = _ => Interlocked.Increment(ref _advertisements);
        try
        {
            scanner = new ScannerSupervisor(CreateSource, time, loggers.CreateLogger("Scanner"));
            scanner.Received += onAdvertisement;
            await scanner.StartAsync(stopping).ConfigureAwait(false);

            var identity = new DeviceIdentity(settings.Name, settings.FriendlyName, await WaitForAdapterAsync(scanner, stopping).ConfigureAwait(false),
                Environment.MachineName, ProxyHost.CompilationTime());
            _log.LogInformation("Bluetooth adapter {Mac}, device name {Name}", identity.MacColon, identity.Name);

            server = new ApiServer(new IPEndPoint(IPAddress.Any, port), identity, scanner, encryption, loggers.CreateLogger<ApiServer>(), time);
            api = server.RunAsync(running.Token); // binds before its first await, so a busy port has already faulted the task
            if (api.IsCompleted)
            {
                await ThrowStartFailureAsync(api, port).ConfigureAwait(false);
            }

            if (!options.NoMdns)
            {
                mdns = new MdnsPublisher(identity, (ushort)port, encryption, settings, loggers.CreateLogger("DNS-SD"), time);
                mdns.Start();
            }

            summary = SummaryLoopAsync(scanner, server, running.Token);
            _log.LogInformation("Proxy is running");

            await Task.WhenAny(api, summary).ConfigureAwait(false);
            if (!stopping.IsCancellationRequested)
            {
                await ThrowStartFailureAsync(api, port).ConfigureAwait(false);
            }
        }
        finally
        {
            await ShutDownAsync(running, api, summary, scanner, mdns, onAdvertisement).ConfigureAwait(false);
        }
    }

    private IAdvertisementSource CreateSource() => options.FakeScanner
        ? new FakeAdvertisementSource(TimeSpan.FromMilliseconds(200), time)
        : new WinRtAdvertisementSource(settings.ScanMode, loggers.CreateLogger("WinRT"));

    /// <summary>The proxy needs the adapter's address as its identity; with no adapter at all it waits (the scanner keeps retrying).</summary>
    private async Task<ulong> WaitForAdapterAsync(ScannerSupervisor scanner, CancellationToken ct)
    {
        var warned = false;
        while (scanner.AdapterAddress == 0)
        {
            if (!warned)
            {
                _log.LogWarning("No Bluetooth adapter yet (scanner is {Status}); waiting for one before starting the API.", scanner.Status);
                warned = true;
            }
            await Task.Delay(TimeSpan.FromSeconds(1), time, ct).ConfigureAwait(false);
        }
        return scanner.AdapterAddress;
    }

    /// <summary>The API task ended on its own (or never started): turn that into the right fatal error.</summary>
    private static async Task ThrowStartFailureAsync(Task api, int port)
    {
        try
        {
            await api.ConfigureAwait(false);
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            throw new HostFatalException(ExitCodes.PortInUse, $"TCP port {port} is already in use (or not permitted). Stop whatever uses it, or choose another port in settings.json.", e);
        }
        throw new InvalidOperationException("The API server stopped unexpectedly.");
    }

    private async Task SummaryLoopAsync(ScannerSupervisor scanner, ApiServer server, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SummaryInterval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var count = Interlocked.Exchange(ref _advertisements, 0);
                _log.LogInformation("{Count} advertisements in the last minute, {Clients} client(s), status {Status}", count, server.Clients.Count, scanner.Status);
            }
        }
        catch (OperationCanceledException)
        {
            // stopping
        }
    }

    private async Task ShutDownAsync(CancellationTokenSource running, Task? api, Task? summary, ScannerSupervisor? scanner, MdnsPublisher? mdns, Action<RawAdvertisement> onAdvertisement)
    {
        _log.LogInformation("Stopping");
        await running.CancelAsync().ConfigureAwait(false);

        if (api is not null)
        {
            try
            {
                await api.WaitAsync(ApiStopTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("Some API clients did not close within {Seconds:0} s; continuing.", ApiStopTimeout.TotalSeconds);
                // Still running: make sure a late failure is observed rather than surfacing as an unobserved task exception.
                _ = api.ContinueWith(late => _log.LogDebug(late.Exception, "The API server ended late with an error"), CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }
            catch (Exception e)
            {
                _log.LogDebug(e, "The API server ended with an error");
            }
        }

        if (summary is not null)
        {
            await summary.ConfigureAwait(false); // never faults: it only ends on cancellation
        }

        if (scanner is not null)
        {
            scanner.Received -= onAdvertisement;
            try
            {
                await scanner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "Stopping the scanner failed");
            }
        }

        if (mdns is not null)
        {
            await mdns.DisposeAsync().ConfigureAwait(false);
        }
        _log.LogInformation("Stopped");
    }

    /// <summary>
    /// DNS-SD registration that follows the network: re-published (after a short pause, off the event thread) when an address
    /// changes and when a key is provisioned (the TXT records switch from "provisioning" to "encrypted").
    /// </summary>
    private sealed class MdnsPublisher : IAsyncDisposable
    {
        private readonly DeviceIdentity _identity;
        private readonly ushort _port;
        private readonly EncryptionState _encryption;
        private readonly ProxySettings _settings;
        private readonly ILogger _log;
        private readonly DnsSdAdvertiser _advertiser;
        private readonly Debouncer _republish;
        private readonly Action _trigger;
        private readonly NetworkAddressChangedEventHandler _onAddressChanged;

        public MdnsPublisher(DeviceIdentity identity, ushort port, EncryptionState encryption, ProxySettings settings, ILogger log, TimeProvider time)
        {
            _identity = identity;
            _port = port;
            _encryption = encryption;
            _settings = settings;
            _log = log;
            _advertiser = new DnsSdAdvertiser(log);
            _republish = new Debouncer(RepublishDelay, Publish, time, log);
            _trigger = _republish.Trigger;
            _onAddressChanged = (_, _) => _republish.Trigger();
        }

        /// <summary>Registers now (on the caller's thread) and from then on whenever the network or the key changes.</summary>
        public void Start()
        {
            Publish();
            NetworkChange.NetworkAddressChanged += _onAddressChanged;
            _encryption.KeyChanged += _trigger;
        }

        private void Publish()
        {
            try
            {
                var interfaces = InterfaceSelector.Select(NetInterface.FromSystem(), _settings.Interfaces);
                _advertiser.Publish(_identity, _port, _encryption.HasKey, interfaces);
            }
            catch (ObjectDisposedException)
            {
                // stopped while a re-publish was due
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "DNS-SD publish failed");
            }
        }

        public async ValueTask DisposeAsync()
        {
            NetworkChange.NetworkAddressChanged -= _onAddressChanged;
            _encryption.KeyChanged -= _trigger;
            await _republish.DisposeAsync().ConfigureAwait(false);
            try
            {
                // Deregistering waits for Windows to confirm, up to about 3 s.
                await Task.Run(_advertiser.Dispose).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "DNS-SD deregistration failed");
            }
        }
    }
}
