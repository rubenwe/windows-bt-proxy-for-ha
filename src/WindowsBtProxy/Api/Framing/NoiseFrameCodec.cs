using System.Buffers;
using Google.Protobuf;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api.Framing;

/// <summary>
/// ESPHome Noise transport framing after the handshake. Outer frame: <c>0x01</c>, 16-bit big-endian length, ciphertext.
/// The ciphertext decrypts to <c>type_hi, type_lo, len_hi, len_lo, payload</c>.
/// Owns and disposes the two ciphers. <see cref="WriteAsync"/> is safe to call from several tasks (encryption happens
/// under the write lock so nonces reach the wire in order); <see cref="ReadAsync"/> must have a single reader.
/// Any decryption failure surfaces as <see cref="System.Security.Cryptography.CryptographicException"/>: treat the connection as dead.
/// </summary>
public sealed class NoiseFrameCodec(Stream stream, CipherState send, CipherState receive, int maxPayload = 65536) : IFrameCodec, IDisposable
{
    internal const byte Indicator = 0x01;
    private const int InnerHeaderSize = 4;
    private const int MaxFrameLength = ushort.MaxValue;

    /// <summary>Largest protobuf payload that fits one Noise frame (65535 minus inner header and tag).</summary>
    public const int MaxWritablePayload = MaxFrameLength - InnerHeaderSize - CipherState.TagSize;

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public async ValueTask<Frame?> ReadAsync(CancellationToken ct)
    {
        var ciphertext = await ReadFrameAsync(stream, ct).ConfigureAwait(false);
        if (ciphertext is null)
        {
            return null;
        }

        var plaintext = receive.Decrypt(ciphertext);
        if (plaintext.Length < InnerHeaderSize)
        {
            throw new InvalidDataException($"Decrypted Noise message is {plaintext.Length} bytes; the header alone is {InnerHeaderSize}.");
        }
        var messageId = (plaintext[0] << 8) | plaintext[1];
        var length = (plaintext[2] << 8) | plaintext[3];
        if (length > plaintext.Length - InnerHeaderSize)
        {
            throw new InvalidDataException($"Noise message declares {length} payload bytes but carries {plaintext.Length - InnerHeaderSize}.");
        }
        if (length > maxPayload)
        {
            throw new InvalidDataException($"Frame payload of {length} bytes exceeds the {maxPayload} byte limit.");
        }
        // Slice to the declared length, as ESPHome's own server does; it was checked against what was received.
        return new Frame(messageId, plaintext.AsMemory(InnerHeaderSize, length));
    }

    public async ValueTask WriteAsync(int messageId, IMessage message, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(messageId);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(messageId, ushort.MaxValue);
        var size = message.CalculateSize();
        if (size > MaxWritablePayload)
        {
            throw new ArgumentException($"{message.GetType().Name} of {size} bytes does not fit one Noise frame (max {MaxWritablePayload}).", nameof(message));
        }

        var plaintextLength = InnerHeaderSize + size;
        var frameLength = 3 + plaintextLength + CipherState.TagSize;
        var plaintext = ArrayPool<byte>.Shared.Rent(plaintextLength);
        var frame = ArrayPool<byte>.Shared.Rent(frameLength);
        try
        {
            plaintext[0] = (byte)(messageId >> 8);
            plaintext[1] = (byte)messageId;
            plaintext[2] = (byte)(size >> 8);
            plaintext[3] = (byte)size;
            message.WriteTo(plaintext.AsSpan(InnerHeaderSize, size));
            WriteHeader(frame, plaintextLength + CipherState.TagSize);

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                send.Encrypt(plaintext.AsSpan(0, plaintextLength), frame.AsSpan(3));
                await stream.WriteAsync(frame.AsMemory(0, frameLength), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(plaintext);
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    public void Dispose()
    {
        send.Dispose();
        receive.Dispose();
    }

    /// <summary>Reads one outer frame's payload; null on a clean end of stream before its first byte.</summary>
    internal static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var indicator = await ReadIndicatorAsync(stream, ct).ConfigureAwait(false);
        if (indicator is null)
        {
            return null;
        }
        if (indicator != Indicator)
        {
            throw BadIndicator(indicator.Value);
        }
        return await ReadFrameBodyAsync(stream, ct).ConfigureAwait(false);
    }

    /// <summary>Reads a frame's first byte, or null on a clean end of stream.</summary>
    internal static async ValueTask<byte?> ReadIndicatorAsync(Stream stream, CancellationToken ct)
    {
        var first = new byte[1];
        return await stream.ReadAsync(first, ct).ConfigureAwait(false) == 0 ? null : first[0];
    }

    /// <summary>Reads the 16-bit length and payload that follow the indicator byte.</summary>
    internal static async ValueTask<byte[]> ReadFrameBodyAsync(Stream stream, CancellationToken ct)
    {
        var lengthBytes = new byte[2];
        await stream.ReadExactlyAsync(lengthBytes, ct).ConfigureAwait(false);
        var payload = new byte[(lengthBytes[0] << 8) | lengthBytes[1]];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }

    internal static InvalidDataException BadIndicator(byte indicator) => new(indicator == 0x00
        ? "Expected Noise preamble 0x01 but got 0x00: the client is using the plaintext protocol."
        : $"Expected Noise preamble 0x01 but got 0x{indicator:X2}.");

    /// <summary>Writes one unencrypted outer frame (handshake phase).</summary>
    internal static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> payload, CancellationToken ct)
    {
        if (payload.Length > MaxFrameLength)
        {
            throw new ArgumentException($"A Noise frame carries at most {MaxFrameLength} bytes.", nameof(payload));
        }
        var frame = new byte[3 + payload.Length];
        WriteHeader(frame, payload.Length);
        payload.CopyTo(frame.AsMemory(3));
        await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteHeader(Span<byte> frame, int payloadLength)
    {
        frame[0] = Indicator;
        frame[1] = (byte)(payloadLength >> 8);
        frame[2] = (byte)payloadLength;
    }
}
