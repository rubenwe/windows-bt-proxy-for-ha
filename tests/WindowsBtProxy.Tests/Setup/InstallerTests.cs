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
            "powershell.exe -NoProfile -NonInteractive -Command \"try { if (Get-NetConnectionProfile -ErrorAction Stop | Where-Object NetworkCategory -eq 'Public') { exit 1 } else { exit 0 } } catch { exit 2 }\"",
            _runner.Single("powershell.exe"));
        var text = _output.ToString();
        Assert.Contains("warning:", text);
        Assert.Contains("Public", text);
        Assert.Contains("Private and Domain", text);
        Assert.Contains("Private network", text);
    }

    [Fact]
    public async Task Install_says_so_when_the_network_check_itself_fails_instead_of_claiming_Public()
    {
        _runner.Handler = line => line.StartsWith("powershell.exe", StringComparison.Ordinal) ? 2 : 0;

        await NewInstaller().InstallAsync(CancellationToken.None);

        var text = _output.ToString();
        Assert.Contains("Could not check the network profile", text);
        Assert.DoesNotContain("warning:", text);
        Assert.DoesNotContain("set to Public", text);
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

        var installer = NewInstaller(_box.Paths(sourceExe: InstalledExe));
        await installer.UninstallAsync(keepSettings: false, CancellationToken.None);

        Assert.True(File.Exists(InstalledExe)); // the running exe cannot delete itself
        Assert.False(File.Exists(Path.Combine(_box.ProgramDir, "leftover.txt")));
        Assert.False(Directory.Exists(_box.Data.Root));
        var detached = Assert.Single(_runner.Detached);
        // Retries once a second for up to a minute, until the folder is gone, instead of a fixed delay.
        Assert.StartsWith("cmd.exe /c for /l %i in (1,1,60) do @if exist ", detached);
        Assert.Contains($"if exist \"{_box.ProgramDir}\" (rmdir /s /q \"{_box.ProgramDir}\" 2>nul & ping -n 2 127.0.0.1 >nul)", detached);
        Assert.True(installer.SelfRemovalScheduled);
    }

    [Fact]
    public async Task The_scheduled_removal_command_really_removes_the_folder_once_the_program_file_is_released()
    {
        // Runs the real detached command (cmd.exe, inside the temp sandbox only) while a file in the folder is held open, like the running exe.
        var installed = Path.Combine(_box.ProgramDir, "windows-bt-proxy.exe");
        Directory.CreateDirectory(_box.ProgramDir);
        File.WriteAllText(installed, "x");
        var hybrid = new CmdRunningRunner(_runner);
        var held = new FileStream(installed, FileMode.Open, FileAccess.Read, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(1500), TestContext.Current.CancellationToken);
            held.Dispose();
        }, TestContext.Current.CancellationToken);

        await new Installer(hybrid, _box.Paths(sourceExe: installed), new SetupConsoleLogger(_output)).UninstallAsync(keepSettings: true, CancellationToken.None);
        await release;

        Assert.False(Directory.Exists(_box.ProgramDir));
    }

    private sealed class CmdRunningRunner(FakeProcessRunner fake) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct) => fake.RunAsync(exe, args, ct);

        public void StartDetached(string exe, string args)
            => new ProcessRunner().RunAsync(exe, args, CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task Uninstall_not_from_the_installed_exe_removes_the_folder_itself_and_schedules_nothing()
    {
        SeedInstall();
        var installer = NewInstaller();

        await installer.UninstallAsync(keepSettings: true, CancellationToken.None);

        Assert.Empty(_runner.Detached);
        Assert.False(installer.SelfRemovalScheduled);
    }

    [Theory]
    [InlineData("Other")]
    [InlineData("WindowsBtProxyForHA & calc")]
    public async Task Uninstall_does_not_schedule_removal_for_an_unexpected_folder_name(string leaf)
    {
        var dir = Path.Combine(_box.Root, "pf", leaf);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "windows-bt-proxy.exe");
        File.WriteAllText(exe, "x");
        var installer = NewInstaller(_box.Paths(sourceExe: exe, programDir: dir));

        await installer.UninstallAsync(keepSettings: true, CancellationToken.None);

        Assert.Empty(_runner.Detached);
        Assert.False(installer.SelfRemovalScheduled);
        Assert.Contains("warning: Not scheduling removal", _output.ToString());
    }

    [Theory]
    [InlineData("pct%dir")]
    [InlineData("a^b")]
    [InlineData("a&b")]
    public async Task Uninstall_does_not_schedule_removal_when_a_parent_folder_has_command_line_metacharacters(string parent)
    {
        // The leaf name is right, so only the metacharacter can be what stops it.
        var dir = Path.Combine(_box.Root, parent, "WindowsBtProxyForHA");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "windows-bt-proxy.exe");
        File.WriteAllText(exe, "x");
        var installer = NewInstaller(_box.Paths(sourceExe: exe, programDir: dir));

        await installer.UninstallAsync(keepSettings: true, CancellationToken.None);

        Assert.Empty(_runner.Detached);
        Assert.False(installer.SelfRemovalScheduled);
    }

    [Fact]
    public async Task Uninstall_removes_the_firewall_rule_of_the_default_port_too()
    {
        SeedInstall(port: 7000);

        await NewInstaller().UninstallAsync(keepSettings: true, CancellationToken.None);

        Assert.Equal(
            [
                "netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 7000)\"",
                "netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 6053)\"",
            ],
            _runner.Commands.Where(c => c.StartsWith("netsh.exe", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Uninstall_issues_exactly_this_command_sequence()
    {
        SeedInstall();

        await NewInstaller().UninstallAsync(keepSettings: true, CancellationToken.None);

        Assert.Equal(
            [
                "net.exe stop WindowsBtProxyForHA",
                "sc.exe delete WindowsBtProxyForHA",
                "netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 6053)\"",
            ],
            _runner.Commands);
    }

    [Fact]
    public async Task Install_issues_exactly_this_command_sequence()
    {
        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Equal(
            [
                "net.exe stop WindowsBtProxyForHA",
                $"sc.exe create WindowsBtProxyForHA binPath= \"\\\"{InstalledExe}\\\" service\" start= auto DisplayName= \"Windows BT Proxy for Home Assistant\"",
                "sc.exe description WindowsBtProxyForHA \"Passive Bluetooth LE proxy for Home Assistant (ESPHome-compatible API).\"",
                "sc.exe failure WindowsBtProxyForHA reset= 86400 actions= restart/5000/restart/5000/restart/30000",
                "netsh.exe advfirewall firewall delete rule name=\"Windows BT Proxy for Home Assistant (TCP 6053)\"",
                $"netsh.exe advfirewall firewall add rule name=\"Windows BT Proxy for Home Assistant (TCP 6053)\" dir=in action=allow protocol=TCP localport=6053 profile=private,domain program=\"{InstalledExe}\"",
                "sc.exe start WindowsBtProxyForHA",
                "powershell.exe -NoProfile -NonInteractive -Command \"try { if (Get-NetConnectionProfile -ErrorAction Stop | Where-Object NetworkCategory -eq 'Public') { exit 1 } else { exit 0 } } catch { exit 2 }\"",
            ],
            _runner.Commands);
    }

    [Fact]
    public async Task Install_with_a_changed_port_also_removes_the_default_ports_old_rule()
    {
        SeedInstall(port: 7000);

        await NewInstaller().InstallAsync(CancellationToken.None);

        var deletes = _runner.Commands.Where(c => c.StartsWith("netsh.exe advfirewall firewall delete rule", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, deletes.Count);
        Assert.Contains("(TCP 7000)", deletes[0]);
        Assert.Contains("(TCP 6053)", deletes[1]);
    }

    // ---- hardening of the data folder ----

    [Fact]
    public async Task Install_refuses_a_data_dir_owned_by_a_non_administrator_before_changing_anything()
    {
        SeedInstall();
        _box.Inspector.Owners[_box.Data.Root] = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Equal($"{_box.Data.Root} was created by a non-administrator; delete it and run install again.", e.Message);
        Assert.Empty(_runner.Commands);
        Assert.Equal("old build", File.ReadAllText(InstalledExe));
    }

    [Fact]
    public async Task Install_refuses_a_pre_placed_child_owned_by_a_non_administrator()
    {
        SeedInstall();
        _box.Inspector.Owners[_box.Data.KeyFile] = new System.Security.Principal.SecurityIdentifier("S-1-5-21-1-2-3-1001");

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Contains(_box.Data.KeyFile, e.Message);
        Assert.Contains("non-administrator", e.Message);
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task Install_accepts_system_and_administrator_owned_data()
    {
        SeedInstall();
        _box.Inspector.Owners[_box.Data.KeyFile] = new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null);

        await NewInstaller().InstallAsync(CancellationToken.None);

        Assert.Contains(_runner.Commands, c => c.StartsWith("sc.exe create", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Install_refuses_a_link_as_the_data_dir_or_inside_it(bool inside)
    {
        SeedInstall();
        Directory.CreateDirectory(Path.Combine(_box.Data.Root, "logs"));
        _box.Inspector.Links.Add(inside ? Path.Combine(_box.Data.Root, "logs") : _box.Data.Root);

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Contains("link", e.Message);
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public void FileSystemInspector_sees_a_junction_as_a_link_and_a_plain_folder_as_not()
    {
        var plain = Directory.CreateDirectory(Path.Combine(_box.Root, "plain")).FullName;
        var junction = Path.Combine(_box.Root, "junction");
        // A junction in a temp folder: no privileges needed, no effect outside the test folder.
        var made = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{plain}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        made!.WaitForExit();
        Assert.Equal(0, made.ExitCode);

        Assert.False(FileSystemInspector.Instance.IsReparsePoint(plain));
        Assert.True(FileSystemInspector.Instance.IsReparsePoint(junction));
        Assert.NotNull(FileSystemInspector.Instance.GetOwner(plain));
    }

    [Fact]
    public void DataDirGuard_ignores_a_folder_that_does_not_exist_yet()
    {
        DataDirGuard.Verify(Path.Combine(_box.Root, "nope"), _box.Inspector);
    }

    // ---- tool output in error messages ----

    [Fact]
    public async Task A_failed_sc_step_includes_what_sc_said_and_the_helpmsg_hint()
    {
        _runner.Handler = line => line.StartsWith("sc.exe create", StringComparison.Ordinal) ? 1072 : 0;
        _runner.OutputHandler = line => line.StartsWith("sc.exe create", StringComparison.Ordinal) ? "[SC] CreateService FAILED 1072:\nThe specified service has been marked for deletion." : "";

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Contains("sc.exe exited with code 1072", e.Message);
        Assert.Contains("net helpmsg 1072", e.Message);
        Assert.Contains("[SC] CreateService FAILED 1072:", e.Message);
        Assert.Contains("marked for deletion", e.Message);
    }

    [Fact]
    public async Task A_failed_netsh_step_includes_netsh_output_but_no_helpmsg_hint()
    {
        _runner.Handler = line => line.StartsWith("netsh.exe advfirewall firewall add", StringComparison.Ordinal) ? 1 : 0;
        _runner.OutputHandler = line => line.StartsWith("netsh.exe advfirewall firewall add", StringComparison.Ordinal) ? "The requested operation requires elevation." : "";

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.Contains("netsh.exe exited with code 1", e.Message);
        Assert.Contains("requires elevation", e.Message);
        Assert.DoesNotContain("helpmsg", e.Message);
    }

    [Fact]
    public async Task A_failure_without_output_has_no_dangling_output_section()
    {
        _runner.Handler = line => line.StartsWith("sc.exe failure", StringComparison.Ordinal) ? 5 : 0;

        var e = await Assert.ThrowsAsync<SetupException>(() => NewInstaller().InstallAsync(CancellationToken.None));

        Assert.DoesNotContain("said", e.Message);
    }

    public void Dispose() => _box.Dispose();
}
