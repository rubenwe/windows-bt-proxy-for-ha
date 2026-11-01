using System.Diagnostics;

namespace WindowsBtProxy.Setup;

/// <summary>
/// The real <see cref="IProcessRunner"/>. Bare program names are resolved against the Windows system directory, never the
/// current directory or this program's own folder, because the installer runs elevated from wherever the user downloaded it.
/// The last lines of the child's output are kept so a failure can say what the tool complained about.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public const int OutputTailLines = 10;

    public async Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct)
    {
        var info = CreateStartInfo(exe, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
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
        // Not redirected and not waited for: the child must keep running after this process exits.
        using var process = Process.Start(CreateStartInfo(exe, args));
    }

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
