using WindowsBtProxy.Api;

namespace WindowsBtProxy.Tests.Api;

public class VarintTests
{
    [Theory]
    [InlineData(0u, new byte[] { 0x00 })]
    [InlineData(127u, new byte[] { 0x7F })]
    [InlineData(128u, new byte[] { 0x80, 0x01 })]
    [InlineData(300u, new byte[] { 0xAC, 0x02 })]
    [InlineData(uint.MaxValue, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F })]
    public void Write_and_read_round_trip(uint value, byte[] bytes)
    {
        var buffer = new byte[5];
        var written = Varint.Write(buffer, value);

        Assert.Equal(bytes.Length, written);
        Assert.Equal(bytes, buffer[..written]);

        Assert.True(Varint.TryRead(bytes, out var read, out var consumed));
        Assert.Equal(value, read);
        Assert.Equal(bytes.Length, consumed);
    }

    [Fact]
    public void TryRead_incomplete_returns_false()
    {
        Assert.False(Varint.TryRead(new byte[] { 0x80 }, out _, out _));
    }

    [Fact]
    public void TryRead_empty_returns_false()
    {
        Assert.False(Varint.TryRead(ReadOnlySpan<byte>.Empty, out _, out _));
    }

    [Fact]
    public void TryRead_six_byte_varint_throws()
    {
        var bytes = new byte[] { 0x80, 0x80, 0x80, 0x80, 0x80, 0x01 };
        Assert.Throws<InvalidDataException>(() => Varint.TryRead(bytes, out _, out _));
    }
}
