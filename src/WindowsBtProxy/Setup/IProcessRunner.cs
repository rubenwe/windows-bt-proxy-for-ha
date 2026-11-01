namespace WindowsBtProxy.Setup;

/// <summary>Runs external programs (sc.exe, netsh.exe, net.exe, ...) so install logic can be tested without touching the machine.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="exe"/> with the raw command line <paramref name="args"/> and waits for it. Returns the exit code; never throws for a non-zero code.</summary>
    Task<int> RunAsync(string exe, string args, CancellationToken ct);

    /// <summary>Starts <paramref name="exe"/> in the background, with no window, and returns without waiting. Used for the clean-up that has to outlive this process.</summary>
    void StartDetached(string exe, string args);
}
