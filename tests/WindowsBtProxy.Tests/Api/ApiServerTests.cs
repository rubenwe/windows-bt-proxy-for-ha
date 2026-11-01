using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using WindowsBtProxy.Api;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Ble;
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
        Assert.StartsWith("windows-bt-proxy ", hello.ServerInfo);
        Assert.True(hello.ServerInfo.Length > "windows-bt-proxy ".Length);
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
        Assert.Equal("2025.6.0", info.EsphomeVersion);
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
        await using var h = await Harness.StartAsync(encryption: EncryptionState.InMemory(new byte[32]));
        await using var c = h.Connect();

        await c.SendAsync(new DeviceInfoRequest());
        var info = Assert.IsType<DeviceInfoResponse>(await c.ReceiveAsync());

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

    /// <summary>A running server on an ephemeral loopback port plus a fake source.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly FakeAdvertisementSource _source;
        private Task _run = Task.CompletedTask;

        private Harness(ApiServer server, FakeAdvertisementSource source)
        {
            Server = server;
            _source = source;
        }

        public ApiServer Server { get; }

        public static async Task<Harness> StartAsync(bool startSource = false, EncryptionState? encryption = null)
        {
            var source = new FakeAdvertisementSource(TimeSpan.FromMilliseconds(20));
            var identity = new DeviceIdentity("windows-bt-proxy-334455", "Windows Bluetooth Proxy", 0x021122334455, "TESTPC", "Nov  1 2026, 12:00:00");
            var server = new ApiServer(new IPEndPoint(IPAddress.Loopback, 0), identity, source, encryption ?? EncryptionState.InMemory(), NullLogger<ApiServer>.Instance, TimeProvider.System);
            var h = new Harness(server, source);
            h._run = server.RunAsync(h._cts.Token); // binds the listener before its first await, so LocalEndpoint is valid right away
            if (startSource)
            {
                await source.StartAsync(CancellationToken.None);
            }
            return h;
        }

        public TestClient Connect() => new(Server.LocalEndpoint);

        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await _cts.CancelAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(5)); // RunAsync must end cleanly on cancellation
            _cts.Dispose();
        }
    }

    /// <summary>Minimal ESPHome client: plaintext codec over a TCP socket, every wait bounded.</summary>
    private sealed class TestClient : IAsyncDisposable
    {
        private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
        private readonly TcpClient _tcp = new();
        private readonly NetworkStream _stream;
        private readonly PlaintextFrameCodec _codec;

        public TestClient(IPEndPoint server)
        {
            _tcp.Connect(server);
            _stream = _tcp.GetStream();
            _codec = new PlaintextFrameCodec(_stream);
        }

        public async Task<HelloResponse> HelloAsync()
        {
            await SendAsync(new HelloRequest { ClientInfo = "test", ApiVersionMajor = 1, ApiVersionMinor = 10 });
            return Assert.IsType<HelloResponse>(await ReceiveAsync());
        }

        public async Task SendAsync(IMessage message)
        {
            using var cts = new CancellationTokenSource(Limit);
            await _codec.WriteAsync(MessageRegistry.IdOf(message), message, cts.Token);
        }

        public async Task SendRawAsync(int messageId, byte[] payload)
        {
            var frame = new List<byte> { 0x00 };
            frame.AddRange(Encode((uint)payload.Length));
            frame.AddRange(Encode((uint)messageId));
            frame.AddRange(payload);
            await SendBytesAsync(frame.ToArray());
        }

        public async Task SendBytesAsync(byte[] bytes)
        {
            using var cts = new CancellationTokenSource(Limit);
            await _stream.WriteAsync(bytes, cts.Token);
            await _stream.FlushAsync(cts.Token);
        }

        public async Task<IMessage> ReceiveAsync()
        {
            var message = await ReceiveOrEofAsync();
            return message ?? throw new EndOfStreamException("Server closed the connection.");
        }

        /// <summary>The next message, or null if the server closed the connection (cleanly or by reset).</summary>
        public async Task<IMessage?> ReceiveOrEofAsync()
        {
            using var cts = new CancellationTokenSource(Limit);
            try
            {
                var frame = await _codec.ReadAsync(cts.Token);
                return frame is { } f ? MessageRegistry.Parse(f.MessageId, f.Payload.Span) : null;
            }
            catch (Exception e) when (e is IOException or EndOfStreamException)
            {
                return null;
            }
        }

        public async Task<T> ReceiveUntilAsync<T>() where T : IMessage
        {
            while (true)
            {
                if (await ReceiveAsync() is T match) return match;
            }
        }

        /// <summary>Reads advertisement batches until at least <paramref name="minAds"/> ads arrived in total.</summary>
        public async Task<List<BluetoothLERawAdvertisementsResponse>> ReceiveBatchesAsync(int minAds)
        {
            var batches = new List<BluetoothLERawAdvertisementsResponse>();
            var total = 0;
            while (total < minAds)
            {
                var batch = Assert.IsType<BluetoothLERawAdvertisementsResponse>(await ReceiveAsync());
                batches.Add(batch);
                total += batch.Advertisements.Count;
            }
            return batches;
        }

        public async Task WaitUntilClosedAsync()
        {
            while (await ReceiveOrEofAsync() is not null)
            {
            }
        }

        /// <summary>Closes with a TCP reset instead of a FIN.</summary>
        public void Abort()
        {
            _tcp.Client.LingerState = new LingerOption(true, 0);
            _tcp.Dispose();
        }

        public static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
        {
            var deadline = DateTime.UtcNow + (timeout ?? Limit);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
                await Task.Delay(10);
            }
        }

        public ValueTask DisposeAsync()
        {
            _tcp.Dispose();
            return ValueTask.CompletedTask;
        }

        private static byte[] Encode(uint value)
        {
            var buffer = new byte[Varint.MaxLength];
            var n = Varint.Write(buffer, value);
            return buffer[..n];
        }
    }
}
