using WindowsBtProxy.Hosting;

namespace WindowsBtProxy.Tests.Hosting;

public sealed class ConsoleAttachTests
{
    [Theory]
    [InlineData("tray")]
    [InlineData("tray", "--show-key")]
    [InlineData("TRAY")]
    [InlineData("Tray", "--Show-Key")]
    [InlineData("service")]
    [InlineData("Service")]
    public void Tray_and_service_need_no_console(params string[] args)
    {
        Assert.False(ConsoleAttach.NeedsConsole(args));
    }

    [Theory]
    [InlineData("run")]
    [InlineData("run", "--no-mdns")]
    [InlineData("install")]
    [InlineData("uninstall", "--keep-settings")]
    [InlineData("key", "show")]
    [InlineData("version")]
    [InlineData("bogus")]
    [InlineData("--show-key", "tray")]
    public void Every_other_verb_needs_a_console(params string[] args)
    {
        Assert.True(ConsoleAttach.NeedsConsole(args));
    }

    [Fact]
    public void No_arguments_need_a_console_because_the_usage_is_printed()
    {
        Assert.True(ConsoleAttach.NeedsConsole([]));
    }
}
