namespace WindowsBtProxy.Tray;

/// <summary>What a running tray should do after its periodic lifecycle check.</summary>
public enum LifecycleAction
{
    /// <summary>Keep running.</summary>
    Stay,

    /// <summary>Quit: the tray is no longer wanted.</summary>
    Exit,

    /// <summary>Start the installed exe and quit: the tray is out of date.</summary>
    RestartInto,
}

/// <summary>
/// The rules that keep the per-user tray in step with the installed service, checked every 30 seconds. Pure, so the Win32 tray only has to
/// gather the four facts and act on the answer.
/// </summary>
public static class TrayLifecycle
{
    /// <summary>Decides whether the tray stays, exits, or restarts into the installed exe.</summary>
    /// <param name="runningFromInstall">This process's full path equals <c>InstallPaths.Default().ExePath</c> (case-insensitive).</param>
    /// <param name="runValuePresent">The autostart <c>Run</c> value exists; it is removed by uninstall and never written by <c>install --no-tray</c>.</param>
    /// <param name="ownVersion">This process's version string.</param>
    /// <param name="installedVersion">The installed exe's version string, or null when the file is missing, mid-copy or unreadable.</param>
    /// <remarks>
    /// Dev builds (not running from the install) are never touched. An unknown installed version waits for the next check rather than
    /// acting on a half-copied file. Versions are compared as exact strings: any difference restarts, and the restarted tray is the installed
    /// exe so its own version equals the installed one, which means the restart cannot loop.
    /// </remarks>
    public static LifecycleAction Decide(bool runningFromInstall, bool runValuePresent, string ownVersion, string? installedVersion)
    {
        if (!runningFromInstall)
        {
            return LifecycleAction.Stay;
        }
        if (!runValuePresent)
        {
            return LifecycleAction.Exit;
        }
        if (installedVersion is null)
        {
            return LifecycleAction.Stay;
        }
        return string.Equals(installedVersion, ownVersion, StringComparison.Ordinal)
            ? LifecycleAction.Stay
            : LifecycleAction.RestartInto;
    }
}
