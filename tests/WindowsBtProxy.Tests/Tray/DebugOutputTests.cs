using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class DebugOutputTests
{
    [Fact]
    public void Write_reaches_OutputDebugString_without_throwing()
    {
        // The tray only calls it from catch blocks, where a missing entry point would go unnoticed; bind it here instead.
        DebugOutput.Write("DebugOutputTests: hello from the test run");
    }
}
