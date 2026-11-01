using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace WindowsBtProxy.Tray;

/// <summary>
/// The real <see cref="INotifyIcon"/>: one <c>Shell_NotifyIcon</c> entry identified by a GUID rather than a window and id, so Windows
/// remembers the user's "show in the notification area" choice across runs.
/// </summary>
/// <param name="hwnd">The window that receives <paramref name="callbackMessage"/>.</param>
/// <param name="guid">From <see cref="GuidFor"/>.</param>
/// <param name="callbackMessage">The message the shell sends on clicks (<c>NOTIFYICON_VERSION_4</c> format).</param>
/// <param name="iconFor">The <c>HICON</c> for a light; the shell copies it, so the caller keeps ownership.</param>
internal sealed unsafe class Win32NotifyIcon(nint hwnd, Guid guid, uint callbackMessage, Func<Light, nint> iconFor) : INotifyIcon
{
    public void Add(TrayView view)
    {
        var data = Data(view, Native.NIF_MESSAGE | Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP);
        if (!Native.Shell_NotifyIconW(Native.NIM_ADD, data))
        {
            // An entry with our GUID can outlive a tray that was killed; it belongs to a dead window, so replace it.
            _ = Native.Shell_NotifyIconW(Native.NIM_DELETE, Data(default, 0));
            if (!Native.Shell_NotifyIconW(Native.NIM_ADD, data))
            {
                // No taskbar yet (early in sign-in): TaskbarCreated arrives once there is one, and the icon is added then.
                Debug.WriteLine("tray: Shell_NotifyIcon(NIM_ADD) failed");
                return;
            }
        }

        var version = Data(default, 0);
        version.UVersion = Native.NOTIFYICON_VERSION_4;
        _ = Native.Shell_NotifyIconW(Native.NIM_SETVERSION, version);
    }

    public void Update(TrayView view)
        => _ = Native.Shell_NotifyIconW(Native.NIM_MODIFY, Data(view, Native.NIF_ICON | Native.NIF_TIP | Native.NIF_SHOWTIP));

    public void Remove() => _ = Native.Shell_NotifyIconW(Native.NIM_DELETE, Data(default, 0));

    /// <summary>
    /// The icon's GUID for an exe path: the first 16 bytes of the SHA-1 of the lower-cased path, marked as an RFC 4122 name-based
    /// (version 5) GUID. Windows ties a notify-icon GUID to the exe that registered it, so each copy of the program (the installed one,
    /// a dev build) gets its own, and each keeps the same one from run to run.
    /// </summary>
    public static Guid GuidFor(string exePath)
    {
        Span<byte> hash = stackalloc byte[SHA1.HashSizeInBytes];
        SHA1.HashData(Encoding.UTF8.GetBytes(exePath.ToLowerInvariant()), hash);
        var bytes = hash[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private Native.NOTIFYICONDATAW Data(TrayView view, uint flags)
    {
        var data = new Native.NOTIFYICONDATAW
        {
            CbSize = (uint)sizeof(Native.NOTIFYICONDATAW),
            HWnd = hwnd,
            UFlags = flags | Native.NIF_GUID,
            UCallbackMessage = callbackMessage,
            GuidItem = guid,
        };
        if ((flags & Native.NIF_ICON) != 0)
        {
            data.HIcon = iconFor(view.Light);
        }
        if ((flags & Native.NIF_TIP) != 0)
        {
            // TrayLight keeps tooltips within 127 chars; the copy still stops one short of the buffer so the NUL always fits.
            var tip = view.Tooltip.AsSpan(0, Math.Min(view.Tooltip.Length, 127));
            tip.CopyTo(new Span<char>(data.SzTip, 128));
        }
        return data;
    }
}
