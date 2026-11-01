using System.Security.Cryptography;
using System.Text;
using Google.Protobuf;
using WindowsBtProxy.Api;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Api;

public class NoiseFrameCodecTests
{
    [Fact]
    public async Task NoiseFrameCodec_round_trips_DeviceInfoResponse()
    {
        using var pair = CipherPair.Handshake();
        var stream = new MemoryStream();
        var message = new DeviceInfoResponse { Name = "bt-proxy-x", MacAddress = "02:11:22:33:44:55", BluetoothProxyFeatureFlags = 1 };

        using (var server = new NoiseFrameCodec(stream, pair.ServerSend, pair.ServerReceive))
        {
            await server.WriteAsync(MessageIds.DeviceInfoResponse, message, CancellationToken.None);
        }
        stream.Position = 0;
        using var client = new NoiseFrameCodec(stream, pair.ClientSend, pair.ClientReceive);
        var frame = await client.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(MessageIds.DeviceInfoResponse, frame.Value.MessageId);
        Assert.Equal(message, DeviceInfoResponse.Parser.ParseFrom(frame.Value.Payload.Span));
        Assert.Null(await client.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Writes_outer_header_and_encrypted_inner_header()
    {
        using var pair = CipherPair.Handshake();
        var stream = new MemoryStream();
        var message = new HelloRequest { ClientInfo = "ha" };
        using var codec = new NoiseFrameCodec(stream, pair.ServerSend, pair.ServerReceive);

        await codec.WriteAsync(MessageIds.HelloRequest, message, CancellationToken.None);

        var bytes = stream.ToArray();
        var payload = message.ToByteArray();
        Assert.Equal(0x01, bytes[0]);
        Assert.Equal(bytes.Length - 3, (bytes[1] << 8) | bytes[2]);
        var plaintext = pair.ClientReceive.Decrypt(bytes.AsSpan(3));
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, (byte)payload.Length }.Concat(payload), plaintext);
    }

