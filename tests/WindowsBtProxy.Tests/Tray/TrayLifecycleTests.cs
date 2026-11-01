using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class TrayLifecycleTests
{
    [Theory]
    [InlineData(true, "0.2.0")]
    [InlineData(false, "0.2.0")]
    [InlineData(false, null)]
    public void A_tray_not_running_from_the_install_always_stays(bool runValuePresent, string? installedVersion)
    {
        Assert.Equal(LifecycleAction.Stay, TrayLifecycle.Decide(false, runValuePresent, "0.1.0", installedVersion));
    }

    [Theory]
    [InlineData("0.2.0")]
    [InlineData("0.1.0")]
    [InlineData(null)]
    public void An_installed_tray_exits_when_the_run_value_is_gone(string? installedVersion)
    {
        Assert.Equal(LifecycleAction.Exit, TrayLifecycle.Decide(true, false, "0.2.0", installedVersion));
    }

    [Fact]
    public void An_unknown_installed_version_stays()
    {
        Assert.Equal(LifecycleAction.Stay, TrayLifecycle.Decide(true, true, "0.2.0", null));
    }

    [Fact]
    public void A_different_installed_version_restarts_into_the_install()
    {
        Assert.Equal(LifecycleAction.RestartInto, TrayLifecycle.Decide(true, true, "0.1.0", "0.2.0"));
    }

    [Fact]
    public void A_downgrade_also_restarts_because_any_difference_counts()
    {
        Assert.Equal(LifecycleAction.RestartInto, TrayLifecycle.Decide(true, true, "0.3.0", "0.2.0"));
    }

    [Fact]
    public void The_same_version_stays_so_a_restarted_tray_cannot_loop()
    {
        Assert.Equal(LifecycleAction.Stay, TrayLifecycle.Decide(true, true, "0.2.0", "0.2.0"));
    }

    [Fact]
    public void Versions_are_compared_exactly()
    {
        Assert.Equal(LifecycleAction.RestartInto, TrayLifecycle.Decide(true, true, "0.2.0", "0.2.0+abc"));
        Assert.Equal(LifecycleAction.RestartInto, TrayLifecycle.Decide(true, true, "0.2.0", "0.2.0 "));
    }
}
