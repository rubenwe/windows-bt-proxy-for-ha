using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Tests.Api;
using WindowsBtProxy.Crypto;
using WindowsBtProxy.Hosting;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Hosting;

public sealed class ProxyHostTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("host-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Exit_codes_have_the_documented_values()
    {
        Assert.Equal(0, ExitCodes.Ok);
        Assert.Equal(2, ExitCodes.CorruptKey);
        Assert.Equal(3, ExitCodes.PortInUse);
        Assert.Equal(4, ExitCodes.CryptoUnsupported);
    }

    [Fact]
    public void Exceptions_map_to_exit_codes()
    {
        Assert.Equal(2, ProxyHost.ExitCodeFor(new KeyStoreCorruptException("bad")));
        Assert.Equal(3, ProxyHost.ExitCodeFor(new HostFatalException(ExitCodes.PortInUse, "busy")));
        Assert.Equal(4, ProxyHost.ExitCodeFor(new HostFatalException(ExitCodes.CryptoUnsupported, "no crypto")));
        Assert.Equal(1, ProxyHost.ExitCodeFor(new SocketException()));
        Assert.Equal(1, ProxyHost.ExitCodeFor(new InvalidOperationException()));
    }

    [Fact]
    public void Crypto_check_passes_only_when_both_primitives_are_supported()
    {
        Assert.Null(ProxyHost.CryptoProblem(true, true));
        Assert.Contains("ChaCha20-Poly1305", ProxyHost.CryptoProblem(false, true));
        Assert.DoesNotContain("X25519", ProxyHost.CryptoProblem(false, true));
        Assert.Contains("X25519", ProxyHost.CryptoProblem(true, false));
        var both = ProxyHost.CryptoProblem(false, false);
        Assert.Contains("ChaCha20-Poly1305", both);
        Assert.Contains("X25519", both);
    }

    [Fact]
    public async Task A_key_that_loads_is_loaded_once()
    {
        var calls = 0;
        var state = await ProxyHost.LoadEncryptionAsync(() => { calls++; return EncryptionState.InMemory(); }, TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.False(state.HasKey);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task A_sharing_violation_is_retried_once()
    {
        var calls = 0;
        var state = await ProxyHost.LoadEncryptionAsync(() =>
        {
            if (++calls == 1)
            {
                throw new KeyStoreCorruptException("locked", new IOException("The process cannot access the file"));
            }
            return EncryptionState.InMemory();
        }, TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);

        Assert.NotNull(state);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_second_failure_is_fatal()
    {
        var calls = 0;
        await Assert.ThrowsAsync<KeyStoreCorruptException>(() => ProxyHost.LoadEncryptionAsync(
            () => { calls++; throw new KeyStoreCorruptException("locked", new IOException()); },
            TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_corrupt_key_is_not_retried()
    {
        var calls = 0;
        await Assert.ThrowsAsync<KeyStoreCorruptException>(() => ProxyHost.LoadEncryptionAsync(
            () => { calls++; throw new KeyStoreCorruptException("not base64"); },
            TimeSpan.Zero, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken));

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Compilation_time_looks_like_ESPHomes()
    {
        Assert.Matches(@"^[A-Z][a-z]{2} \d{2} \d{4}, \d{2}:\d{2}:\d{2}$", ProxyHost.CompilationTime());
    }

    [Theory]
    [InlineData(ExitCodes.Ok, 0)]
    [InlineData(ExitCodes.Failed, 1)]
    [InlineData(ExitCodes.CorruptKey, 0)]
    [InlineData(ExitCodes.PortInUse, 3)]
    [InlineData(ExitCodes.CryptoUnsupported, 0)]
    public void Only_transient_failures_are_reported_to_the_service_control_manager(int processExitCode, int scmExitCode)
    {
        // The last failure action repeats forever, so a permanent failure (2, 4) must not look like a failure to the SCM.
        Assert.Equal(scmExitCode, ProxyRuntime.ScmExitCodeFor(processExitCode));
    }

    /// <summary>A host for tests: fake scanner, no mDNS, a temp data dir and its own pipe (the installed service owns the default one).</summary>
    private ProxyHostOptions Options() => new(new DataPaths(Path.Combine(_dir, "data")), AsService: false, FakeScanner: true, NoMdns: true, PortOverride: 0, ProtectKeyFile: false, PipeName: UniquePipeName());

    private static string UniquePipeName() => $"wbp-test-{Guid.NewGuid():N}";

    [Fact]
    public async Task The_data_dir_is_protected_and_verified_before_anything_is_read_or_written()
    {
        var options = Options();
        var events = new List<string>();
        var protector = new DataDirProtector(
            root => events.Add($"apply (dir exists: {Directory.Exists(root)}, settings exist: {File.Exists(options.Paths.SettingsFile)})"),
            root => events.Add($"verify (settings exist: {File.Exists(options.Paths.SettingsFile)})"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        var run = ProxyHost.RunAsync(options, cts.Token, protector);
        await TestClient.WaitUntilAsync(() => File.Exists(options.Paths.SettingsFile), TimeSpan.FromSeconds(15));
        await cts.CancelAsync();
        var exitCode = await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(exitCode == 0, $"exit code {exitCode}; the host's log:\n{Log(options)}");
        Assert.Equal(["apply (dir exists: False, settings exist: False)", "verify (settings exist: False)"], events);
    }

    [Fact]
    public async Task A_data_dir_that_cannot_be_verified_stops_the_service_with_exit_code_2_before_the_key_or_settings_are_read()
    {
        var options = Options();
        Directory.CreateDirectory(options.Paths.Root);
        File.WriteAllText(options.Paths.KeyFile, "planted, not a key"); // would be exit 2 as well if it were loaded, so check what was not touched
        var applied = 0;
        var protector = new DataDirProtector(_ => applied++, _ => throw new SetupException($"{options.Paths.Root} is not owned by SYSTEM or Administrators; delete it and run install again."));

        var exitCode = await ProxyHost.RunAsync(options, TestContext.Current.CancellationToken, protector).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(ExitCodes.CorruptKey, exitCode);
        Assert.Equal(1, applied);
        Assert.False(File.Exists(options.Paths.SettingsFile), "settings.json must not be read or created");
        Assert.False(Directory.Exists(options.Paths.LogDir), "nothing may be logged into an untrusted folder");
        Assert.Equal("planted, not a key", File.ReadAllText(options.Paths.KeyFile));
    }

    [Fact]
    public void A_failing_apply_is_a_fatal_error_too()
    {
        var failure = new DataDirProtector(_ => throw new UnauthorizedAccessException("Access to the path is denied."), _ => Assert.Fail("verify must not run after a failed apply")).TryProtect(@"C:\data");

        Assert.NotNull(failure);
        Assert.Equal(ExitCodes.CorruptKey, failure.ExitCode);
        Assert.Contains(@"C:\data", failure.Message);
        Assert.Contains("The API is not started", failure.Message);
    }

    [Fact]
    public void A_trusted_data_dir_is_not_a_failure()
    {
        Assert.Null(new DataDirProtector(_ => { }, _ => { }).TryProtect(@"C:\data"));
    }

    [Fact]
    public async Task Status_pipe_serves_the_running_proxy()
    {
        var options = Options();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = ProxyHost.RunAsync(options, cts.Token, protector: null);

        ProxyStatus? status = null;
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (status is not { Scanner: ScannerStatus.Running, ApiRunning: true })
        {
            Assert.True(DateTime.UtcNow < deadline, $"the proxy never reported running over the pipe (last: {status})");
            Assert.False(run.IsCompleted, "the host stopped");
            status = (await StatusPipeClient.RequestAsync(options.PipeName, PipeRequestTypes.Status, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken))?.Status;
            if (status is not { Scanner: ScannerStatus.Running, ApiRunning: true })
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }

        var settings = SettingsStore.LoadOrCreate(options.Paths, Environment.MachineName, NullLogger.Instance);
        Assert.Equal(settings.Name, status.Name);
        Assert.Equal(settings.FriendlyName, status.FriendlyName);
        Assert.Equal(EncryptionMode.Provisionable, status.Encryption);
        Assert.Empty(status.Clients);
        Assert.NotNull(status.AdapterMac);
        Assert.Null(status.AdvertisementsLastMinute); // the first count comes after a minute
        Assert.Equal(ProxyHost.Version, status.Version);

        await cts.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        // Shutdown closed the pipe: nobody answers on the name any more.
        Assert.Null(await StatusPipeClient.RequestAsync(options.PipeName, PipeRequestTypes.Status, TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Pipe_name_in_use_is_not_fatal()
    {
        var name = UniquePipeName();
        using var holderStop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var holderStatus = new ProxyStatus("other", "other", "Other", 1, ScanMode.Passive, ScannerStatus.Running, null, true, null, EncryptionMode.Required, []);
        var holder = new StatusPipeServer(name, () => holderStatus, () => new KeyReply(null), NullLogger.Instance).RunAsync(holderStop.Token);
        Assert.NotNull(await StatusPipeClient.RequestAsync(name, PipeRequestTypes.Status, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        var port = FreePort();
        var options = Options() with { PipeName = name, PortOverride = port };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var run = ProxyHost.RunAsync(options, cts.Token, protector: null);

        await TestClient.WaitUntilAsync(() => run.IsCompleted || Accepts(port), TimeSpan.FromSeconds(30));
        Assert.False(run.IsCompleted, "the host stopped");
        await TestClient.WaitUntilAsync(() => Log(options).Contains("Proxy is running", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
        Assert.Contains($"WARN WindowsBtProxy: The status pipe {name} is already in use (another copy of the proxy running?); the tray and the status command will not see this one.", Log(options), StringComparison.Ordinal);
        // The first server still owns the name and answers with its own status.
        Assert.Equal("other", (await StatusPipeClient.RequestAsync(name, PipeRequestTypes.Status, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken))?.Status?.Name);

        await cts.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await holderStop.CancelAsync();
        Assert.True(await holder.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static bool Accepts(int port)
    {
        try
        {
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>The host's log so far (the file logger writes each line through, so this is current).</summary>
    private static string Log(ProxyHostOptions options)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(options.Paths.LogDir, "proxy.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}
