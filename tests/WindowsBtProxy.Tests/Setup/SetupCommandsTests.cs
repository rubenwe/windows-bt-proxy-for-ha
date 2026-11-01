using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

public sealed class SetupCommandsTests : IDisposable
{
    private readonly SetupSandbox _box = new();
    private readonly FakeProcessRunner _runner = new();
    private readonly StringWriter _output = new();

    private Task<int> Run(string[] args, bool elevated = true, FakeElevation? elevation = null)
        => SetupCommands.RunAsync(args, _box.Context(_runner, _output, elevated, elevation: elevation), CancellationToken.None);

    [Theory]
    [InlineData]
    [InlineData("service")]
    [InlineData("--version")]
    [InlineData("keys")]
    [InlineData("installer")]
    public void TryRunAsync_returns_null_for_anything_else(params string[] args)
    {
        Assert.Null(SetupCommands.TryRunAsync(args, CancellationToken.None));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("INSTALL")]
    [InlineData("Uninstall")]
    [InlineData("key")]
    [InlineData("KEY")]
    public void IsSetupVerb_accepts_the_three_verbs_in_any_case(string verb)
    {
        Assert.True(SetupCommands.IsSetupVerb([verb, "extra"]));
    }

    [Fact]
    public async Task Install_when_not_elevated_relaunches_and_returns_the_childs_exit_code()
    {
        var elevation = new FakeElevation(elevated: false) { RelaunchExitCode = 9 };

        var code = await Run(["install"], elevation: elevation);

        Assert.Equal(9, code);
        Assert.Equal(["install"], Assert.Single(elevation.Relaunches));
        Assert.Empty(_runner.Commands);
        Assert.False(Directory.Exists(_box.ProgramDir));
    }

