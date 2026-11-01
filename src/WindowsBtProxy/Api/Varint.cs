namespace WindowsBtProxy.Api;

/// <summary>Protobuf base-128 varint encoding for 32-bit unsigned values, as used in ESPHome frame headers.</summary>
public static class Varint
{
    /// <summary>The longest encoding of a 32-bit value.</summary>
    public const int MaxLength = 5;

    /// <summary>Writes <paramref name="value"/> to <paramref name="dest"/> and returns the number of bytes written (1 to 5).</summary>
    public static int Write(Span<byte> dest, uint value)
    {
        var i = 0;
        while (value >= 0x80)
        {
            dest[i++] = (byte)(value | 0x80);
            value >>= 7;
        }
        dest[i++] = (byte)value;
        return i;
    }

    /// <summary>
    /// Reads a varint from the start of <paramref name="src"/>. Returns false if <paramref name="src"/> ends before the
    /// varint does. Throws <see cref="InvalidDataException"/> if the varint cannot fit in 32 bits (more than 5 bytes).
    /// </summary>
    public static bool TryRead(ReadOnlySpan<byte> src, out uint value, out int consumed)
    {
        value = 0;
        consumed = 0;
        for (var i = 0; i < src.Length; i++)
        {
            var b = src[i];
            if (i == MaxLength - 1 && (b & 0xF0) != 0)
            {
                // Fifth byte: continuation bit or bits above 32 are set, so this can never be a valid uint.
                throw new InvalidDataException("Varint is longer than 32 bits.");
            }
            value |= (uint)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                consumed = i + 1;
                return true;
            }
        }
        value = 0;
        return false;
    }
}
