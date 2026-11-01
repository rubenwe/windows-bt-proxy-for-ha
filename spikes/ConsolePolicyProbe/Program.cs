using System.Runtime.InteropServices;

// Spike probe for the tray's console-window question. Logs to %TEMP%\console-probe.log.
//   probe <tray|show> [--no-alloc] [--release-if-alone] [--hold <seconds>] [--ctrlc] [--exit <code>]
// "show" calls AllocConsoleWithOptions(DEFAULT) first (unless --no-alloc) and prints a line to stdout; "tray" does not.
// --release-if-alone calls FreeConsole when this is the only process on its console (variant B).
// --hold keeps the process alive so the launcher can inspect it; --ctrlc waits for Ctrl+C during the hold and exits 130.
var mode = args.Length > 0 ? args[0] : "show";
var noAlloc = args.Contains("--no-alloc");
var release = args.Contains("--release-if-alone");
var ctrlc = args.Contains("--ctrlc");
var hold = IntAfter("--hold", 0);
var exitCode = IntAfter("--exit", 0);

Snapshot("before");
if (mode != "tray" && !noAlloc)
{
    try
    {
        var options = new Native.AllocConsoleOptions { Mode = 0, UseShowWindow = 0, ShowWindow = 0 };
        var hr = Native.AllocConsoleWithOptions(ref options, out var result);
        Log($"AllocConsoleWithOptions hr=0x{hr:X8} result={result} (0 none, 1 new, 2 existing)");
    }
    catch (EntryPointNotFoundException)
    {
        Log("AllocConsoleWithOptions not exported (pre-24H2): ignored");
    }
}
Snapshot("after-alloc");
if (release)
{
    var alone = Native.GetConsoleProcessList(new uint[8], 8) == 1;
    Log($"release-if-alone: alone={alone}");
    if (alone)
    {
        Log($"FreeConsole -> {Native.FreeConsole()}");
        Snapshot("after-free");
    }
}

if (mode != "tray")
{
    Console.Out.WriteLine("probe stdout line");
    Console.Out.Flush();
    Log("wrote stdout line");
}

var interrupted = new ManualResetEventSlim();
if (ctrlc)
{
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; Log("Ctrl+C received"); interrupted.Set(); };
    Log("waiting for Ctrl+C");
}
if (hold > 0)
{
    var seen = interrupted.Wait(TimeSpan.FromSeconds(hold));
    if (ctrlc)
    {
        Log(seen ? "exiting after Ctrl+C" : "hold elapsed, no Ctrl+C seen");
        return seen ? 130 : 0;
    }
}
Log($"exit {exitCode}");
return exitCode;

int IntAfter(string flag, int fallback)
{
    var i = Array.IndexOf(args, flag);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static void Snapshot(string when)
{
    var hwnd = Native.GetConsoleWindow();
    var count = Native.GetConsoleProcessList(new uint[16], 16);
    Log($"{when}: GetConsoleWindow!=0 -> {hwnd != IntPtr.Zero}, processes on console={count}, stdout={StdoutKind()}, stdin={HandleKind(-10)}");
}

static string StdoutKind() => HandleKind(-11);

static string HandleKind(int std)
{
    var h = Native.GetStdHandle(std);
    if (h == IntPtr.Zero || h == new IntPtr(-1))
    {
        return "none";
    }
    return Native.GetFileType(h) switch
    {
        2 => Native.GetConsoleMode(h, out _) ? "console" : "char-device",
        3 => "pipe",
        1 => "file",
        _ => "unknown",
    };
}

static void Log(string message)
{
    var path = Path.Combine(Path.GetTempPath(), "console-probe.log");
    File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} pid={Environment.ProcessId} {message}{Environment.NewLine}");
}

internal static partial class Native
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct AllocConsoleOptions
    {
        public int Mode;          // ALLOC_CONSOLE_MODE: 0 default, 1 new window, 2 no window
        public int UseShowWindow; // BOOL
        public ushort ShowWindow; // WORD
    }

    [DllImport("kernel32.dll")] internal static extern int AllocConsoleWithOptions(ref AllocConsoleOptions options, out int result);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] internal static extern uint GetConsoleProcessList([Out] uint[] list, uint count);
    [DllImport("kernel32.dll")] internal static extern bool FreeConsole();
    [DllImport("kernel32.dll")] internal static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] internal static extern uint GetFileType(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern bool GetConsoleMode(IntPtr handle, out uint mode);
}
