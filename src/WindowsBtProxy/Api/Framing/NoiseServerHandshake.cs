using System.Security.Cryptography;
using System.Text;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api.Framing;

/// <summary>
/// Server side of ESPHome's Noise connection setup, mirroring the device firmware and aioesphomeapi's <c>noise.py</c>:
/// <list type="number">
/// <item>client hello frame (payload ignored, normally empty) → server hello <c>0x01</c> + name + <c>\0</c> + MAC hex + <c>\0</c>;</item>
/// <item>handshake frame <c>0x00</c> + message A → <c>0x00</c> + message B, or the reject frame <c>0x01</c> + "Handshake MAC failure".</item>
/// </list>
/// Both client frames may be at most <see cref="MaxHandshakeFrameLength"/> bytes, as in ESPHome; a longer one is rejected
/// without being read. Malformed input gets an explicit reject frame where ESPHome sends one, then
/// <see cref="InvalidDataException"/>. The caller bounds the whole exchange with a timeout and closes the stream on any exception.
/// </summary>
public static class NoiseServerHandshake
{
    private const byte ChosenProtocol = 0x01; // Noise_NNpsk0_25519_ChaChaPoly_SHA256
    private const byte HandshakeOk = 0x00;
    private const byte HandshakeReject = 0x01;

    /// <summary>Largest client frame accepted before the handshake completes (ESPHome's limit).</summary>
    public const int MaxHandshakeFrameLength = 128;

    public static async Task<NoiseFrameCodec> RunAsync(Stream s, ReadOnlyMemory<byte> psk, string nameForHello, string macHex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);

        // A plaintext client's first byte is 0x00; ESPHome answers with a reject frame so the client can report that
        // the device requires encryption.
        var indicator = await NoiseFrameCodec.ReadIndicatorAsync(s, ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Client closed the connection before its Noise hello.");
        if (indicator != NoiseFrameCodec.Indicator)
        {
            await RejectAsync(s, "Bad indicator byte", ct).ConfigureAwait(false);
            throw NoiseFrameCodec.BadIndicator(indicator);
        }
        return await RunAfterIndicatorAsync(s, psk, nameForHello, macHex, ct).ConfigureAwait(false);
    }

    /// <summary>Same as <see cref="RunAsync"/> for a caller that already read the client hello's <c>0x01</c> indicator byte.</summary>
    internal static async Task<NoiseFrameCodec> RunAfterIndicatorAsync(Stream s, ReadOnlyMemory<byte> psk, string nameForHello, string macHex, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        var hello = ServerHello(nameForHello, macHex);
        if (psk.Length != NoiseHandshake.PskSize)
        {
            throw new ArgumentException($"The Noise pre-shared key is {NoiseHandshake.PskSize} bytes.", nameof(psk));
        }

        // 1. Client hello (payload ignored) → server hello.
        await ReadHandshakeFrameBodyAsync(s, ct).ConfigureAwait(false);
        await NoiseFrameCodec.WriteFrameAsync(s, hello, ct).ConfigureAwait(false);

        // 2. Handshake: 0x00 + message A.
        var indicator = await NoiseFrameCodec.ReadIndicatorAsync(s, ct).ConfigureAwait(false)
            ?? throw new EndOfStreamException("Client closed the connection before its Noise handshake.");
        if (indicator != NoiseFrameCodec.Indicator)
        {
            throw NoiseFrameCodec.BadIndicator(indicator);
        }
        var frame = await ReadHandshakeFrameBodyAsync(s, ct).ConfigureAwait(false);
        if (frame.Length == 0)
        {
            await RejectAsync(s, "Empty handshake message", ct).ConfigureAwait(false);
            throw new InvalidDataException("Noise handshake frame is empty.");
        }
        if (frame[0] != HandshakeOk)
        {
            await RejectAsync(s, "Bad handshake error byte", ct).ConfigureAwait(false);
            throw new InvalidDataException($"Noise handshake frame starts with 0x{frame[0]:X2}, expected 0x00.");
        }

        using var responder = new NoiseResponder(psk.Span);
        try
        {
            responder.ReadMessageA(frame.AsSpan(1));
        }
        catch (CryptographicException e)
        {
            await RejectAsync(s, "Handshake MAC failure", ct).ConfigureAwait(false);
            throw new NoiseAuthenticationException("The client's Noise handshake did not authenticate (wrong encryption key?).", e);
        }
        catch (InvalidDataException)
        {
            await RejectAsync(s, "Handshake error", ct).ConfigureAwait(false);
            throw;
        }

        var messageB = responder.WriteMessageB();
        var (send, receive) = responder.Split();
        try
        {
            await NoiseFrameCodec.WriteFrameAsync(s, (byte[])[HandshakeOk, .. messageB], ct).ConfigureAwait(false);
            return new NoiseFrameCodec(s, send, receive);
        }
        catch
        {
            send.Dispose();
            receive.Dispose();
            throw;
        }
    }

    private static byte[] ServerHello(string name, string macHex)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(macHex);
        if (name.Contains('\0') || macHex.Contains('\0'))
        {
            throw new ArgumentException("Server hello fields cannot contain NUL characters.");
        }
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var macBytes = Encoding.UTF8.GetBytes(macHex);
        return [ChosenProtocol, .. nameBytes, 0x00, .. macBytes, 0x00];
    }

    /// <summary>Reads a handshake-phase frame body; one longer than <see cref="MaxHandshakeFrameLength"/> gets ESPHome's reject and is never read.</summary>
    private static async ValueTask<byte[]> ReadHandshakeFrameBodyAsync(Stream s, CancellationToken ct)
    {
        var length = await NoiseFrameCodec.ReadFrameLengthAsync(s, ct).ConfigureAwait(false);
        if (length > MaxHandshakeFrameLength)
        {
            await RejectAsync(s, "Bad handshake packet len", ct).ConfigureAwait(false);
            throw new InvalidDataException($"Noise handshake frame of {length} bytes exceeds the {MaxHandshakeFrameLength} byte limit.");
        }
        var payload = new byte[length];
        await s.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    /// <summary>Best effort: if the peer is already gone, the exception the caller is about to throw is the one that matters.</summary>
    private static async ValueTask RejectAsync(Stream s, string reason, CancellationToken ct)
    {
        try
        {
            await NoiseFrameCodec.WriteFrameAsync(s, (byte[])[HandshakeReject, .. Encoding.ASCII.GetBytes(reason)], ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
