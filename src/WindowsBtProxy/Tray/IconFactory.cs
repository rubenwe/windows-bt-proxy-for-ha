namespace WindowsBtProxy.Tray;

/// <summary>Turns a <see cref="Light"/> into an <c>HICON</c> for the notification area.</summary>
internal static unsafe class IconFactory
{
    /// <summary>The colours from the spec, as 0xAARRGGBB.</summary>
    public static uint ColorOf(Light light) => light switch
    {
        Light.Green => 0xFF22A447,
        Light.Amber => 0xFFF2A900,
        _ => 0xFFD93025,
    };

    /// <summary>
    /// Creates a <paramref name="size"/>-pixel icon showing <paramref name="light"/>'s dot. The caller owns the handle and frees it with
    /// <see cref="Native.DestroyIcon"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">GDI could not create the bitmaps or the icon (out of GDI handles).</exception>
    public static nint Create(Light light, int size)
    {
        var pixels = IconPixels.Dot(size, ColorOf(light));

        // A top-down (negative height) 32-bit DIB, so the pixel rows go in the order IconPixels produces them.
        var header = new Native.BITMAPINFOHEADER
        {
            BiSize = (uint)sizeof(Native.BITMAPINFOHEADER),
            BiWidth = size,
            BiHeight = -size,
            BiPlanes = 1,
            BiBitCount = 32,
            BiCompression = Native.BI_RGB,
        };
        var color = Native.CreateDIBSection(0, header, Native.DIB_RGB_COLORS, out var bits, 0, 0);
        if (color == 0)
        {
            throw new InvalidOperationException("CreateDIBSection failed.");
        }

        nint mask = 0;
        try
        {
            var target = new Span<uint>(bits, pixels.Length);
            for (var i = 0; i < pixels.Length; i++)
            {
                target[i] = Unpremultiply(pixels[i]);
            }

            // The AND mask is ignored for a 32-bit colour bitmap with alpha, but CreateIconIndirect still needs one: all zeros.
            // Monochrome rows are padded to a multiple of 16 bits.
            var maskBytes = new byte[((size + 15) / 16 * 2) * size];
            fixed (byte* maskBits = maskBytes)
            {
                mask = Native.CreateBitmap(size, size, 1, 1, maskBits);
            }
            if (mask == 0)
            {
                throw new InvalidOperationException("CreateBitmap failed.");
            }

            var info = new Native.ICONINFO { FIcon = 1, HbmMask = mask, HbmColor = color };
            var icon = Native.CreateIconIndirect(info);
            return icon != 0 ? icon : throw new InvalidOperationException("CreateIconIndirect failed.");
        }
        finally
        {
            // CreateIconIndirect copies both bitmaps, so they are ours to free either way.
            _ = Native.DeleteObject(color);
            if (mask != 0)
            {
                _ = Native.DeleteObject(mask);
            }
        }
    }

    /// <summary>
    /// Icon colour bitmaps carry straight (not premultiplied) alpha, like the images in an .ico file; the system premultiplies when it
    /// draws. Feeding it premultiplied pixels would darken the anti-aliased edge a second time.
    /// </summary>
    private static uint Unpremultiply(uint pixel)
    {
        var alpha = pixel >> 24;
        if (alpha is 0 or 255)
        {
            return alpha == 0 ? 0 : pixel;
        }
        uint Straight(int shift) => Math.Min(255u, (((pixel >> shift) & 0xFF) * 255 + (alpha / 2)) / alpha) << shift;
        return (alpha << 24) | Straight(16) | Straight(8) | Straight(0);
    }
}
