namespace WindowsBtProxy.Setup;

/// <summary>What a finished program left behind: its exit code and the last lines it printed (stdout then stderr), for error messages.</summary>
public sealed record ProcessResult(int ExitCode, string Output = "");

/// <summary>Runs external programs (sc.exe, netsh.exe, net.exe, ...) so install logic can be tested without touching the machine.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="exe"/> with the raw command line <paramref name="args"/> and waits for it. Never throws for a non-zero exit code.</summary>
    Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct);

    /// <summary>Starts <paramref name="exe"/> in the background, with no window and in the system folder, and returns without waiting. Used for the folder clean-up, which has to outlive this process.</summary>
    void StartDetached(string exe, string args);

    /// <summary>
    /// Starts <paramref name="exe"/> through ShellExecute, the way Explorer and the Run value at logon do: in the system folder, not waited
    /// for, and inheriting no handles. Used for the tray, which lives until sign-out and so must not hold this process's (possibly captured)
    /// output open.
    /// </summary>
    void StartShell(string exe, string args);
}
