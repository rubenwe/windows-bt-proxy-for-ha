using System.Buffers;
using Google.Protobuf;

namespace WindowsBtProxy.Api.Framing;

/// <summary>
/// ESPHome plaintext framing: <c>0x00</c> preamble, varint payload length, varint message id, then the payload.
/// <see cref="WriteAsync"/> is safe to call from several tasks; <see cref="ReadAsync"/> must have a single reader.
/// </summary>
public sealed class PlaintextFrameCodec(Stream stream, int maxPayload = 65536) : IFrameCodec
{
    internal const byte Preamble = 0x00;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _preambleConsumed;

    /// <summary>For a server that already read the first frame's <c>0x00</c> preamble to choose the transport.</summary>
    internal PlaintextFrameCodec(Stream stream, bool firstPreambleConsumed, int maxPayload = 65536)
        : this(stream, maxPayload)
    {
        _preambleConsumed = firstPreambleConsumed;
    }

    public async ValueTask<Frame?> ReadAsync(CancellationToken ct)
    {
        if (_preambleConsumed)
        {
            _preambleConsumed = false;
        }
        else
        {
            var first = new byte[1];
            if (await stream.ReadAsync(first, ct).ConfigureAwait(false) == 0)
            {
                return null;
            }
            if (first[0] != Preamble)
            {
                throw new InvalidDataException($"Expected plaintext preamble 0x00 but got 0x{first[0]:X2}.");
            }
        }

        var length = await ReadVarintAsync(ct).ConfigureAwait(false);
        if (length > (uint)maxPayload)
        {
            throw new InvalidDataException($"Frame payload of {length} bytes exceeds the {maxPayload} byte limit.");
        }
        var id = await ReadVarintAsync(ct).ConfigureAwait(false);
        if (id > int.MaxValue)
        {
            throw new InvalidDataException($"Message id {id} is out of range.");
        }

        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return new Frame((int)id, payload);
    }

    public async ValueTask WriteAsync(int messageId, IMessage message, CancellationToken ct)
    {
        var size = message.CalculateSize();
        var buffer = ArrayPool<byte>.Shared.Rent(1 + Varint.MaxLength + Varint.MaxLength + size);
        try
        {
            buffer[0] = Preamble;
            var offset = 1;
            offset += Varint.Write(buffer.AsSpan(offset), (uint)size);
            offset += Varint.Write(buffer.AsSpan(offset), (uint)messageId);
            message.WriteTo(buffer.AsSpan(offset, size));
            offset += size;

            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await stream.WriteAsync(buffer.AsMemory(0, offset), ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async ValueTask<uint> ReadVarintAsync(CancellationToken ct)
    {
        var bytes = new byte[Varint.MaxLength];
        for (var n = 1; n <= Varint.MaxLength; n++)
        {
            await stream.ReadExactlyAsync(bytes.AsMemory(n - 1, 1), ct).ConfigureAwait(false);
            if (Varint.TryRead(bytes.AsSpan(0, n), out var value, out _))
            {
                return value;
            }
        }
        throw new InvalidDataException("Varint is longer than 32 bits.");
    }
}
