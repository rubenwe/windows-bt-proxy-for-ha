using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace WindowsBtProxy.Setup;

/// <summary>Whether this process is an administrator, and how to get one that is (UAC prompt).</summary>
public interface IElevation
{
    bool IsElevated { get; }

    /// <summary>
    /// Runs this program again, elevated, with <paramref name="args"/>, waits, and returns its exit code. If it could not be
    /// started (prompt declined, no program path) a message goes to <paramref name="output"/> and the result is null.
    /// </summary>
    Task<int?> RelaunchElevatedAsync(string[] args, TextWriter output, CancellationToken ct);
}

/// <summary>The real thing: the token's Administrators membership and the <c>runas</c> shell verb.</summary>
public sealed class WindowsElevation : IElevation
{
    private const int ErrorCancelled = 1223;

    public bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public async Task<int?> RelaunchElevatedAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            output.WriteLine("Cannot tell where this program is running from, so it cannot ask for administrator rights. Run it from an elevated terminal (Run as administrator).");
            return null;
        }

        var info = new ProcessStartInfo(exe) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        // The elevated copy runs in its own console window; this flag makes it wait for Enter so its output can be read.
        info.ArgumentList.Add(SetupCommands.RelaunchedFlag);

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                output.WriteLine("Could not start the elevated copy of this program.");
                return null;
            }
            await process.WaitForExitAsync(ct);
            return process.ExitCode;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            output.WriteLine("Administrator rights were declined (the UAC prompt was cancelled), so nothing was changed.");
            return null;
        }
        catch (Win32Exception e)
        {
            output.WriteLine($"Could not ask for administrator rights: {e.Message}");
            return null;
        }
    }
}
