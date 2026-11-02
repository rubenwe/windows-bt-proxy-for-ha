using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using WindowsBtProxy.Api;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Api;

public class ApiServerTests
{
    [Fact]
    public async Task Hello_returns_api_1_13_and_name()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();

        var hello = await c.HelloAsync();

        Assert.Equal(1u, hello.ApiVersionMajor);
        Assert.Equal(13u, hello.ApiVersionMinor);
        Assert.Equal("windows-bt-proxy-334455", hello.Name);
        var version = typeof(ApiServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal($"windows-bt-proxy {version}", hello.ServerInfo);
    }

    [Fact]
    public async Task DeviceInfo_reports_flags_33_mac_formats_and_encryption_fields()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();

        await c.SendAsync(new DeviceInfoRequest());
        var info = Assert.IsType<DeviceInfoResponse>(await c.ReceiveAsync());

        Assert.Equal("windows-bt-proxy-334455", info.Name);
        Assert.Equal("Windows Bluetooth Proxy", info.FriendlyName);
        Assert.Equal("02:11:22:33:44:55", info.MacAddress);
        Assert.Equal("02:11:22:33:44:55", info.BluetoothMacAddress);
        Assert.Equal("2026.10.0", info.EsphomeVersion);
        Assert.Equal("Windows", info.Manufacturer);
        Assert.Equal("TESTPC", info.Model);
        Assert.Equal("Nov  1 2026, 12:00:00", info.CompilationTime);
        Assert.Equal(33u, info.BluetoothProxyFeatureFlags);
        Assert.True(info.ApiEncryptionSupported);
        Assert.True(info.ApiEncryptionProvisionable);
    }

    [Fact]
    public async Task DeviceInfo_is_not_provisionable_once_a_key_exists()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var h = await Harness.StartAsync(encryption: EncryptionState.InMemory(key));
        await using var c = await h.ConnectNoiseAsync(key); // a keyed device serves Noise only

        var info = await c.DeviceInfoAsync();

        Assert.True(info.ApiEncryptionSupported);
        Assert.False(info.ApiEncryptionProvisionable);
    }

    [Fact]
    public async Task ListEntities_returns_only_done()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();

        await c.SendAsync(new ListEntitiesRequest());
        Assert.IsType<ListEntitiesDoneResponse>(await c.ReceiveAsync());

        // Nothing else follows: the next frame is the answer to a ping.
        await c.SendAsync(new PingRequest());
        Assert.IsType<PingResponse>(await c.ReceiveAsync());
    }

    [Fact]
    public async Task No_advertisements_before_subscribe()
    {
        await using var h = await Harness.StartAsync(startSource: true);
        await using var c = h.Connect();
        await c.HelloAsync();

        await Task.Delay(300, TestContext.Current.CancellationToken); // several batcher intervals of source ticks; nothing may arrive because we have not subscribed
        await c.SendAsync(new PingRequest());

        // Frames arrive in order, so any advertisement sent before the ping reply would show up here instead.
        Assert.IsType<PingResponse>(await c.ReceiveAsync());
    }

    [Fact]
    public async Task Subscribed_client_receives_fake_advertisements_in_batches()
    {
        await using var h = await Harness.StartAsync(startSource: true);
        await using var c = h.Connect();
        await c.HelloAsync();
        await c.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest { Flags = 1 });

        var batches = await c.ReceiveBatchesAsync(minAds: 12);

        Assert.All(batches, b => Assert.InRange(b.Advertisements.Count, 1, 16));
        Assert.Contains(batches, b => b.Advertisements.Count > 1);
        var ads = batches.SelectMany(b => b.Advertisements).ToList();
        Assert.Contains(ads, a => a.Address == 0xA4C138000001 && a.AddressType == 0);
        Assert.Contains(ads, a => a.Address == 0xC0FFEE000002 && a.AddressType == 1);
        Assert.Contains(ads, a => a.Address == 0xA4C138000003 && a.AddressType == 0);
        Assert.All(ads, a => Assert.Equal(-60, a.Rssi));
    }

    [Fact]
    public async Task Unsubscribe_stops_the_stream()
    {
        await using var h = await Harness.StartAsync(startSource: true);
        await using var c = h.Connect();
        await c.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());
        await c.ReceiveBatchesAsync(minAds: 3);

        await c.SendAsync(new UnsubscribeBluetoothLEAdvertisementsRequest());
        await TestClient.WaitUntilAsync(() => h.Server.Clients is [{ Subscribed: false }]);

        // Drain what was already in flight, let several source ticks pass, then a ping must be answered with nothing ahead of it.
        await c.SendAsync(new PingRequest());
        await c.ReceiveUntilAsync<PingResponse>();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        await c.SendAsync(new PingRequest());
        Assert.IsType<PingResponse>(await c.ReceiveAsync());
    }

    [Fact]
    public async Task Two_clients_each_receive_advertisements()
    {
        await using var h = await Harness.StartAsync(startSource: true);
        await using var a = h.Connect();
        await using var b = h.Connect();
        await a.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());
        await b.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());

        var ta = a.ReceiveBatchesAsync(minAds: 6);
        var tb = b.ReceiveBatchesAsync(minAds: 6);

        Assert.True((await ta).Sum(x => x.Advertisements.Count) >= 6);
        Assert.True((await tb).Sum(x => x.Advertisements.Count) >= 6);
        Assert.Equal(2, h.Server.Clients.Count(x => x.Subscribed));
    }

    [Fact]
    public async Task Ping_is_answered()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();

        await c.SendAsync(new PingRequest());

        Assert.IsType<PingResponse>(await c.ReceiveAsync());
    }

    [Fact]
    public async Task Authentication_and_unknown_messages_are_ignored()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();

