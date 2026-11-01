namespace WindowsBtProxy.Tray;

/// <summary>
/// Draws the tray icon's coloured dot in code, so the exe carries no image files and the dot is sharp at every DPI's icon size.
/// </summary>
internal static class IconPixels
{
    /// <summary>Samples per pixel along each axis; 4 × 4 is enough for a smooth edge at 16 px.</summary>
    private const int Samples = 4;

    /// <summary>How much of the colour the rim keeps, which makes the dot stand out on a taskbar of a similar colour.</summary>
    private const double RimShade = 0.7;

    /// <summary>
    /// A filled disc of <paramref name="argb"/> (0xAARRGGBB) on a transparent square, with an anti-aliased edge and a rim 1 px wide in
    /// a darker shade of the same colour.
    /// </summary>
    /// <returns>
    /// <paramref name="size"/> × <paramref name="size"/> pixels, top row first, each a <c>uint</c> laid out as 0xAARRGGBB, i.e. BGRA bytes
    /// in memory, with the colour premultiplied by alpha.
    /// </returns>
    public static uint[] Dot(int size, uint argb)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 4);

        var alpha = (argb >> 24) / 255.0;
        var fill = (R: (argb >> 16 & 0xFF) * alpha, G: (argb >> 8 & 0xFF) * alpha, B: (argb & 0xFF) * alpha);
        var rim = (R: fill.R * RimShade, G: fill.G * RimShade, B: fill.B * RimShade);

        // Half a pixel of margin, so the outermost pixels are only partly covered and the edge is smooth instead of clipped.
        var centre = size / 2.0;
        var radius = centre - 0.5;
        var innerRadius = radius - 1;

        var pixels = new uint[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                double a = 0, r = 0, g = 0, b = 0;
                for (var sy = 0; sy < Samples; sy++)
                {
                    for (var sx = 0; sx < Samples; sx++)
                    {
                        var dx = x + ((sx + 0.5) / Samples) - centre;
                        var dy = y + ((sy + 0.5) / Samples) - centre;
                        var distance = Math.Sqrt((dx * dx) + (dy * dy));
                        if (distance > radius)
                        {
                            continue;
                        }
                        var shade = distance > innerRadius ? rim : fill;
                        a += alpha;
                        r += shade.R;
                        g += shade.G;
                        b += shade.B;
                    }
                }

                const int count = Samples * Samples;
                pixels[(y * size) + x] = (Channel(a * 255, count) << 24) | (Channel(r, count) << 16) | (Channel(g, count) << 8) | Channel(b, count);
            }
        }
        return pixels;
    }

    private static uint Channel(double sum, int count) => (uint)Math.Clamp(Math.Round(sum / count), 0, 255);
}
