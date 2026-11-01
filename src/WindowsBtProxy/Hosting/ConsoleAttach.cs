using System.Runtime.InteropServices;

namespace WindowsBtProxy.Hosting;

/// <summary>
/// Keeps the <c>tray</c> verb free of a console window although the exe is a console-subsystem program. The manifest sets
/// <c>consoleAllocationPolicy</c> to <c>detached</c> (Windows 11 24H2+), so a launch without a console to inherit (the Run key,
/// Explorer, a UAC relaunch) gets none; every verb that prints calls <see cref="Ensure"/> first and gets one back, as before.
/// A launch from a terminal inherits that terminal's console either way. Before 24H2 the manifest setting is ignored and the
/// console always exists, so <c>tray</c> calls <see cref="ReleaseIfAlone"/> and the window only flashes.
/// </summary>
internal static partial class ConsoleAttach
{
    private const int ConsoleModeDefault = 0; // ALLOC_CONSOLE_MODE_DEFAULT: attach to the parent's console, else open a window.

    /// <summary>
    /// False for the verbs that must never show a console window: <c>service</c> (the SCM gives it none anyway) and <c>tray</c>, with
    /// or without <c>--show-key</c>. True for everything else, including no verb at all, because those print usage or results.
    /// </summary>
    public static bool NeedsConsole(string[] args)
        => args.Length == 0 || !(args[0].Equals("service", StringComparison.OrdinalIgnoreCase) || args[0].Equals("tray", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Must run before anything touches <see cref="Console"/>. Attaches to the parent's console or opens a window when there is
    /// none; a no-op where the console already exists. The export only exists on Windows 11 24H2+, and there the manifest is what
    /// detached us, so on older Windows (where the console is already there) the missing export is expected and ignored.
    /// </summary>
    public static void Ensure()
    {
        try
        {
            var options = new AllocConsoleOptions { Mode = ConsoleModeDefault };
            _ = AllocConsoleWithOptions(ref options, out _);
        }
        catch (EntryPointNotFoundException)
        {
            // Pre-24H2: the policy was ignored, the console exists.
        }
    }

    /// <summary>
    /// Pre-24H2 only matters: when this process is the only one on its console (a double click, the Run key), nobody is waiting for
    /// its output, so drop the console. Started from a terminal, other processes share it and it stays.
    /// </summary>
    public static void ReleaseIfAlone()
    {
        Span<uint> list = stackalloc uint[2];
        if (GetConsoleProcessList(ref MemoryMarshal.GetReference(list), (uint)list.Length) == 1)
        {
            _ = FreeConsole();
        }
    }

    /// <summary>Mirrors <c>ALLOC_CONSOLE_OPTIONS</c> in consoleapi.h (Windows SDK 10.0.26100): mode, BOOL, WORD.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct AllocConsoleOptions
    {
        public int Mode;
        public int UseShowWindow;
        public ushort ShowWindow;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int AllocConsoleWithOptions(ref AllocConsoleOptions options, out int result);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetConsoleProcessList(ref uint processList, uint processCount);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();
}
