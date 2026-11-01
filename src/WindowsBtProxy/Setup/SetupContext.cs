using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Setup;

/// <summary>Everything the setup commands touch, so tests can swap the machine for fakes and temp folders.</summary>
public sealed record SetupContext(
    IProcessRunner Runner,
    InstallPaths Paths,
    IElevation Elevation,
    TextWriter Out,
    ILogger Log)
{
    /// <summary>False in tests, which run as a normal user and cannot use the SYSTEM/Administrators-only ACL on the key file.</summary>
    public bool ApplyKeyAcl { get; init; } = true;

    /// <summary>Called at the end of a command that ran in an elevated copy's own console window, so its output stays readable.</summary>
    public Action? WaitForKey { get; init; }

    public static SetupContext CreateDefault()
    {
        var output = Console.Out;
        return new SetupContext(new ProcessRunner(), InstallPaths.Default(), new WindowsElevation(), output, new SetupConsoleLogger(output))
        {
            WaitForKey = () =>
            {
                output.WriteLine();
                output.Write("Press Enter to close this window. ");
                Console.In.ReadLine();
            },
        };
    }
}
