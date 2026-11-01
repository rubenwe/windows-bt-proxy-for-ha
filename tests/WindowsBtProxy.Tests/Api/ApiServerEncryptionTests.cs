using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Api;

public sealed class ApiServerEncryptionTests : IDisposable
{
    private static readonly byte[] ZeroPsk = new byte[32];
    private readonly string _dir = Directory.CreateTempSubdirectory("api-encryption-tests-").FullName;

    private KeyStore Store => new(Path.Combine(_dir, "noise.key"), applyAcl: false);

    [Fact]
    public async Task Unprovisioned_accepts_plaintext_and_zero_psk()
    {
        await using var h = await Harness.StartAsync(encryption: EncryptionState.Load(Store));

        await using var plain = h.Connect();
        Assert.Equal("windows-bt-proxy-334455", (await plain.HelloAsync()).Name);

        await using var noise = await h.ConnectNoiseAsync(ZeroPsk);
        Assert.Equal(("windows-bt-proxy-334455", "021122334455"), noise.ServerHello);
        Assert.Equal("windows-bt-proxy-334455", (await noise.HelloAsync()).Name);
        Assert.IsType<PingResponse>(await Ping(noise));

        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 2);
        Assert.Equal([false, true], h.Server.Clients.Select(c => c.Encrypted).Order());
    }

    [Fact]
    public async Task Set_key_over_zero_psk_then_plaintext_is_refused()
    {
        var state = EncryptionState.Load(Store);
        await using var h = await Harness.StartAsync(encryption: state);
        var key = RandomNumberGenerator.GetBytes(32);

        await using (var adopter = await h.ConnectNoiseAsync(ZeroPsk))
        {
            await adopter.HelloAsync();
            Assert.True(await adopter.SetKeyAsync(Convert.ToBase64String(key)));
            Assert.Null(await adopter.ReceiveOrEofAsync()); // the zero-PSK session is closed after the answer
        }

        Assert.Equal(key, Store.Load());
        Assert.Equal(key, state.Key.ToArray());

        // aioesphomeapi's plaintext helper turns a 0x01 first byte into RequiresEncryptionAPIError.
        await using var plain = h.Connect();
        await plain.SendAsync(new HelloRequest { ClientInfo = "Home Assistant", ApiVersionMajor = 1, ApiVersionMinor = 10 });
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00 }, await plain.ReadToEndAsync());
    }

    [Fact]
    public async Task After_provisioning_new_key_session_receives_advertisements()
    {
        await using var h = await Harness.StartAsync(startSource: true, encryption: EncryptionState.Load(Store));
        var key = await ProvisionAsync(h);

        await using var c = await h.ConnectNoiseAsync(key);
        await c.HelloAsync();
        await c.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest { Flags = 1 });

        var ads = (await c.ReceiveBatchesAsync(minAds: 6)).SelectMany(b => b.Advertisements).ToList();
        Assert.Contains(ads, a => a.Address == 0xA4C138000001);
        await TestClient.WaitUntilAsync(() => h.Server.Clients is [{ Encrypted: true, Subscribed: true }]);
    }

    [Fact]
    public async Task After_provisioning_zero_psk_gets_MAC_failure()
    {
        await using var h = await Harness.StartAsync(encryption: EncryptionState.Load(Store));
        await ProvisionAsync(h);

        await using var c = h.Connect();

        Assert.Equal("Handshake MAC failure", await c.NoiseHandshakeAsync(ZeroPsk));
        Assert.Null(await c.ReceiveOrEofAsync());
    }

    [Fact]
    public async Task Device_info_provisionable_flips_to_false()
    {
        await using var h = await Harness.StartAsync(encryption: EncryptionState.Load(Store));
        await using (var before = await h.ConnectNoiseAsync(ZeroPsk))
        {
            var info = await before.DeviceInfoAsync();
            Assert.True(info.ApiEncryptionSupported);
            Assert.True(info.ApiEncryptionProvisionable);
        }

        var key = await ProvisionAsync(h);

        await using var after = await h.ConnectNoiseAsync(key);
        var infoAfter = await after.DeviceInfoAsync();
        Assert.True(infoAfter.ApiEncryptionSupported);
        Assert.False(infoAfter.ApiEncryptionProvisionable);
    }

    [Fact]
    public async Task Provisioning_closes_every_plaintext_and_zero_psk_session()
    {
        await using var h = await Harness.StartAsync(encryption: EncryptionState.Load(Store));
        await using var plain = h.Connect();
        await plain.HelloAsync();
        await using var otherZero = await h.ConnectNoiseAsync(ZeroPsk);
        await otherZero.HelloAsync();
        await using var adopter = await h.ConnectNoiseAsync(ZeroPsk);

        Assert.True(await adopter.SetKeyAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        Assert.Null(await adopter.ReceiveOrEofAsync());
        Assert.Null(await plain.ReceiveOrEofAsync());
        Assert.Null(await otherZero.ReceiveOrEofAsync());
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0);
    }

    [Fact]
    public async Task Provisioning_client_that_resets_before_the_answer_still_closes_other_unkeyed_sessions()
    {
        var state = EncryptionState.Load(Store);
        await using var h = await Harness.StartAsync(encryption: state);
        await using var plain = h.Connect();
        await plain.HelloAsync();
        var adopter = await h.ConnectNoiseAsync(ZeroPsk);
        // KeyChanged runs on the provisioning connection after the key took effect and before the answer is written:
        // reset the client there, so writing the answer fails.
        state.KeyChanged += () =>
        {
            adopter.Abort();
            Thread.Sleep(200); // let the RST reach the server socket
        };

        await adopter.SendAsync(SetKeyRequest(RandomNumberGenerator.GetBytes(32)));

        Assert.Null(await plain.ReceiveOrEofAsync());
        Assert.True(state.HasKey);
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0);
    }

    [Fact]
    public async Task Provisioning_client_that_stopped_reading_cannot_delay_closing_unkeyed_sessions()
    {
        await using var source = new FloodSource();
        await using var h = await Harness.StartAsync(encryption: EncryptionState.Load(Store), source: source);
        await using var plain = h.Connect();
        await plain.HelloAsync();
        await using var adopter = await h.ConnectNoiseAsync(ZeroPsk, receiveBufferSize: 1024);
        await adopter.SendAsync(new SubscribeBluetoothLEAdvertisementsRequest());
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Any(c => c.Subscribed));

        // The adopter never reads, so the server's advertisement flush to it blocks inside the codec's write lock.
        using var flood = new CancellationTokenSource();
        var pumping = Task.Run(() => source.Flood(flood.Token), CancellationToken.None);
        await source.WaitForBacklogAsync();
        await adopter.SendAsync(SetKeyRequest(RandomNumberGenerator.GetBytes(32)));

        var watch = Stopwatch.StartNew();
        Assert.Null(await plain.ReceiveOrEofAsync());
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(1)); // not held up by the stuck answer
        // The adopter itself is unkeyed: it is dropped once its answer cannot be written within the bound.
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0, TimeSpan.FromSeconds(5));
        Assert.Contains(h.Log.Lines, l => l.Contains("provisioning answer", StringComparison.Ordinal));

        await flood.CancelAsync();
        await pumping;
    }

    [Fact]
    public async Task Set_key_is_refused_on_a_plaintext_session_which_stays_open()
    {
        var state = EncryptionState.Load(Store);
        await using var h = await Harness.StartAsync(encryption: state);
        await using var plain = h.Connect();

        Assert.False(await plain.SetKeyAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        Assert.IsType<PingResponse>(await Ping(plain));
        Assert.False(state.HasKey);
        Assert.Null(Store.Load());
    }

    [Fact]
    public async Task Set_key_is_refused_on_a_keyed_session()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var state = EncryptionState.InMemory(key);
        await using var h = await Harness.StartAsync(encryption: state);
        await using var c = await h.ConnectNoiseAsync(key);

        Assert.False(await c.SetKeyAsync(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));

        Assert.IsType<PingResponse>(await Ping(c));
        Assert.Equal(key, state.Key.ToArray());
    }

    [Fact]
    public async Task Set_key_with_empty_or_all_zero_key_is_refused()
    {
        var state = EncryptionState.Load(Store);
        await using var h = await Harness.StartAsync(encryption: state);
        await using var c = await h.ConnectNoiseAsync(ZeroPsk);

        Assert.False(await c.SetKeyAsync(""));
        Assert.False(await c.SetKeyAsync(Convert.ToBase64String(new byte[32])));

        Assert.IsType<PingResponse>(await Ping(c));
        Assert.False(state.HasKey);
    }

    [Fact]
    public async Task Keyed_device_refuses_plaintext_even_without_a_message()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var h = await Harness.StartAsync(encryption: EncryptionState.InMemory(key));
        await using var plain = h.Connect();

        await plain.SendBytesAsync([0x00]);

        Assert.Equal(new byte[] { 0x01, 0x00, 0x00 }, await plain.ReadToEndAsync());
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0);
    }

    [Fact]
    public async Task Oversized_handshake_frame_is_rejected()
    {
        await using var h = await Harness.StartAsync();
        await using var c = h.Connect();

        // A client hello announcing 129 bytes: ESPHome permits at most 128 before the handshake completes.
        await c.SendBytesAsync([0x01, 0x00, 129]);

        var reply = await c.ReadToEndAsync();
        Assert.Equal([0x01, 0x00, (byte)(1 + "Bad handshake packet len".Length), 0x01, .. "Bad handshake packet len"u8], reply);
    }

    [Fact]
    public async Task Stalled_handshake_is_dropped_after_the_timeout()
    {
        await using var h = await Harness.StartAsync(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        await using var c = h.Connect();
        await c.SendBytesAsync([0x01, 0x00]); // half a frame header, then silence

        var watch = Stopwatch.StartNew();
        await c.ReadToEndAsync();

        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
        await TestClient.WaitUntilAsync(() => h.Server.Clients.Count == 0);
    }

    [Fact]
    public async Task Silent_client_is_dropped_after_the_timeout()
    {
        await using var h = await Harness.StartAsync(handshakeTimeout: TimeSpan.FromMilliseconds(300));
        await using var c = h.Connect();

        var watch = Stopwatch.StartNew();
        Assert.Empty(await c.ReadToEndAsync());

        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    /// <summary>Provisions a fresh key the way Home Assistant does and returns it.</summary>
    private static async Task<byte[]> ProvisionAsync(Harness h)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        await using var adopter = await h.ConnectNoiseAsync(ZeroPsk);
        Assert.True(await adopter.SetKeyAsync(Convert.ToBase64String(key)));
        Assert.Null(await adopter.ReceiveOrEofAsync());
        return key;
    }

    private static NoiseEncryptionSetKeyRequest SetKeyRequest(byte[] key) =>
        new() { Key = ByteString.CopyFrom(Encoding.ASCII.GetBytes(Convert.ToBase64String(key))) };

    private static async Task<IMessage> Ping(TestClient c)
    {
        await c.SendAsync(new PingRequest());
        return await c.ReceiveAsync();
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Raises full-size advertisements as fast as it can, to back up a subscriber that does not read.</summary>
    private sealed class FloodSource : IAdvertisementSource
    {
        private static readonly byte[] Data = [.. Enumerable.Range(0, 62).Select(i => (byte)i)];
        private long _raised;

        public event Action<RawAdvertisement>? Received;

        public event Action<ScannerStatus>? StatusChanged { add { } remove { } }

        public ScannerStatus Status => ScannerStatus.Running;

        public ulong AdapterAddress => 0x021122334455;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public void Flood(CancellationToken ct)
        {
            for (ulong address = 1; !ct.IsCancellationRequested; address++)
            {
                Received?.Invoke(new RawAdvertisement(address, -60, 0, Data));
                Interlocked.Increment(ref _raised);
            }
        }

        /// <summary>
        /// Waits until far more data was offered than loopback socket buffers hold (at ~1.4 KB per 16-ad frame, 200 000 ads
        /// are ~17 MB). The batcher drops the oldest ads when its writer is stuck, so the source never waits.
        /// </summary>
        public Task WaitForBacklogAsync() => TestClient.WaitUntilAsync(() => Interlocked.Read(ref _raised) > 200_000, TimeSpan.FromSeconds(10));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
