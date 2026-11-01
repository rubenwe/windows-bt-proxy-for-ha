namespace WindowsBtProxy.Setup;

/// <summary>What a finished program left behind: its exit code and the last lines it printed (stdout then stderr), for error messages.</summary>
public sealed record ProcessResult(int ExitCode, string Output = "");

/// <summary>Runs external programs (sc.exe, netsh.exe, net.exe, ...) so install logic can be tested without touching the machine.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="exe"/> with the raw command line <paramref name="args"/> and waits for it. Never throws for a non-zero exit code.</summary>
    Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct);

    /// <summary>Starts <paramref name="exe"/> in the background, with no window and in the system folder, and returns without waiting. Used for what has to outlive this process: the folder clean-up and the tray.</summary>
    void StartDetached(string exe, string args);
}
