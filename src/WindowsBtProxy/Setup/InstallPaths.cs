using WindowsBtProxy.Config;

namespace WindowsBtProxy.Setup;

/// <summary>
/// Where <see cref="Installer"/> puts things. <paramref name="ApplyDataDirAcl"/> and <paramref name="SourceExe"/> exist so tests can
/// use temp folders: applying the locked-down ACL to a temp folder would lock the test user out of its own clean-up.
/// </summary>
public sealed record InstallPaths(string ProgramDir, DataPaths Data, bool ApplyDataDirAcl = true, string? SourceExe = null)
{
    public const string ExeName = "windows-bt-proxy.exe";

    /// <summary>The installed copy of the program.</summary>
    public string ExePath => Path.Combine(ProgramDir, ExeName);

    /// <summary>The program that is running now (the one install copies, the one uninstall must not delete from under itself).</summary>
    public string? RunningExe => SourceExe ?? Environment.ProcessPath;

    /// <summary><c>%ProgramFiles%\WindowsBtProxyForHA</c> and <c>%ProgramData%\WindowsBtProxyForHA</c>.</summary>
    public static InstallPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsBtProxyForHA"),
        DataPaths.Default);
}
