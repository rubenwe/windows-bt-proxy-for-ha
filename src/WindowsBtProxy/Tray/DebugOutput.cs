using System.Runtime.InteropServices;

namespace WindowsBtProxy.Tray;

/// <summary>
/// The tray's diagnostics: it has no console and no log file of its own, so what it swallows goes to <c>OutputDebugString</c>, which
/// Sysinternals DebugView (or an attached debugger) shows. Unlike <c>Debug.WriteLine</c> this is not compiled out of the Release build that
/// ships. Never pass it the encryption key or anything derived from it: any process on the machine can read this output.
/// </summary>
internal static partial class DebugOutput
{
    private const string Prefix = "[windows-bt-proxy] ";

    public static void Write(string message) => OutputDebugStringW(Prefix + message + "\n");

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void OutputDebugStringW(string lpOutputString);
}
