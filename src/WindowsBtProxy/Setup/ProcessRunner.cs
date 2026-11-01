using System.Diagnostics;

namespace WindowsBtProxy.Setup;

/// <summary>
/// The real <see cref="IProcessRunner"/>. Bare program names are resolved against the Windows system directory, never the
/// current directory or this program's own folder, because the installer runs elevated from wherever the user downloaded it.
/// The child's output is discarded: the exit code is what the installer acts on.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string exe, string args, CancellationToken ct)
    {
        var info = CreateStartInfo(exe, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {exe}.");
        // Drain both pipes so a chatty child can never block on a full buffer.
        var drain = Task.WhenAll(process.StandardOutput.ReadToEndAsync(ct), process.StandardError.ReadToEndAsync(ct));
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
        await drain;
        return process.ExitCode;
    }

    public void StartDetached(string exe, string args)
    {
        // Not redirected and not waited for: the child must keep running after this process exits.
        using var process = Process.Start(CreateStartInfo(exe, args));
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
