using WindowsBtProxy.Config;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

public sealed class InstallerTests : IDisposable
{
    private readonly SetupSandbox _box = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly StringWriter _output = new();

    private Installer NewInstaller(InstallPaths? paths = null) => new(_runner, paths ?? _box.Paths(), new SetupConsoleLogger(_output));

    private string InstalledExe => Path.Combine(_box.ProgramDir, "windows-bt-proxy.exe");

    private void SeedInstall(int port = 6053)
    {
        Directory.CreateDirectory(_box.ProgramDir);
        File.WriteAllText(InstalledExe, "old build");
        File.WriteAllText(Path.Combine(_box.ProgramDir, "leftover.txt"), "x");
        Directory.CreateDirectory(_box.Data.Root);
        File.WriteAllText(_box.Data.SettingsFile, $"{{ \"name\": \"bt-proxy-test\", \"friendlyName\": \"Test\", \"port\": {port} }}");
        File.WriteAllText(_box.Data.KeyFile, "key");
    }

    [Fact]
    public async Task Install_runs_sc_create_with_auto_start_and_service_arg()
    {
        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(
            $"sc.exe create WindowsBtProxyForHA binPath= \"\\\"{InstalledExe}\\\" service\" start= auto DisplayName= \"Windows BT Proxy for Home Assistant\"",
            _runner.Single("sc.exe create"));
        Assert.StartsWith("sc.exe description WindowsBtProxyForHA \"", _runner.Single("sc.exe description"));
        Assert.Contains("sc.exe start WindowsBtProxyForHA", _runner.Commands);
        Assert.Equal("new build", File.ReadAllText(InstalledExe));
        Assert.True(File.Exists(_box.Data.SettingsFile));
    }

    [Fact]
    public async Task Install_sets_restart_on_failure()
    {
        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(
            "sc.exe failure WindowsBtProxyForHA reset= 86400 actions= restart/5000/restart/5000/restart/30000",
            _runner.Single("sc.exe failure"));
    }

