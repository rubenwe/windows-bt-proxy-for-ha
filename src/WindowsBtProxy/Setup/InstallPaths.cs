using WindowsBtProxy.Config;

namespace WindowsBtProxy.Setup;

/// <summary>
/// Where <see cref="Installer"/> puts things. <paramref name="ApplyDataDirAcl"/>, <paramref name="SourceExe"/> and
/// <paramref name="Inspector"/> exist so tests can use temp folders: applying the locked-down ACL to a temp folder would lock the
/// test user out of its own clean-up, and tests cannot create files owned by other users.
/// </summary>
public sealed record InstallPaths(string ProgramDir, DataPaths Data, bool ApplyDataDirAcl = true, string? SourceExe = null, IDataDirInspector? Inspector = null)
{
    public const string ExeName = "windows-bt-proxy.exe";

    /// <summary>The installed copy of the program.</summary>
    public string ExePath => Path.Combine(ProgramDir, ExeName);

    /// <summary>The program that is running now (the one install copies, the one uninstall must not delete from under itself).</summary>
    public string? RunningExe => SourceExe ?? Environment.ProcessPath;

    public IDataDirInspector DataInspector => Inspector ?? FileSystemInspector.Instance;

    /// <summary><c>%ProgramFiles%\WindowsBtProxyForHA</c> and <c>%ProgramData%\WindowsBtProxyForHA</c>.</summary>
    public static InstallPaths Default() => new(
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsBtProxyForHA"),
        DataPaths.Default);
}