    [Fact]
    public async Task Uninstall_keep_settings_when_not_elevated_relaunches_with_the_same_args()
    {
        var elevation = new FakeElevation(elevated: false);

        await Run(["uninstall", "--keep-settings"], elevation: elevation);

        Assert.Equal(["uninstall", "--keep-settings"], Assert.Single(elevation.Relaunches));
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task Install_when_elevated_runs_the_installer()
    {
        var code = await Run(["install"]);

        Assert.Equal(0, code);
        Assert.Contains(_runner.Commands, c => c.StartsWith("sc.exe create", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uninstall_keep_settings_when_elevated_keeps_the_data_dir()
    {
        Directory.CreateDirectory(_box.Data.Root);
        File.WriteAllText(_box.Data.SettingsFile, "{}");

        var code = await Run(["uninstall", "--keep-settings"]);

        Assert.Equal(0, code);
        Assert.True(File.Exists(_box.Data.SettingsFile));
    }

    [Fact]
    public async Task A_failing_step_prints_the_error_and_returns_1()
    {
        _runner.Handler = line => line.StartsWith("sc.exe create", StringComparison.Ordinal) ? 5 : 0;

        var code = await Run(["install"]);

        Assert.Equal(1, code);
        Assert.Contains("error: Could not create the service", _output.ToString());
    }

    [Theory]
    [InlineData("install", "now")]
    [InlineData("uninstall", "--purge")]
    [InlineData("key")]
    [InlineData("key", "export")]
    [InlineData("key", "show", "extra")]
    public async Task Bad_arguments_print_usage_and_return_2_without_touching_anything(params string[] args)
    {
        var code = await Run(args);

        Assert.Equal(2, code);
        Assert.Contains("Usage:", _output.ToString());
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task Relaunched_copy_waits_for_a_key_press_at_the_end_and_the_flag_is_not_an_argument()
    {
        var waited = 0;
        var context = _box.Context(_runner, _output) with { WaitForKey = () => waited++ };

        var code = await SetupCommands.RunAsync(["install", "--relaunched"], context, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(1, waited);
    }

    [Fact]
    public async Task A_normal_run_does_not_wait_for_a_key_press()
    {
        var waited = 0;
        var context = _box.Context(_runner, _output) with { WaitForKey = () => waited++ };

        await SetupCommands.RunAsync(["install"], context, CancellationToken.None);

        Assert.Equal(0, waited);
    }

    // ---- key ----

    [Theory]
    [InlineData("show")]
    [InlineData("rotate")]
    [InlineData("clear")]
    public async Task Key_commands_need_an_elevated_terminal_and_never_relaunch(string sub)
    {
        Directory.CreateDirectory(_box.Data.Root);
        var elevation = new FakeElevation(elevated: false);

        var code = await Run(["key", sub], elevation: elevation);

        Assert.Equal(1, code);
        Assert.Equal("Run this from an elevated terminal (Run as administrator)." + Environment.NewLine, _output.ToString());
        Assert.Empty(elevation.Relaunches);
        Assert.Empty(_runner.Commands);
        Assert.False(File.Exists(_box.Data.KeyFile));
    }

    [Fact]
    public async Task Key_show_without_a_key_says_ha_will_provision_one()
    {
        Directory.CreateDirectory(_box.Data.Root);

        var code = await Run(["key", "show"]);

        Assert.Equal(0, code);
        Assert.Contains("no key — HA will provision one when you add the proxy", _output.ToString());
    }

    [Fact]
    public async Task Key_show_prints_the_stored_key_as_base64()
    {
        Directory.CreateDirectory(_box.Data.Root);
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        new WindowsBtProxy.Crypto.KeyStore(_box.Data.KeyFile, applyAcl: false).Save(key);

        var code = await Run(["key", "show"]);

        Assert.Equal(0, code);
        Assert.Equal(Convert.ToBase64String(key) + Environment.NewLine, _output.ToString());
    }

    [Fact]
    public async Task Key_show_reports_a_corrupt_key_file()
    {
        Directory.CreateDirectory(_box.Data.Root);
        File.WriteAllText(_box.Data.KeyFile, "garbage");

        var code = await Run(["key", "show"]);

        Assert.Equal(1, code);
        Assert.Contains("error:", _output.ToString());
        Assert.DoesNotContain("garbage", _output.ToString());
    }

    [Fact]
    public async Task Key_rotate_saves_a_new_random_key_and_prints_it()
    {
        Directory.CreateDirectory(_box.Data.Root);
        _runner.Handler = line => line.StartsWith("net.exe stop", StringComparison.Ordinal) ? 2 : 0; // not running

        var code = await Run(["key", "rotate"]);

        var saved = new WindowsBtProxy.Crypto.KeyStore(_box.Data.KeyFile, applyAcl: false).Load();
        Assert.Equal(0, code);
        Assert.NotNull(saved);
        Assert.Equal(32, saved.Length);
        Assert.Contains(Convert.ToBase64String(saved), _output.ToString());
        Assert.Contains("Home Assistant will ask for this key", _output.ToString());
        Assert.DoesNotContain("net.exe start", string.Join('\n', _runner.Commands)); // it was not running, so it is not started
    }

    [Fact]
    public async Task Key_rotate_twice_gives_different_keys()
    {
        Directory.CreateDirectory(_box.Data.Root);
        var store = new WindowsBtProxy.Crypto.KeyStore(_box.Data.KeyFile, applyAcl: false);

        await Run(["key", "rotate"]);
        var first = store.Load();
        await Run(["key", "rotate"]);

        Assert.NotEqual(first, store.Load());
    }

    [Fact]
    public async Task Key_rotate_restarts_a_running_service()
    {
        Directory.CreateDirectory(_box.Data.Root);

        var code = await Run(["key", "rotate"]);

        Assert.Equal(0, code);
        Assert.Equal(["net.exe stop WindowsBtProxyForHA", "net.exe start WindowsBtProxyForHA"], _runner.Commands);
        Assert.Contains("restarted", _output.ToString());
    }

    [Fact]
    public async Task Key_rotate_without_a_data_dir_asks_for_install_first()
    {
        var code = await Run(["key", "rotate"]);

        Assert.Equal(1, code);
        Assert.Contains("install", _output.ToString());
        Assert.Empty(_runner.Commands);
    }

    [Fact]
    public async Task Key_clear_deletes_the_key_file_and_restarts_a_running_service()
    {
        Directory.CreateDirectory(_box.Data.Root);
        new WindowsBtProxy.Crypto.KeyStore(_box.Data.KeyFile, applyAcl: false).Save(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));

        var code = await Run(["key", "clear"]);

        Assert.Equal(0, code);
        Assert.False(File.Exists(_box.Data.KeyFile));
        Assert.Equal(["net.exe stop WindowsBtProxyForHA", "net.exe start WindowsBtProxyForHA"], _runner.Commands);
    }

    [Fact]
    public async Task Key_clear_with_no_key_file_still_succeeds()
    {
        var code = await Run(["key", "clear"]);

        Assert.Equal(0, code);
    }

    public void Dispose() => _box.Dispose();
}
