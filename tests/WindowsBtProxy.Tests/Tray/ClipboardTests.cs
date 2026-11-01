using System.Runtime.InteropServices;
using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

/// <summary>
/// Uses the real clipboard of whoever runs the tests. The text on it is put back afterwards; the test skips rather than touch a
/// clipboard holding a picture or files, which it could not put back, or one another program has open.
/// </summary>
public sealed class ClipboardTests
{
    private const uint CF_UNICODETEXT = 13;
    private static readonly uint[] UnrestorableFormats = [2 /* CF_BITMAP */, 8 /* CF_DIB */, 15 /* CF_HDROP */, 17 /* CF_DIBV5 */];

    [Fact]
    public void Text_set_on_the_clipboard_reads_back_and_stays_out_of_the_clipboard_history()
    {
        var owner = Native.CreateWindowExW(0, "STATIC", null, 0, 0, 0, 0, 0, Native.HWND_MESSAGE, 0, Native.GetModuleHandleW(null), 0);
        Assert.NotEqual(0, owner);
        try
        {
            if (!TryReadClipboard(owner, out var saved, out var restorable))
            {
                Assert.Skip("Another program has the clipboard open.");
            }
            if (!restorable)
            {
                Assert.Skip("The clipboard holds a picture or files, which this test could not put back.");
            }

            var text = $"wbp-test-{Guid.NewGuid():N} ünïcode";
            try
            {
                Assert.True(Clipboard.SetText(owner, text));

                Assert.True(TryReadClipboard(owner, out var read, out _));
                Assert.Equal(text, read);
                Assert.True(TryReadDword(owner, "CanIncludeInClipboardHistory", out var history));
                Assert.Equal(0u, history);
                Assert.True(TryReadDword(owner, "CanUploadToCloudClipboard", out var cloud));
                Assert.Equal(0u, cloud);
            }
            finally
            {
                Restore(owner, saved);
            }
        }
        finally
        {
            _ = Native.DestroyWindow(owner);
        }
    }

    private static bool TryReadClipboard(nint owner, out string? text, out bool restorable)
    {
        text = null;
        restorable = false;
        if (!Native.OpenClipboard(owner))
        {
            return false;
        }
        try
        {
            restorable = !UnrestorableFormats.Any(Native.IsClipboardFormatAvailable)
                && (Native.CountClipboardFormats() == 0 || Native.IsClipboardFormatAvailable(CF_UNICODETEXT));
            var data = Native.GetClipboardData(CF_UNICODETEXT);
            if (data != 0)
            {
                var pointer = Native.GlobalLock(data);
                text = Marshal.PtrToStringUni(pointer);
                _ = Native.GlobalUnlock(data);
            }
            return true;
        }
        finally
        {
            _ = Native.CloseClipboard();
        }
    }

    private static bool TryReadDword(nint owner, string format, out uint value)
    {
        value = uint.MaxValue;
        if (!Native.OpenClipboard(owner))
        {
            return false;
        }
        try
        {
            var data = Native.GetClipboardData(Native.RegisterClipboardFormatW(format));
            if (data == 0)
            {
                return false;
            }
            value = (uint)Marshal.ReadInt32(Native.GlobalLock(data));
            _ = Native.GlobalUnlock(data);
            return true;
        }
        finally
        {
            _ = Native.CloseClipboard();
        }
    }

    private static void Restore(nint owner, string? saved)
    {
        if (saved is not null)
        {
            _ = Clipboard.SetText(owner, saved);
            return;
        }
        if (Native.OpenClipboard(owner))
        {
            _ = Native.EmptyClipboard();
            _ = Native.CloseClipboard();
        }
    }
}