#pragma warning disable CS0612
        await c.SendAsync(new AuthenticationRequest { Password = "secret" });
#pragma warning restore CS0612
        await c.SendRawAsync(messageId: 4242, payload: [1, 2, 3]);
        await c.SendAsync(new PingRequest());

        Assert.IsType<PingResponse>(await c.ReceiveAsync());
    }

    [Fact]
    public async Task Malformed_payload_closes_only_that_connection()
    {
        await using var h = await Harness.StartAsync();
        await using var bad = h.Connect();
        await using var good = h.Connect();
        await good.HelloAsync(); // both are accepted before the bad frame goes out

        await bad.SendRawAsync(MessageIds.HelloRequest, [0x0A, 0xFF]); // field 1, length 255, truncated

        Assert.Null(await bad.ReceiveOrEofAsync());
        await good.SendAsync(new PingRequest());
        Assert.IsType<PingResponse>(await good.ReceiveAsync());
    }

    [Fact]
    public async Task Disconnect_request_gets_response_and_close()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();
        await c.HelloAsync();

        await c.SendAsync(new DisconnectRequest());

        Assert.IsType<DisconnectResponse>(await c.ReceiveAsync());
        Assert.Null(await c.ReceiveOrEofAsync());
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0);
    }

    [Fact]
    public async Task Abrupt_disconnect_releases_subscription()
    {
        await using var h = await Harness.StartAsync(startSource: true);
        var a = h.Connect();
        await a.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());
        await a.ReceiveBatchesAsync(minAds: 3);
        Assert.Single(h.Server.Clients);

        a.Abort(); // RST, no Disconnect message

        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0, TimeSpan.FromSeconds(1));

        // The source keeps feeding new subscribers.
        await using var b = h.Connect();
        await b.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());
        Assert.NotEmpty(await b.ReceiveBatchesAsync(minAds: 3));
    }

    [Fact]
    public async Task Garbage_client_does_not_affect_others()
    {
        await using var h = await Harness.StartAsync(startSource: true);
        await using var good = h.Connect();
        await good.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());

        await using var garbage = h.Connect();
        await garbage.SendBytesAsync(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n"));
        await garbage.WaitUntilClosedAsync();

        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 1);
        Assert.NotEmpty(await good.ReceiveBatchesAsync(minAds: 3));
        await good.SendAsync(new PingRequest());
        await good.ReceiveUntilAsync<PingResponse>();
    }

    [Fact]
    public async Task At_most_eight_connections_are_served_including_ones_that_never_authenticate()
    {
        await using var h = await Harness.StartAsync();
        var idle = new List<TestClient>();
        try
        {
            for (var i = 0; i < ApiServer.MaxConnections; i++)
            {
                idle.Add(h.Connect()); // connected, never sends a byte: still negotiating
            }
            await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == ApiServer.MaxConnections);

            await using var excess = h.Connect();
            await excess.WaitUntilClosedAsync(); // closed straight after accept
            Assert.Equal(ApiServer.MaxConnections, h.Server.Clients.Count);
            Assert.Contains(h.Log.Lines, l => l.StartsWith("Debug: Refusing ", StringComparison.Ordinal));
            Assert.DoesNotContain(h.Log.Lines, l => l.Contains("Refusing", StringComparison.Ordinal) && !l.StartsWith("Debug:", StringComparison.Ordinal));

            // A freed slot is usable again.
            await idle[0].DisposeAsync();
            await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == ApiServer.MaxConnections - 1);
            await using var replacement = h.Connect();
            Assert.Equal(1u, (await replacement.HelloAsync()).ApiVersionMajor);
        }
        finally
        {
            foreach (var c in idle)
            {
                await c.DisposeAsync();
            }
        }
    }

    [Fact]
    public async Task Connecting_and_disconnecting_is_logged_at_debug_and_the_hello_at_info()
    {
        await using var h = await Harness.StartAsync();
        await using (var c = h.Connect())
        {
            await c.HelloAsync();
        }
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0);
        await TestClient.WaitUntilAsync(() => h.Log.Lines.Any(l => l.Contains("disconnected", StringComparison.Ordinal)));

        Assert.Contains(h.Log.Lines, l => l.StartsWith("Debug: Client ", StringComparison.Ordinal) && l.EndsWith(" connected", StringComparison.Ordinal));
        Assert.Contains(h.Log.Lines, l => l.StartsWith("Debug: Client ", StringComparison.Ordinal) && l.EndsWith(" disconnected", StringComparison.Ordinal));
        Assert.Contains(h.Log.Lines, l => l.StartsWith("Information: Client ", StringComparison.Ordinal) && l.Contains(" says hello: ", StringComparison.Ordinal));
        Assert.Single(h.Log.Lines, l => l.StartsWith("Information: Client ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ClientsChanged_fires_and_client_info_carries_hello_text()
    {
        await using var h = await Harness.StartAsync();
        var changes = 0;
        h.Server.ClientsChanged += () => Interlocked.Increment(ref changes);

        await using var c = h.Connect();
        await c.SendAsync(new HelloRequest { ClientInfo = "Home Assistant 2025.6", ApiVersionMajor = 1, ApiVersionMinor = 10 });
        await c.ReceiveAsync();

        await TestClient.WaitUntilAsync(() => h.Server.Clients is [{ ClientInfoText: "Home Assistant 2025.6", Encrypted: false, Subscribed: false }]);
        Assert.True(Volatile.Read(ref changes) >= 2); // connected, then hello text known
        Assert.Equal(IPAddress.Loopback, h.Server.Clients[0].Remote.Address);
    }
}
