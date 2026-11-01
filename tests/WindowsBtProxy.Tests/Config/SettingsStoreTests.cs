using WindowsBtProxy.Config;
using WindowsBtProxy.Tests.Api;

namespace WindowsBtProxy.Tests.Config;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Directory.CreateTempSubdirectory("settings-tests-").FullName, "data");
    private readonly ListLogger _log = new();

    private DataPaths Paths => new(_root);

    [Fact]
    public void DataPaths_layout()
    {
        var paths = new DataPaths(@"C:\x");

        Assert.Equal(@"C:\x\settings.json", paths.SettingsFile);
        Assert.Equal(@"C:\x\noise.key", paths.KeyFile);
        Assert.Equal(@"C:\x\logs", paths.LogDir);
        Assert.EndsWith("WindowsBtProxyForHA", DataPaths.Default.Root);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), DataPaths.Default.Root);
    }

    [Fact]
    public void LoadOrCreate_writes_defaults_when_missing()
    {
        var settings = SettingsStore.LoadOrCreate(Paths, "DESKTOP-ABC123", _log);

        Assert.Equal("bt-proxy-desktop-abc123", settings.Name);
        Assert.Equal("BT Proxy DESKTOP-ABC123", settings.FriendlyName);
        Assert.Equal(6053, settings.Port);
        Assert.Equal(ScanMode.Passive, settings.ScanMode);
        Assert.Null(settings.Interfaces);
        Assert.Equal("info", settings.LogLevel);
        Assert.True(settings.Updates.Check);
        Assert.False(settings.Updates.Auto);

        var json = File.ReadAllText(Paths.SettingsFile);
        Assert.Contains("\"name\": \"bt-proxy-desktop-abc123\"", json);
        Assert.Contains("\"friendlyName\": \"BT Proxy DESKTOP-ABC123\"", json);
        Assert.Contains("\"scanMode\": \"passive\"", json);
        Assert.Contains("\"check\": true", json);
        Assert.Contains("\"auto\": false", json);

        // The written file round-trips, and the machine name no longer matters once it exists.
        var again = SettingsStore.LoadOrCreate(Paths, "OTHER", _log);
        Assert.Equal(settings.Name, again.Name);
        Assert.Equal(settings.FriendlyName, again.FriendlyName);
        Assert.Equal(settings.Updates, again.Updates);
    }

    [Fact]
    public void LoadOrCreate_recovers_from_corrupt_json_and_keeps_bad_copy()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, "{ not json");

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal("bt-proxy-pc1", settings.Name);
        Assert.Equal("{ not json", File.ReadAllText(Paths.SettingsFile + ".bad"));
        Assert.Contains("\"port\": 6053", File.ReadAllText(Paths.SettingsFile));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning:"));
    }

    [Fact]
    public void LoadOrCreate_keeps_user_values()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, """
            {
              "name": "kitchen",
              "friendlyName": "Kitchen PC",
              "port": 7000,
              "scanMode": "active",
              "interfaces": ["AA:BB:CC:DD:EE:FF"],
              "logLevel": "debug",
              "updates": { "check": false, "auto": true },
              "somethingNew": 1
            }
            """);

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal("kitchen", settings.Name);
        Assert.Equal("Kitchen PC", settings.FriendlyName);
        Assert.Equal(7000, settings.Port);
        Assert.Equal(ScanMode.Active, settings.ScanMode);
        Assert.Equal(["AA:BB:CC:DD:EE:FF"], settings.Interfaces!);
        Assert.Equal("debug", settings.LogLevel);
        Assert.False(settings.Updates.Check);
        Assert.True(settings.Updates.Auto);
        Assert.False(File.Exists(Paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void LoadOrCreate_fills_missing_fields_with_defaults_without_rewriting_the_file()
    {
        Directory.CreateDirectory(_root);
        const string original = "{ \"port\": 7000 }";
        File.WriteAllText(Paths.SettingsFile, original);

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal(7000, settings.Port);
        Assert.Equal("bt-proxy-pc1", settings.Name);
        Assert.Equal("BT Proxy PC1", settings.FriendlyName);
        Assert.Equal(ScanMode.Passive, settings.ScanMode);
        Assert.True(settings.Updates.Check);
        Assert.False(settings.Updates.Auto);
        Assert.Equal(original, File.ReadAllText(Paths.SettingsFile));
    }

    [Fact]
    public void LoadOrCreate_handles_partial_updates_object_and_null_values()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, """{ "name": null, "updates": { "auto": true } }""");

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal("bt-proxy-pc1", settings.Name);
        Assert.True(settings.Updates.Check);
        Assert.True(settings.Updates.Auto);
    }

    [Fact]
    public void LoadOrCreate_sanitizes_a_stored_name_and_warns()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, """{ "name": "Küche Proxy" }""");

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal("k-che-proxy", settings.Name);
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning:"));
    }

    [Fact]
    public void LoadOrCreate_treats_a_json_null_document_as_corrupt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, "null");

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal("bt-proxy-pc1", settings.Name);
        Assert.True(File.Exists(Paths.SettingsFile + ".bad"));
    }

    [Fact]
    public void LoadOrCreate_replaces_an_out_of_range_port_with_the_default()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, """{ "port": 99999 }""");

        Assert.Equal(6053, SettingsStore.LoadOrCreate(Paths, "PC1", _log).Port);
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning:"));
    }

    [Fact]
    public void LoadOrCreate_uses_defaults_and_leaves_the_file_alone_when_it_is_locked()
    {
        Directory.CreateDirectory(_root);
        const string original = "{ \"port\": 7000 }";
        File.WriteAllText(Paths.SettingsFile, original);

        ProxySettings settings;
        using (new FileStream(Paths.SettingsFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);
        }

        Assert.Equal(6053, settings.Port);
        Assert.Equal("bt-proxy-pc1", settings.Name);
        Assert.Equal(original, File.ReadAllText(Paths.SettingsFile));
        Assert.False(File.Exists(Paths.SettingsFile + ".bad"));
        Assert.Contains(_log.Lines, l => l.StartsWith("Warning:"));
    }

    [Fact]
    public void LoadOrCreate_does_not_overwrite_a_corrupt_file_it_could_not_move_aside()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SettingsFile, "{ not json");
        // A non-empty directory at the .bad path makes the move fail.
        Directory.CreateDirectory(Paths.SettingsFile + ".bad");
        File.WriteAllText(Path.Combine(Paths.SettingsFile + ".bad", "keep"), "x");

        var settings = SettingsStore.LoadOrCreate(Paths, "PC1", _log);

        Assert.Equal("bt-proxy-pc1", settings.Name);
        Assert.Equal("{ not json", File.ReadAllText(Paths.SettingsFile));
    }

    public void Dispose()
    {
        Directory.Delete(Directory.GetParent(_root)!.FullName, recursive: true);
    }
}
