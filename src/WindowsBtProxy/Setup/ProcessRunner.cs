using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WindowsBtProxy.Setup;

/// <summary>
/// The real <see cref="IProcessRunner"/>. Bare program names are resolved against the Windows system directory, never the
/// current directory or this program's own folder, because the installer runs elevated from wherever the user downloaded it.
/// The last lines of the child's output are kept so a failure can say what the tool complained about.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public const int OutputTailLines = 10;

    /// <summary>
    /// What sc.exe, net.exe and netsh.exe write into a pipe: the OEM code page (850 on a German system), not UTF-8, which would
    /// garble umlauts in their messages. <c>CultureInfo.TextInfo.OEMCodePage</c> is no help under InvariantGlobalization (it says 437),
    /// so ask Windows. Code pages other than the built-in ones need the code-pages provider; UTF-8 if anything goes wrong.
    /// </summary>
    internal static Encoding OemEncoding { get; } = CreateOemEncoding();

    private static Encoding CreateOemEncoding()
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding((int)GetOEMCP()) ?? Encoding.UTF8;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException)
        {
            return Encoding.UTF8;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    public async Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct)
    {
        var info = CreateStartInfo(exe, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.StandardOutputEncoding = OemEncoding;
        info.StandardErrorEncoding = OemEncoding;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        // Drain both pipes so a chatty child can never block on a full buffer.
        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            throw;
        }
        return new ProcessResult(process.ExitCode, Tail(await stdout + "\n" + await stderr));
    }

    public void StartDetached(string exe, string args)
    {
        // Not redirected and not waited for: the child must keep running after this process exits. It starts in the system folder: a
        // current folder inherited from here would stay locked, even against the removal helper's own rmdir.
        var info = CreateStartInfo(exe, args);
        info.WorkingDirectory = Environment.SystemDirectory;
        using var process = Process.Start(info);
    }

    public void StartShell(string exe, string args) => StartShellDetached(exe, args);

    /// <summary>
    /// The one way a tray gets started from code (install, and a tray restarting into an upgraded exe), matching the Run value at logon.
    /// ShellExecute hands the child none of this process's handles: a <c>CreateProcess</c> child (<c>UseShellExecute = false</c>) inherits
    /// every inheritable one, so a tray started by <c>$x = &amp; exe install</c> would keep the captured stdout open until sign-out and the
    /// caller would never finish. The system folder as working directory keeps the tray from locking the folder it was started from.
    /// </summary>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
    public static void StartShellDetached(string exe, string args)
    {
        using var process = Process.Start(ShellStartInfo(exe, args));
    }

    internal static ProcessStartInfo ShellStartInfo(string exe, string args) => new(Resolve(exe), args)
    {
        UseShellExecute = true,
        WorkingDirectory = Environment.SystemDirectory,
    };

    /// <summary>The last <see cref="OutputTailLines"/> non-blank lines.</summary>
    internal static string Tail(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('\n', lines.Length <= OutputTailLines ? lines : lines[^OutputTailLines..]);
    }

    private static ProcessStartInfo CreateStartInfo(string exe, string args) => new(Resolve(exe), args)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    private static string Resolve(string exe)
    {
        if (Path.GetDirectoryName(exe)?.Length > 0)
        {
            return exe;
        }
        var system = Environment.SystemDirectory;
        return string.Equals(exe, "powershell.exe", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(system, "WindowsPowerShell", "v1.0", exe)
            : Path.Combine(system, exe);
    }
}
