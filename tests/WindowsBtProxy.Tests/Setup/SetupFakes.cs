using WindowsBtProxy.Config;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

/// <summary>Records every command line instead of running it; a handler decides the exit code (default 0).</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<string> Commands { get; } = [];

    public List<string> Detached { get; } = [];

    public Func<string, int>? Handler { get; set; }

    public Task<int> RunAsync(string exe, string args, CancellationToken ct)
    {
        var line = $"{exe} {args}";
        Commands.Add(line);
        return Task.FromResult(Handler?.Invoke(line) ?? 0);
    }

    public void StartDetached(string exe, string args) => Detached.Add($"{exe} {args}");

    public int IndexOf(string prefix) => Commands.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));

    public string Single(string prefix) => Commands.Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
}

internal sealed class FakeElevation(bool elevated) : IElevation
{
    public bool IsElevated { get; } = elevated;

    public List<string[]> Relaunches { get; } = [];

    public int RelaunchExitCode { get; set; } = 7;

    public Task<int> RelaunchElevatedAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        Relaunches.Add(args);
        return Task.FromResult(RelaunchExitCode);
    }
}

/// <summary>Temp folders standing in for Program Files and ProgramData, and a fake "running exe" to install.</summary>
internal sealed class SetupSandbox : IDisposable
{
    public SetupSandbox()
    {
        Root = Directory.CreateTempSubdirectory("setup-tests-").FullName;
        Directory.CreateDirectory(Path.Combine(Root, "download"));
        SourceExe = Path.Combine(Root, "download", "windows-bt-proxy.exe");
        File.WriteAllText(SourceExe, "new build");
    }

    public string Root { get; }

    public string SourceExe { get; }

    // Spaces on purpose: the real Program Files has one, and the quoting has to survive it.
    public string ProgramDir => Path.Combine(Root, "Program Files", "WindowsBtProxyForHA");

    public DataPaths Data => new(Path.Combine(Root, "ProgramData", "WindowsBtProxyForHA"));

    public InstallPaths Paths(string? sourceExe = null) => new(ProgramDir, Data, ApplyDataDirAcl: false, SourceExe: sourceExe ?? SourceExe);

    public SetupContext Context(FakeProcessRunner runner, StringWriter output, bool elevated = true, InstallPaths? paths = null, FakeElevation? elevation = null)
        => new(runner, paths ?? Paths(), elevation ?? new FakeElevation(elevated), output, new SetupConsoleLogger(output)) { ApplyKeyAcl = false };

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
