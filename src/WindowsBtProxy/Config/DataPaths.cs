namespace WindowsBtProxy.Config;

/// <summary>Where the service keeps its files. Pure path arithmetic: creating the directories is up to whoever writes into them.</summary>
public sealed record DataPaths(string Root)
{
    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string KeyFile => Path.Combine(Root, "noise.key");

    public string LogDir => Path.Combine(Root, "logs");

    /// <summary><c>%ProgramData%\WindowsBtProxyForHA</c>.</summary>
    public static DataPaths Default { get; } = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WindowsBtProxyForHA"));
}