    [Fact]
    public async Task Reads_frames_in_order_then_eof()
    {
        using var pair = CipherPair.Handshake();
        var stream = new MemoryStream();
        using (var server = new NoiseFrameCodec(stream, pair.ServerSend, pair.ServerReceive))
        {
            await server.WriteAsync(MessageIds.PingRequest, new PingRequest(), CancellationToken.None);
            await server.WriteAsync(MessageIds.PingResponse, new PingResponse(), CancellationToken.None);
        }
        stream.Position = 0;
        using var client = new NoiseFrameCodec(stream, pair.ClientSend, pair.ClientReceive);

        Assert.Equal(MessageIds.PingRequest, (await client.ReadAsync(CancellationToken.None))!.Value.MessageId);
        Assert.Equal(MessageIds.PingResponse, (await client.ReadAsync(CancellationToken.None))!.Value.MessageId);
        Assert.Null(await client.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NoiseFrameCodec_rejects_plaintext_preamble()
    {
        using var pair = CipherPair.Handshake();
        using var codec = new NoiseFrameCodec(new MemoryStream(new byte[] { 0x00, 0x00, 0x07 }), pair.ServerSend, pair.ServerReceive);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Eof_mid_frame_throws_EndOfStreamException()
    {
        using var pair = CipherPair.Handshake();
        using var codec = new NoiseFrameCodec(new MemoryStream(new byte[] { 0x01, 0x00, 0x20, 0xAA }), pair.ServerSend, pair.ServerReceive);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Tampered_frame_throws_CryptographicException()
    {
        using var pair = CipherPair.Handshake();
        var ciphertext = pair.ClientSend.Encrypt([0x00, 0x07, 0x00, 0x00]);
        ciphertext[0] ^= 0x01;
        using var codec = new NoiseFrameCodec(new MemoryStream(Frame(ciphertext)), pair.ServerSend, pair.ServerReceive);

        await Assert.ThrowsAnyAsync<CryptographicException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Inner_length_beyond_plaintext_is_rejected()
    {
        using var pair = CipherPair.Handshake();
        var ciphertext = pair.ClientSend.Encrypt([0x00, 0x07, 0x00, 0x02, 0xAA]);
        using var codec = new NoiseFrameCodec(new MemoryStream(Frame(ciphertext)), pair.ServerSend, pair.ServerReceive);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Inner_plaintext_shorter_than_header_is_rejected()
    {
        using var pair = CipherPair.Handshake();
        var ciphertext = pair.ClientSend.Encrypt([0x00, 0x07, 0x00]);
        using var codec = new NoiseFrameCodec(new MemoryStream(Frame(ciphertext)), pair.ServerSend, pair.ServerReceive);

        await Assert.ThrowsAsync<InvalidDataException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Payload_is_sliced_to_the_inner_length()
    {
        using var pair = CipherPair.Handshake();
        var ciphertext = pair.ClientSend.Encrypt([0x00, 0x07, 0x00, 0x01, 0xAA, 0xBB]);
        using var codec = new NoiseFrameCodec(new MemoryStream(Frame(ciphertext)), pair.ServerSend, pair.ServerReceive);

        var frame = await codec.ReadAsync(CancellationToken.None);

        Assert.Equal(7, frame!.Value.MessageId);
        Assert.Equal(new byte[] { 0xAA }, frame.Value.Payload.ToArray());
    }

    [Fact]
    public async Task Server_handshake_sends_name_and_mac_in_hello()
    {
        var psk = RandomNumberGenerator.GetBytes(32);
        using var initiator = new NoiseInitiator(psk);
        var input = new MemoryStream([.. Frame([]), .. Frame([0x00, .. initiator.WriteMessageA()])]);
        var output = new MemoryStream();
        var duplex = new DuplexStream(input, output);

        using var codec = await NoiseServerHandshake.RunAsync(duplex, psk, "bt-proxy-x", "021122334455", CancellationToken.None);

        var written = new MemoryStream(output.ToArray());
        var hello = ReadFrame(written);
        Assert.Equal([0x01, .. "bt-proxy-x"u8, 0x00, .. "021122334455"u8, 0x00], hello);
        var handshake = ReadFrame(written);
        Assert.Equal(0x00, handshake[0]);
        initiator.ReadMessageB(handshake.AsSpan(1));

        // The returned codec speaks with the split ciphers: the client can decrypt what it writes, and vice versa.
        var (clientSend, clientReceive) = initiator.Split();
        using (clientSend)
        using (clientReceive)
        {
            await codec.WriteAsync(MessageIds.PingResponse, new PingResponse(), CancellationToken.None);
            var reply = new MemoryStream(output.ToArray()[(int)written.Position..]);
            Assert.Equal(MessageIds.PingResponse, (await new NoiseFrameCodec(reply, clientSend, clientReceive).ReadAsync(CancellationToken.None))!.Value.MessageId);
        }
    }

    [Fact]
    public async Task Server_handshake_with_wrong_psk_sends_MAC_failure_frame()
    {
        using var initiator = new NoiseInitiator(RandomNumberGenerator.GetBytes(32));
        var input = new MemoryStream([.. Frame([]), .. Frame([0x00, .. initiator.WriteMessageA()])]);
        var output = new MemoryStream();

        await Assert.ThrowsAsync<NoiseAuthenticationException>(() =>
            NoiseServerHandshake.RunAsync(new DuplexStream(input, output), RandomNumberGenerator.GetBytes(32), "bt-proxy-x", "021122334455", CancellationToken.None));

        var written = new MemoryStream(output.ToArray());
        ReadFrame(written); // server hello
        Assert.Equal([0x01, .. "Handshake MAC failure"u8], ReadFrame(written));
        Assert.Equal(written.Length, written.Position);
    }

    [Fact]
    public async Task Server_handshake_rejects_plaintext_client_with_bad_indicator_frame()
    {
        // A plaintext client's first frame starts with 0x00; ESPHome answers with a 0x01-framed reject so the client
        // reports "requires encryption" instead of a bare disconnect.
        var input = new MemoryStream(new byte[] { 0x00, 0x00, 0x01 });
        var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NoiseServerHandshake.RunAsync(new DuplexStream(input, output), new byte[32], "n", "m", CancellationToken.None));

        Assert.Equal([0x01, .. "Bad indicator byte"u8], ReadFrame(new MemoryStream(output.ToArray())));
    }

    [Fact]
    public async Task Server_handshake_rejects_handshake_frame_without_zero_prefix()
    {
        var input = new MemoryStream([.. Frame([]), .. Frame([0x01, .. new byte[48]])]);
        var output = new MemoryStream();

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            NoiseServerHandshake.RunAsync(new DuplexStream(input, output), new byte[32], "n", "m", CancellationToken.None));
    }

    [Fact]
    public async Task Server_handshake_eof_throws_EndOfStreamException()
    {
        var input = new MemoryStream(Frame([]));

        await Assert.ThrowsAsync<EndOfStreamException>(() =>
            NoiseServerHandshake.RunAsync(new DuplexStream(input, new MemoryStream()), new byte[32], "n", "m", CancellationToken.None));
    }

    private static byte[] Frame(byte[] payload) => [0x01, (byte)(payload.Length >> 8), (byte)payload.Length, .. payload];

    private static byte[] ReadFrame(Stream s)
    {
        var header = new byte[3];
        s.ReadExactly(header);
        Assert.Equal(0x01, header[0]);
        var payload = new byte[(header[1] << 8) | header[2]];
        s.ReadExactly(payload);
        return payload;
    }

    /// <summary>Both ends of a completed random-key handshake.</summary>
    private sealed class CipherPair : IDisposable
    {
        public required CipherState ClientSend { get; init; }
        public required CipherState ClientReceive { get; init; }
        public required CipherState ServerSend { get; init; }
        public required CipherState ServerReceive { get; init; }

        public static CipherPair Handshake()
        {
            var psk = RandomNumberGenerator.GetBytes(32);
            using var initiator = new NoiseInitiator(psk);
            using var responder = new NoiseResponder(psk);
            responder.ReadMessageA(initiator.WriteMessageA());
            initiator.ReadMessageB(responder.WriteMessageB());
            var (cs, cr) = initiator.Split();
            var (ss, sr) = responder.Split();
            return new CipherPair { ClientSend = cs, ClientReceive = cr, ServerSend = ss, ServerReceive = sr };
        }

        public void Dispose()
        {
            ClientSend.Dispose();
            ClientReceive.Dispose();
            ServerSend.Dispose();
            ServerReceive.Dispose();
        }
    }

    /// <summary>Reads from one stream and writes to another, like a socket seen from the server.</summary>
    private sealed class DuplexStream(Stream input, Stream output) : Stream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
