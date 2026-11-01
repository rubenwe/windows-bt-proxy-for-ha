namespace WindowsBtProxy.Crypto;

/// <summary>
/// Base64 decoding as lenient as Python's <c>binascii.a2b_base64</c> in its default (non-strict) mode, which
/// aioesphomeapi's <c>decode_noise_psk</c> uses: characters outside the base64 alphabet are skipped, and padding ends
/// the data once a quad holds at least two characters. Unlike Python, missing padding is tolerated; a lone trailing
/// character (which cannot encode a byte) is not.
/// </summary>
internal static class LenientBase64
{
    /// <summary>Decodes <paramref name="text"/> (ASCII) into <paramref name="destination"/>; true only if it yields exactly that many bytes.</summary>
    public static bool TryDecodeExactly(ReadOnlySpan<byte> text, Span<byte> destination)
    {
        var written = 0;
        var bits = 0u;
        var bitCount = 0;
        var quadPosition = 0;
        foreach (var c in text)
        {
            if (c == (byte)'=')
            {
                if (quadPosition >= 2)
                {
                    break;
                }
                continue;
            }
            var value = Decode(c);
            if (value < 0)
            {
                continue;
            }
            quadPosition = (quadPosition + 1) & 3;
            bits = (bits << 6) | (uint)value;
            bitCount += 6;
            if (bitCount >= 8)
            {
                bitCount -= 8;
                if (written == destination.Length)
                {
                    return false; // more data than expected
                }
                destination[written++] = (byte)(bits >> bitCount);
                bits &= (1u << bitCount) - 1;
            }
        }
        return quadPosition != 1 && written == destination.Length;
    }

    private static int Decode(byte c) => c switch
    {
        >= (byte)'A' and <= (byte)'Z' => c - 'A',
        >= (byte)'a' and <= (byte)'z' => c - 'a' + 26,
        >= (byte)'0' and <= (byte)'9' => c - '0' + 52,
        (byte)'+' => 62,
        (byte)'/' => 63,
        _ => -1,
    };
}
