using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Setup;
using WindowsBtProxy.Tests.Api;

namespace WindowsBtProxy.Tests.Ipc;

public sealed class StatusPipeServerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SecurityIdentifier Network = new(WellKnownSidType.NetworkSid, null);
    private static readonly SecurityIdentifier Interactive = new(WellKnownSidType.InteractiveSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly ProxyStatus SampleStatus = new(
        "0.2.0", "bt-proxy-test", "BT Proxy Test", 6053, ScanMode.Passive, ScannerStatus.Running, "02:11:22:33:44:55",
        ApiRunning: true, AdvertisementsLastMinute: 1234, EncryptionMode.Required,
        [new ClientStatus("192.168.1.10", "Home Assistant 2026.10.4", Subscribed: true, Encrypted: true)]);

    private const string SampleKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public async Task Status_round_trip()
    {
        await using var server = Server.Start();

        var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Status, Timeout, Ct);

        Assert.NotNull(response);
        Assert.Equal(PipeProtocol.Version, response.V);
        Assert.Null(response.Error);
        Assert.Null(response.Key);
        Assert.NotNull(response.Status);
        Assert.Equal(SampleStatus with { Clients = [] }, response.Status with { Clients = [] });
        Assert.Equal(SampleStatus.Clients, response.Status.Clients);
    }

    [Fact]
    public async Task Key_is_denied_to_non_admins()
    {
        var keyCalls = 0;
        await using var server = Server.Start(isAdministrator: _ => false, key: () => { Interlocked.Increment(ref keyCalls); return new KeyReply(SampleKey); });

        var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Key, Timeout, Ct);

        Assert.NotNull(response);
        Assert.Equal(PipeProtocol.Version, response.V);
        Assert.Equal(PipeErrors.Denied, response.Error);
        Assert.Null(response.Key);
        Assert.Null(response.Status);
        Assert.Equal(0, Volatile.Read(ref keyCalls));
    }

    [Fact]
    public async Task Key_is_returned_to_admins()
    {
        await using (var server = Server.Start(isAdministrator: _ => true, key: () => new KeyReply(SampleKey)))
        {
            var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Key, Timeout, Ct);

            Assert.NotNull(response);
            Assert.Null(response.Error);
            Assert.Equal(SampleKey, response.Key?.Key);
        }

        await using (var server = Server.Start(isAdministrator: _ => true, key: () => new KeyReply(null)))
        {
            var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Key, Timeout, Ct);

            Assert.NotNull(response);
            Assert.Null(response.Error);
            Assert.NotNull(response.Key); // an answer that says "no key stored", not a missing answer
            Assert.Null(response.Key.Key);
        }
    }

    [Fact]
    public async Task Wrong_version_and_unknown_type_and_garbage()
    {
        await using var server = Server.Start();

        Assert.Equal("{\"v\":1,\"error\":\"version\"}", await RawRequestAsync(server.Name, "{\"v\":2,\"type\":\"status\"}"));
        Assert.Equal("{\"v\":1,\"error\":\"unknown-request\"}", await RawRequestAsync(server.Name, "{\"v\":1,\"type\":\"nope\"}"));
        Assert.Equal("{\"v\":1,\"error\":\"bad-request\"}", await RawRequestAsync(server.Name, "hello"));
        Assert.Equal("{\"v\":1,\"error\":\"bad-request\"}", await RawRequestAsync(server.Name, "{\"v\":1}"));
        Assert.Equal("{\"v\":1,\"error\":\"bad-request\"}", await RawRequestAsync(server.Name, "null"));
    }

    [Fact]
    public async Task Oversized_request_is_rejected_without_allocating()
    {
        await using var server = Server.Start();
        await using var raw = await ConnectRawAsync(server.Name);

        try
        {
            await raw.WriteAsync(Enumerable.Repeat((byte)'a', 1 << 20).ToArray(), Ct).AsTask().WaitAsync(Timeout, Ct);
        }
        catch (IOException)
        {
            // The server hung up part-way through: that is the point.
        }
        Assert.True(await IsClosedByServerAsync(raw, Timeout));

        var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Status, Timeout, Ct);
        Assert.NotNull(response?.Status);
    }

    [Fact]
    public async Task Silent_client_is_dropped_and_others_are_served()
    {
        await using var server = Server.Start();
        await using var silent = await ConnectRawAsync(server.Name);
        var connected = Stopwatch.StartNew();
        var closed = IsClosedByServerAsync(silent, TimeSpan.FromSeconds(10));

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Status, Timeout, Ct)));

        Assert.All(responses, r => Assert.NotNull(r?.Status));
        Assert.True(await closed);
        Assert.True(connected.Elapsed < TimeSpan.FromSeconds(2.5), $"closed after {connected.Elapsed}");
    }

    [Fact]
    public async Task Second_server_on_same_name_reports_in_use()
    {
        await using var first = Server.Start();
        Assert.NotNull(await StatusPipeClient.RequestAsync(first.Name, PipeRequestTypes.Status, Timeout, Ct));
        var log = new ListLogger();

        var second = await new StatusPipeServer(first.Name, () => SampleStatus, () => new KeyReply(null), log)
            .RunAsync(Ct).WaitAsync(TimeSpan.FromSeconds(1), Ct);

        Assert.False(second);
        Assert.Contains(log.Lines, l => l.StartsWith("Warning:", StringComparison.Ordinal) && l.Contains(first.Name, StringComparison.Ordinal));
        var response = await StatusPipeClient.RequestAsync(first.Name, PipeRequestTypes.Status, Timeout, Ct);
        Assert.NotNull(response?.Status);
    }

    [Fact]
    public void Acl_denies_network_and_allows_interactive_only()
    {
        AssertExpectedAcl(PipeAcl.Create());
    }

    [Fact]
    public async Task Running_server_applies_the_acl()
    {
        await using var server = Server.Start();
        await using var raw = await ConnectRawAsync(server.Name);

        AssertExpectedAcl(raw.GetAccessControl());
    }

    [Fact]
    public async Task Interactive_rights_are_enough_for_a_client()
    {
        // A pipe whose only grant is the INTERACTIVE ACE's rights, given to this user: if a client can connect InOut,
        // read its mode and talk, so can any interactive user of the real pipe.
        var name = UniqueName();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAcl.InteractiveRights, AccessControlType.Allow));
        await using var server = NamedPipeServerStreamAcl.Create(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        var accepted = server.WaitForConnectionAsync(Ct);

        await using var client = await ConnectRawAsync(name);
        await accepted.WaitAsync(Timeout, Ct);

        Assert.Equal(PipeTransmissionMode.Byte, client.ReadMode);
        // Unbuffered pipes: a write completes only once the other end reads, so each read starts before its write.
        var ping = PipeProtocol.ReadLineAsync(server, Ct);
        await PipeProtocol.WriteLineAsync(client, "ping", Ct).WaitAsync(Timeout, Ct);
        Assert.Equal("ping", await ping.WaitAsync(Timeout, Ct));
        var pong = PipeProtocol.ReadLineAsync(client, Ct);
        await PipeProtocol.WriteLineAsync(server, "pong", Ct).WaitAsync(Timeout, Ct);
        Assert.Equal("pong", await pong.WaitAsync(Timeout, Ct));
    }

    [Fact]
    public async Task Clients_connect_at_identification_level()
    {
        TokenImpersonationLevel? level = null;
        await using var server = Server.Start(isAdministrator: pipe =>
        {
            pipe.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
                level = identity.ImpersonationLevel;
            });
            return false;
        });

        var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Key, Timeout, Ct);

        Assert.Equal(PipeErrors.Denied, response?.Error);
        Assert.Equal(TokenImpersonationLevel.Identification, level);
    }

    [Fact]
    public async Task Real_admin_check_matches_this_process()
    {
        // Not elevated on a developer PC, elevated on CI runners: the answer must follow whichever this is.
        var elevated = new WindowsElevation().IsElevated;
        await using var server = Server.Start(isAdministrator: null);

        var response = await StatusPipeClient.RequestAsync(server.Name, PipeRequestTypes.Key, Timeout, Ct);

        Assert.NotNull(response);
        if (elevated)
        {
            Assert.Null(response.Error);
            Assert.Equal(SampleKey, response.Key?.Key);
        }
        else
        {
            Assert.Equal(PipeErrors.Denied, response.Error);
            Assert.Null(response.Key);
        }
        // Denied because the token says so, not because reading it failed (which also denies).
        Assert.DoesNotContain(server.Log.Lines, l => l.Contains("token", StringComparison.Ordinal) && l.StartsWith("Debug:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Request_returns_null_when_nobody_listens()
    {
        var watch = Stopwatch.StartNew();

        var response = await StatusPipeClient.RequestAsync(UniqueName(), PipeRequestTypes.Status, TimeSpan.FromMilliseconds(300), Ct);

        Assert.Null(response);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    private static void AssertExpectedAcl(PipeSecurity security)
    {
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();

        var deny = Assert.Single(rules, r => r.AccessControlType == AccessControlType.Deny);
        Assert.Equal(Network, deny.IdentityReference);
        Assert.Equal(PipeAccessRights.FullControl, deny.PipeAccessRights);

        // PipeAccessRule adds Synchronize to every allow rule; compare without it.
        var allows = rules.Where(r => r.AccessControlType == AccessControlType.Allow)
            .GroupBy(r => (SecurityIdentifier)r.IdentityReference)
            .ToDictionary(g => g.Key, g => g.Aggregate((PipeAccessRights)0, (a, r) => a | r.PipeAccessRights) & ~PipeAccessRights.Synchronize);
        var fullControl = PipeAccessRights.FullControl & ~PipeAccessRights.Synchronize;
        var expected = new Dictionary<SecurityIdentifier, PipeAccessRights>
        {
            [LocalSystem] = fullControl,
            [Administrators] = fullControl,
            [WindowsIdentity.GetCurrent().User!] = fullControl,
            [Interactive] = PipeAccessRights.ReadWrite,
        };
        Assert.Equal(expected.OrderBy(p => p.Key.Value), allows.OrderBy(p => p.Key.Value));

        // Ruling 5: an interactive user must not be able to add a server instance of this pipe and answer other clients.
        Assert.Equal((PipeAccessRights)0, allows[Interactive] & PipeAccessRights.CreateNewInstance);
        Assert.Equal(PipeAccessRights.ReadWrite, PipeAcl.InteractiveRights);

        foreach (var wide in new[] { WellKnownSidType.WorldSid, WellKnownSidType.BuiltinUsersSid, WellKnownSidType.AuthenticatedUserSid })
        {
            Assert.DoesNotContain(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(wide, null)));
        }
    }

    private static string UniqueName() => $"wbp-test-{Guid.NewGuid():N}";

    private static async Task<NamedPipeClientStream> ConnectRawAsync(string name)
    {
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        await client.ConnectAsync((int)Timeout.TotalMilliseconds, Ct);
        return client;
    }

    private static async Task<string?> RawRequestAsync(string name, string line)
    {
        await using var client = await ConnectRawAsync(name);
        await client.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), Ct);
        return await PipeProtocol.ReadLineAsync(client, Ct).WaitAsync(Timeout, Ct);
    }

    /// <summary>True once the server end is gone (end of stream or a broken pipe); false if it is still open after <paramref name="within"/>.</summary>
    private static async Task<bool> IsClosedByServerAsync(NamedPipeClientStream client, TimeSpan within)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(within);
        var buffer = new byte[256];
        try
        {
            while (await client.ReadAsync(buffer, timeout.Token) > 0)
            {
            }
            return true;
        }
        catch (IOException)
        {
            return true;
        }
        catch (OperationCanceledException) when (!Ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed class Server : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<bool> _run;

        private Server(string name, Func<NamedPipeServerStream, bool>? isAdministrator, Func<KeyReply> key)
        {
            Name = name;
            _run = new StatusPipeServer(name, () => SampleStatus, key, Log, isAdministrator).RunAsync(_stop.Token);
        }

        public string Name { get; }

        public ListLogger Log { get; } = new();

        /// <param name="isAdministrator">The admin check; null means the real one (<see cref="ClientToken"/>).</param>
        public static Server Start(Func<NamedPipeServerStream, bool>? isAdministrator = null, Func<KeyReply>? key = null) =>
            new(UniqueName(), isAdministrator, key ?? (() => new KeyReply(SampleKey)));

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            Assert.True(await _run.WaitAsync(Timeout)); // RunAsync ends cleanly, and only after its handlers
            _stop.Dispose();
        }
    }
}
