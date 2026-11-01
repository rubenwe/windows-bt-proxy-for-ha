using System.Runtime.InteropServices;

namespace WindowsBtProxy.Tray;

/// <summary>Puts text on the Windows clipboard, for "Show encryption key…" (the only thing this program copies).</summary>
internal static unsafe class Clipboard
{
    /// <summary>How often to try while another program has the clipboard open; they hold it for milliseconds.</summary>
    private const int OpenAttempts = 10;

    private static readonly TimeSpan OpenRetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Replaces the clipboard's contents with <paramref name="text"/>. Returns false when the clipboard stayed busy or Windows refused
    /// the data.
    /// </summary>
    /// <param name="owner">
    /// The window that owns the clipboard afterwards. Not 0: after <c>OpenClipboard(NULL)</c>, <c>EmptyClipboard</c> leaves no owner
    /// and <c>SetClipboardData</c> may fail. A message-only window will do, and the text stays after it is destroyed.
    /// </param>
    /// <param name="text">The text, as <c>CF_UNICODETEXT</c>.</param>
    /// <remarks>
    /// The text is a secret (the encryption key), so it is marked to stay out of the clipboard history (Win+V) and the cloud
    /// clipboard: otherwise Windows would keep it, and might sync it to other devices, long after it was pasted.
    /// </remarks>
    public static bool SetText(nint owner, string text)
    {
        if (!Open(owner))
        {
            return false;
        }
        try
        {
            if (!Native.EmptyClipboard())
            {
                return false;
            }

            // The block is zeroed, so the extra char is the terminating null that CF_UNICODETEXT needs.
            if (!Put(Native.CF_UNICODETEXT, MemoryMarshal.AsBytes(text.AsSpan()), (text.Length + 1) * sizeof(char)))
            {
                return false;
            }

            // Best effort: a Windows that does not know these formats has neither feature.
            _ = PutDword("CanIncludeInClipboardHistory", 0);
            _ = PutDword("CanUploadToCloudClipboard", 0);
            return true;
        }
        finally
        {
            _ = Native.CloseClipboard();
        }
    }

    private static bool Open(nint owner)
    {
        for (var attempt = 1; ; attempt++)
        {
            if (Native.OpenClipboard(owner))
            {
                return true;
            }
            if (attempt == OpenAttempts)
            {
                return false;
            }
            Thread.Sleep(OpenRetryDelay);
        }
    }

    private static bool PutDword(string formatName, uint value)
    {
        var format = Native.RegisterClipboardFormatW(formatName);
        return format != 0 && Put(format, MemoryMarshal.AsBytes(new ReadOnlySpan<uint>(in value)), sizeof(uint));
    }

    /// <summary>
    /// Copies <paramref name="data"/> into a zeroed movable global block of <paramref name="size"/> bytes and hands it to the
    /// clipboard, which then owns it; frees it only when the clipboard did not take it.
    /// </summary>
    private static bool Put(uint format, ReadOnlySpan<byte> data, int size)
    {
        var block = Native.GlobalAlloc(Native.GMEM_MOVEABLE | Native.GMEM_ZEROINIT, (nuint)size);
        if (block == 0)
        {
            return false;
        }
        var memory = Native.GlobalLock(block);
        if (memory == 0)
        {
            _ = Native.GlobalFree(block);
            return false;
        }
        data.CopyTo(new Span<byte>((void*)memory, size));
        _ = Native.GlobalUnlock(block);

        if (Native.SetClipboardData(format, block) == 0)
        {
            _ = Native.GlobalFree(block);
            return false;
        }
        return true;
    }
}
