using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using WindowsBtProxy.Api;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Api;

/// <summary>A running server on an ephemeral loopback port plus a fake (or caller-supplied) source.</summary>
internal sealed class Harness : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly IAdvertisementSource _source;
    private Task _run = Task.CompletedTask;

    private Harness(ApiServer server, IAdvertisementSource source)
    {
        Server = server;
        _source = source;
    }

    public ApiServer Server { get; }

    /// <summary>What the server logged.</summary>
    public ListLogger Log { get; private init; } = new();

    public static async Task<Harness> StartAsync(
        bool startSource = false,
        EncryptionState? encryption = null,
        TimeSpan? handshakeTimeout = null,
        IAdvertisementSource? source = null)
    {
        var logger = new ListLogger();
        source ??= new FakeAdvertisementSource(TimeSpan.FromMilliseconds(20));
        var identity = new DeviceIdentity("windows-bt-proxy-334455", "Windows Bluetooth Proxy", 0x021122334455, "TESTPC", "Nov  1 2026, 12:00:00");
        var server = new ApiServer(new IPEndPoint(IPAddress.Loopback, 0), identity, source, encryption ?? EncryptionState.InMemory(), logger, TimeProvider.System);
        if (handshakeTimeout is { } timeout)
        {
            server.HandshakeTimeout = timeout;
        }
        var h = new Harness(server, source) { Log = logger };
        h._run = server.RunAsync(h._cts.Token); // binds the listener before its first await, so LocalEndpoint is valid right away
        if (startSource)
        {
            await source.StartAsync(CancellationToken.None);
        }
        return h;
    }

    /// <summary>A plaintext client.</summary>
    public TestClient Connect() => new(Server.LocalEndpoint);

    /// <summary>A Noise client that completed the handshake with <paramref name="psk"/>; fails the test if the server rejects it.</summary>
    public async Task<TestClient> ConnectNoiseAsync(ReadOnlyMemory<byte> psk, int? receiveBufferSize = null)
    {
        var c = new TestClient(Server.LocalEndpoint, receiveBufferSize);
        var reject = await c.NoiseHandshakeAsync(psk);
        Assert.Null(reject);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        await _source.DisposeAsync();
        await _cts.CancelAsync();
        await _run.WaitAsync(TimeSpan.FromSeconds(5)); // RunAsync must end cleanly on cancellation
        _cts.Dispose();
    }
}

/// <summary>Minimal ESPHome client over a TCP socket: plaintext by default, Noise after <see cref="NoiseHandshakeAsync"/>. Every wait is bounded.</summary>
internal sealed class TestClient : IAsyncDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private readonly TcpClient _tcp = new();
    private readonly NetworkStream _stream;
    private IFrameCodec _codec;

    /// <param name="receiveBufferSize">Set before connecting, so a client that stops reading backs the server up quickly.</param>
    public TestClient(IPEndPoint server, int? receiveBufferSize = null)
    {
        if (receiveBufferSize is { } size)
        {
            _tcp.ReceiveBufferSize = size;
        }
        _tcp.Connect(server);
        _stream = _tcp.GetStream();
        _codec = new PlaintextFrameCodec(_stream);
    }

    /// <summary>The name and MAC from the Noise server hello, once <see cref="NoiseHandshakeAsync"/> ran.</summary>
    public (string Name, string Mac)? ServerHello { get; private set; }

    /// <summary>
    /// Runs the client side of ESPHome's Noise setup like aioesphomeapi (hello and handshake frames sent at once).
    /// Returns null on success (later messages are encrypted) or the server's reject reason.
    /// </summary>
    public async Task<string?> NoiseHandshakeAsync(ReadOnlyMemory<byte> psk)
    {
        using var cts = new CancellationTokenSource(Limit);
        using var initiator = new NoiseInitiator(psk.Span);
        await SendBytesAsync([0x01, 0x00, 0x00, .. HandshakeFrame([0x00, .. initiator.WriteMessageA()])]);

        var hello = await NoiseFrameCodec.ReadFrameAsync(_stream, cts.Token) ?? throw new EndOfStreamException("No server hello.");
        Assert.Equal(0x01, hello[0]);
        var fields = Encoding.UTF8.GetString(hello.AsSpan(1)).Split('\0');
        ServerHello = (fields[0], fields[1]);

        var reply = await NoiseFrameCodec.ReadFrameAsync(_stream, cts.Token) ?? throw new EndOfStreamException("No handshake reply.");
        if (reply[0] != 0x00)
        {
            return Encoding.ASCII.GetString(reply.AsSpan(1));
        }
        initiator.ReadMessageB(reply.AsSpan(1));
        var (send, receive) = initiator.Split();
        _codec = new NoiseFrameCodec(_stream, send, receive);
        return null;
    }

    public async Task<HelloResponse> HelloAsync()
    {
        await SendAsync(new HelloRequest { ClientInfo = "test", ApiVersionMajor = 1, ApiVersionMinor = 10 });
        return Assert.IsType<HelloResponse>(await ReceiveAsync());
    }

    public async Task<DeviceInfoResponse> DeviceInfoAsync()
    {
        await SendAsync(new DeviceInfoRequest());
        return Assert.IsType<DeviceInfoResponse>(await ReceiveAsync());
    }

    /// <summary>Sends <c>NoiseEncryptionSetKeyRequest</c> with <paramref name="keyText"/> as the key field (HA sends base64 text) and returns the answer.</summary>
    public async Task<bool> SetKeyAsync(string keyText)
    {
        await SendAsync(new NoiseEncryptionSetKeyRequest { Key = ByteString.CopyFrom(Encoding.ASCII.GetBytes(keyText)) });
        return Assert.IsType<NoiseEncryptionSetKeyResponse>(await ReceiveAsync()).Success;
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

    /// <summary>Every byte the server sends until it closes the connection (a reset ends the read too).</summary>
    public async Task<byte[]> ReadToEndAsync()
    {
        using var cts = new CancellationTokenSource(Limit);
        var all = new MemoryStream();
        var buffer = new byte[256];
        try
        {
            int n;
            while ((n = await _stream.ReadAsync(buffer, cts.Token)) > 0)
            {
                all.Write(buffer, 0, n);
            }
        }
        catch (IOException)
        {
        }
        return all.ToArray();
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
        (_codec as IDisposable)?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static byte[] HandshakeFrame(byte[] payload) => [0x01, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    private static byte[] Encode(uint value)
    {
        var buffer = new byte[Varint.MaxLength];
        var n = Varint.Write(buffer, value);
        return buffer[..n];
    }
}
