using Google.Protobuf;

namespace WindowsBtProxy.Api.Framing;

/// <summary>One decoded ESPHome frame: the message id and its still-serialized protobuf payload.</summary>
public readonly record struct Frame(int MessageId, ReadOnlyMemory<byte> Payload);

/// <summary>Reads and writes ESPHome Native API frames over a byte stream (plaintext or Noise).</summary>
public interface IFrameCodec
{
    /// <summary>Reads the next frame, or returns null on a clean end of stream (no bytes of a new frame received).</summary>
    ValueTask<Frame?> ReadAsync(CancellationToken ct);

    /// <summary>Writes <paramref name="message"/> as a frame with id <paramref name="messageId"/>.</summary>
    ValueTask WriteAsync(int messageId, IMessage message, CancellationToken ct);
}
