using Microsoft.Extensions.Logging;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

public sealed class SetupConsoleLoggerTests
{
    [Fact]
    public void Warnings_and_errors_include_the_exception_message()
    {
        var output = new StringWriter();
        var log = new SetupConsoleLogger(output);

        log.LogWarning(new IOException("disk is full"), "Could not write {File}.", "settings.json");
        log.LogError(new InvalidOperationException("boom"), "Failed.");
        log.LogInformation(new IOException("ignored detail"), "Plain info.");
        log.LogDebug("never shown");

        Assert.Equal(
            $"warning: Could not write settings.json. (disk is full){Environment.NewLine}error: Failed. (boom){Environment.NewLine}Plain info.{Environment.NewLine}",
            output.ToString());
    }
}
