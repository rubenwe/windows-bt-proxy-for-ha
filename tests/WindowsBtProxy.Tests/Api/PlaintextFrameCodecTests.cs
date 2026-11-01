using Google.Protobuf;
using WindowsBtProxy.Api;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Api.Proto;

namespace WindowsBtProxy.Tests.Api;

public class PlaintextFrameCodecTests
{
    [Fact]
    public async Task Writes_preamble_length_type_payload()
    {
        var stream = new MemoryStream();
        var codec = new PlaintextFrameCodec(stream);

        await codec.WriteAsync(MessageIds.PingResponse, new PingResponse(), CancellationToken.None);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x08 }, stream.ToArray());
    }

    [Fact]
    public async Task Writes_non_empty_payload_with_varint_length()
    {
        var stream = new MemoryStream();
        var codec = new PlaintextFrameCodec(stream);
        var message = new HelloRequest { ClientInfo = "ha" };

        await codec.WriteAsync(MessageIds.HelloRequest, message, CancellationToken.None);

        var payload = message.ToByteArray();
        Assert.Equal(new byte[] { 0x00, (byte)payload.Length, 0x01 }.Concat(payload), stream.ToArray());
    }

    [Fact]
    public async Task Reads_frame_written_by_the_codec()
    {
        var stream = new MemoryStream();
        var message = new HelloRequest { ClientInfo = "ha", ApiVersionMajor = 1 };
        await new PlaintextFrameCodec(stream).WriteAsync(MessageIds.HelloRequest, message, CancellationToken.None);
        stream.Position = 0;

        var frame = await new PlaintextFrameCodec(stream).ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(MessageIds.HelloRequest, frame.Value.MessageId);
        Assert.Equal(message.ToByteArray(), frame.Value.Payload.ToArray());
    }

    [Fact]
    public async Task Reads_frame_split_across_reads()
    {
        var payload = new HelloRequest { ClientInfo = "split-me" }.ToByteArray();
        var bytes = new byte[] { 0x00, (byte)payload.Length, 0x01 }.Concat(payload).ToArray();
        var codec = new PlaintextFrameCodec(new OneByteAtATimeStream(bytes));

        var frame = await codec.ReadAsync(CancellationToken.None);

        Assert.NotNull(frame);
        Assert.Equal(1, frame.Value.MessageId);
        Assert.Equal(payload, frame.Value.Payload.ToArray());
    }

    [Fact]
    public async Task Reads_two_frames_back_to_back_then_eof()
    {
        var stream = new MemoryStream();
        var writer = new PlaintextFrameCodec(stream);
        await writer.WriteAsync(MessageIds.PingRequest, new PingRequest(), CancellationToken.None);
        await writer.WriteAsync(MessageIds.PingResponse, new PingResponse(), CancellationToken.None);
        stream.Position = 0;
        var reader = new PlaintextFrameCodec(stream);

        Assert.Equal(MessageIds.PingRequest, (await reader.ReadAsync(CancellationToken.None))!.Value.MessageId);
        Assert.Equal(MessageIds.PingResponse, (await reader.ReadAsync(CancellationToken.None))!.Value.MessageId);
        Assert.Null(await reader.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Returns_null_on_clean_eof()
    {
        var codec = new PlaintextFrameCodec(new MemoryStream());

        Assert.Null(await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Eof_mid_frame_throws_EndOfStreamException()
    {
        var codec = new PlaintextFrameCodec(new MemoryStream(new byte[] { 0x00, 0x05, 0x01, 0xAA }));

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Wrong_preamble_throws_InvalidDataException()
    {
        var codec = new PlaintextFrameCodec(new MemoryStream(new byte[] { 0x01, 0x00, 0x08 }));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_without_allocating()
    {
        // Length varint = 10_000_000, then the stream ends. A codec that tried to read the payload would hit EOF
        // (EndOfStreamException) instead, so InvalidDataException proves it rejected the length first.
        var lengthBytes = new byte[5];
        var n = Varint.Write(lengthBytes, 10_000_000);
        var bytes = new byte[] { 0x00 }.Concat(lengthBytes.Take(n)).Append((byte)0x01).ToArray();
        var codec = new PlaintextFrameCodec(new MemoryStream(bytes));

        await Assert.ThrowsAsync<InvalidDataException>(async () => await codec.ReadAsync(CancellationToken.None));
    }

    private sealed class OneByteAtATimeStream(byte[] data) : Stream
    {
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (_position >= data.Length || buffer.Length == 0) return 0;
            buffer.Span[0] = data[_position++];
            return 1;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
