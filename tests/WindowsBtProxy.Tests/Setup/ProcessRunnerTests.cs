using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

/// <summary>Only runs cmd.exe with harmless commands; the install commands themselves are never run by tests.</summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_the_exit_code_without_throwing_for_non_zero()
    {
        var runner = new ProcessRunner();

        Assert.Equal(0, await runner.RunAsync("cmd.exe", "/c exit 0", CancellationToken.None));
        Assert.Equal(3, await runner.RunAsync("cmd.exe", "/c exit 3", CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_does_not_block_on_chatty_output()
    {
        var code = await new ProcessRunner().RunAsync("cmd.exe", "/c for /l %i in (1,1,3000) do @echo line %i", CancellationToken.None);

        Assert.Equal(0, code);
    }
}
