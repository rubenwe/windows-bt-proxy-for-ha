using System.Text;
using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Tests.Ipc;

public sealed class PipeProtocolTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReadLineAsync_splits_on_newline_and_returns_null_on_eof()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"v\":1}\n\ncafé\npartial"));

        Assert.Equal("{\"v\":1}", await PipeProtocol.ReadLineAsync(stream, Ct));
        Assert.Equal("", await PipeProtocol.ReadLineAsync(stream, Ct));
        Assert.Equal("café", await PipeProtocol.ReadLineAsync(stream, Ct));
        // A line is only a line once its '\n' arrives: a truncated tail is end of stream, not a request.
        Assert.Null(await PipeProtocol.ReadLineAsync(stream, Ct));
        Assert.Null(await PipeProtocol.ReadLineAsync(stream, Ct));
    }

    [Fact]
    public async Task ReadLineAsync_accepts_a_line_of_exactly_the_limit()
    {
        var content = new string('a', PipeProtocol.MaxLineBytes - 1); // plus '\n' = MaxLineBytes
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content + "\n"));

        Assert.Equal(content, await PipeProtocol.ReadLineAsync(stream, Ct));
    }

    [Fact]
    public async Task ReadLineAsync_stops_reading_once_the_limit_is_passed()
    {
        using var stream = new EndlessStream();

        await Assert.ThrowsAsync<PipeLineTooLongException>(() => PipeProtocol.ReadLineAsync(stream, Ct));
        Assert.True(stream.BytesRead <= PipeProtocol.MaxLineBytes, $"read {stream.BytesRead} bytes");
    }

    [Fact]
    public async Task WriteLineAsync_appends_newline_and_enforces_the_limit()
    {
        using var stream = new MemoryStream();
        await PipeProtocol.WriteLineAsync(stream, "{\"v\":1}", Ct);
        Assert.Equal("{\"v\":1}\n", Encoding.UTF8.GetString(stream.ToArray()));

        await Assert.ThrowsAsync<ArgumentException>(() => PipeProtocol.WriteLineAsync(stream, "a\nb", Ct));
        await Assert.ThrowsAsync<PipeLineTooLongException>(() => PipeProtocol.WriteLineAsync(stream, new string('a', PipeProtocol.MaxLineBytes), Ct));
    }

    /// <summary>A peer that never sends '\n': any number of 'x' bytes, counting how many were taken.</summary>
    private sealed class EndlessStream : Stream
    {
        public long BytesRead { get; private set; }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            buffer.Fill((byte)'x');
            BytesRead += buffer.Length;
            return buffer.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => new(Read(buffer.Span));

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