    [Fact]
    public async Task Install_replaces_firewall_rule_for_private_and_domain_only()
    {
        SeedInstall(port: 7000);

        await NewInstaller().InstallAsync(CancellationToken.None);

        var deleteAt = _runner.IndexOf("netsh.exe advfirewall firewall delete rule");
        var addAt = _runner.IndexOf("netsh.exe advfirewall firewall add rule");
        Assert.InRange(deleteAt, 0, addAt - 1);
        Assert.Equal("netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 7000)\"", _runner.Commands[deleteAt]);
        Assert.Equal(
            "netsh.exe advfirewall firewall add rule name=\"Windows BT Proxy for Home Assistant (TCP 7000)\" dir=in action=allow protocol=TCP localport=7000 profile=private,domain program=\"" + InstalledExe + "\"",
            _runner.Commands[addAt]);
        Assert.DoesNotContain("public", _runner.Commands[addAt], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Install_over_existing_stops_service_before_copy()
    {
        SeedInstall();
        string? exeWhenStopped = null;
        _runner.Handler = line =>
        {
            if (line.StartsWith("net.exe stop", StringComparison.Ordinal))
            {
                exeWhenStopped = File.ReadAllText(InstalledExe);
            }
            return 0;
        };

        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal("net.exe stop WindowsBtProxyForHA", _runner.Commands[0]);
        Assert.Equal("old build", exeWhenStopped); // still the old file when the service was stopped, i.e. stop came before the copy
        Assert.Equal("new build", File.ReadAllText(InstalledExe));
        Assert.True(_runner.IndexOf("net.exe stop") < _runner.IndexOf("sc.exe create"));
    }

    [Fact]
    public async Task Install_twice_updates_the_existing_service_instead_of_failing()
    {
        _runner.Handler = line => line.StartsWith("sc.exe create", StringComparison.Ordinal) ? 1073 : 0;

        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.StartsWith("sc.exe config WindowsBtProxyForHA binPath= ", _runner.Single("sc.exe config"));
        Assert.Contains("sc.exe start WindowsBtProxyForHA", _runner.Commands);
    }

    [Fact]
    public async Task Install_from_the_installed_path_does_not_copy_onto_itself()
    {
        SeedInstall();

        await NewInstaller(_box.Paths(sourceExe: InstalledExe)).InstallAsync(CancellationToken.None);

        Assert.Equal("old build", File.ReadAllText(InstalledExe));
        Assert.Contains(_runner.Commands, c => c.StartsWith("sc.exe create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_keeps_an_existing_settings_file()
    {
        SeedInstall(port: 7000);

        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Contains("bt-proxy-test", File.ReadAllText(_box.Data.SettingsFile));
        Assert.Equal("key", File.ReadAllText(_box.Data.KeyFile));
    }

    [Fact]
    public async Task Install_failing_to_create_the_service_reports_the_exit_code()
    {
        _runner.Handler = line => line.StartsWith("sc.exe create", StringComparison.Ordinal) ? 5 : 0;

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Contains("create the service", e.Message);
        Assert.Contains("5", e.Message);
        Assert.DoesNotContain(_runner.Commands, c => c.StartsWith("sc.exe start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_reports_a_service_that_will_not_start_after_finishing_the_rest()
    {
        _runner.Handler = line => line.StartsWith("sc.exe start", StringComparison.Ordinal) ? 1053 : 0;

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Contains("start the service", e.Message);
        Assert.Contains(_runner.Commands, c => c.StartsWith("netsh.exe advfirewall firewall add rule", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Install_warns_when_a_network_is_public_but_still_succeeds()
    {
        _runner.Handler = line => line.StartsWith("powershell.exe", StringComparison.Ordinal) ? 1 : 0;

        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(
            "powershell.exe -NoProfile -NonInteractive -Command \"if (Get-NetConnectionProfile | Where-Object NetworkCategory -eq 'Public') { exit 1 } else { exit 0 }\"",
            _runner.Single("powershell.exe"));
        var text = _output.ToString();
        Assert.Contains("warning:", text);
        Assert.Contains("Public", text);
        Assert.Contains("Private and Domain", text);
        Assert.Contains("Private network", text);
    }

    [Fact]
    public async Task Install_does_not_warn_when_no_network_is_public()
    {
        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.DoesNotContain("warning:", _output.ToString());
    }

    [Fact]
    public async Task Uninstall_removes_service_rule_and_files()
    {
        SeedInstall(port: 7000);

        await NewInstaller().UninstallAsync(keepSettings: false, CancellationToken.None);

        Assert.True(_runner.IndexOf("net.exe stop WindowsBtProxyForHA") < _runner.IndexOf("sc.exe delete WindowsBtProxyForHA"));
        Assert.Contains("netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 7000)\"", _runner.Commands);
        Assert.False(Directory.Exists(_box.ProgramDir));
        Assert.False(Directory.Exists(_box.Data.Root));
        Assert.Empty(_runner.Detached);
    }

    [Fact]
    public async Task Uninstall_keep_settings_leaves_data_dir()
    {
        SeedInstall();

        await NewInstaller().UninstallAsync(keepSettings: true, CancellationToken.None);

        Assert.False(Directory.Exists(_box.ProgramDir));
        Assert.True(File.Exists(_box.Data.SettingsFile));
        Assert.True(File.Exists(_box.Data.KeyFile));
    }

    [Fact]
    public async Task Uninstall_when_nothing_is_installed_is_a_no_op()
    {
        _runner.Handler = line => line.StartsWith("sc.exe delete", StringComparison.Ordinal) ? 1060 : 2;

        await NewInstaller().UninstallAsync(keepSettings: false, CancellationToken.None);

        Assert.Contains("is not installed", _output.ToString());
        Assert.DoesNotContain("warning:", _output.ToString());
        Assert.False(Directory.Exists(_box.Data.Root)); // uninstall must not create the settings it is about to delete
        Assert.Contains("netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 6053)\"", _runner.Commands);
    }

    [Fact]
    public async Task Uninstall_stops_before_touching_files_when_the_service_cannot_be_deleted()
    {
        SeedInstall();
        _runner.Handler = line => line.StartsWith("sc.exe delete", StringComparison.Ordinal) ? 5 : 0;

        await Assert.ThrowsAsync<SetupException>(() => NewInstaller().UninstallAsync(keepSettings: false, CancellationToken.None));

        Assert.True(File.Exists(InstalledExe));
        Assert.True(File.Exists(_box.Data.SettingsFile));
    }

    [Fact]
    public async Task Uninstall_from_the_installed_exe_removes_everything_else_and_schedules_the_rest()
    {
        SeedInstall();

        await NewInstaller(_box.Paths(sourceExe: InstalledExe)).UninstallAsync(keepSettings: false, CancellationToken.None);

        Assert.True(File.Exists(InstalledExe)); // the running exe cannot delete itself
        Assert.False(File.Exists(Path.Combine(_box.ProgramDir, "leftover.txt")));
        Assert.False(Directory.Exists(_box.Data.Root));
        var detached = Assert.Single(_runner.Detached);
        Assert.StartsWith("cmd.exe /c ping -n ", detached);
        Assert.EndsWith($"& rmdir /s /q \"{_box.ProgramDir}\"", detached);
    }

    public void Dispose() => _box.Dispose();
}
