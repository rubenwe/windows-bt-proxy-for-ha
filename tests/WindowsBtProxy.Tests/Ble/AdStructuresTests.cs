using System.Text;
using WindowsBtProxy.Ble;

namespace WindowsBtProxy.Tests.Ble;

public class AdStructuresTests
{
    [Fact]
    public void Build_prefixes_each_section_with_length_and_type()
    {
        var data = AdStructures.Build([
            (0x01, new byte[] { 0x06 }),
            (0x09, Encoding.ASCII.GetBytes("ab")),
        ]);

        Assert.Equal(new byte[] { 0x02, 0x01, 0x06, 0x03, 0x09, 0x61, 0x62 }, data);
    }

    [Fact]
    public void Build_of_no_sections_is_empty()
    {
        Assert.Empty(AdStructures.Build([]));
    }

    [Fact]
    public void Build_accepts_a_section_with_no_data()
    {
        Assert.Equal(new byte[] { 0x01, 0xFF }, AdStructures.Build([(0xFF, ReadOnlyMemory<byte>.Empty)]));
    }

    [Fact]
    public void Build_rejects_a_section_too_long_for_the_one_byte_length()
    {
        // length byte = 1 (type) + data.Length must fit in a byte
        Assert.Equal(256, AdStructures.Build([(0xFF, new byte[254])]).Length);
        Assert.Throws<ArgumentException>(() => AdStructures.Build([(0xFF, new byte[255])]));
    }
}
