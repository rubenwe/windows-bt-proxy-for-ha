using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class IconPixelsTests
{
    private const uint Green = 0xFF22A447;

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_array_has_one_pixel_per_square(int size)
    {
        Assert.Equal(size * size, IconPixels.Dot(size, Green).Length);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void The_centre_pixel_is_the_colour_and_opaque(int size)
    {
        var pixels = IconPixels.Dot(size, Green);

        Assert.Equal(Green, pixels[(size / 2 * size) + (size / 2)]);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    public void The_corner_pixels_are_transparent(int size)
    {
        var pixels = IconPixels.Dot(size, Green);

        Assert.Equal(0u, Alpha(pixels[0]));
        Assert.Equal(0u, Alpha(pixels[size - 1]));
        Assert.Equal(0u, Alpha(pixels[(size - 1) * size]));
        Assert.Equal(0u, Alpha(pixels[(size * size) - 1]));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    [InlineData(32)]
    public void An_edge_pixel_is_partly_transparent(int size)
    {
        var pixels = IconPixels.Dot(size, Green);

        // The leftmost pixel of the middle row is where the disc's edge crosses.
        var alpha = Alpha(pixels[size / 2 * size]);
        Assert.InRange(alpha, 1u, 254u);
    }

    [Fact]
    public void Colour_channels_never_exceed_alpha_because_they_are_premultiplied()
    {
        foreach (var pixel in IconPixels.Dot(24, 0xFFF2A900))
        {
            var alpha = Alpha(pixel);
            Assert.True((pixel >> 16 & 0xFF) <= alpha && (pixel >> 8 & 0xFF) <= alpha && (pixel & 0xFF) <= alpha, $"pixel {pixel:X8}");
        }
    }

    private static uint Alpha(uint pixel) => pixel >> 24;
}
