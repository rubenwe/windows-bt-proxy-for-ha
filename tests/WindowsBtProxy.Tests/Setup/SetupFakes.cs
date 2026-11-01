using System.Security.Principal;
using WindowsBtProxy.Config;
using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

/// <summary>Records every command line instead of running it; a handler decides the exit code (default 0) and what the tool "printed".</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public List<string> Commands { get; } = [];

    public List<string> Detached { get; } = [];

    public Func<string, int>? Handler { get; set; }

    public Func<string, string>? OutputHandler { get; set; }

    public Exception? ThrowOnStart { get; set; }

    public Task<ProcessResult> RunAsync(string exe, string args, CancellationToken ct)
    {
        var line = $"{exe} {args}";
        Commands.Add(line);
        if (ThrowOnStart is not null && exe == "net.exe" && args.StartsWith("start", StringComparison.Ordinal))
        {
            throw ThrowOnStart;
        }
        return Task.FromResult(new ProcessResult(Handler?.Invoke(line) ?? 0, OutputHandler?.Invoke(line) ?? string.Empty));
    }

    public void StartDetached(string exe, string args) => Detached.Add($"{exe} {args}");

    public int IndexOf(string prefix) => Commands.FindIndex(c => c.StartsWith(prefix, StringComparison.Ordinal));

    public string Single(string prefix) => Commands.Single(c => c.StartsWith(prefix, StringComparison.Ordinal));
}

internal sealed class FakeElevation(bool elevated) : IElevation
{
    public bool IsElevated { get; } = elevated;

    public List<string[]> Relaunches { get; } = [];

    /// <summary>Null simulates a declined UAC prompt.</summary>
    public int? RelaunchExitCode { get; set; } = 7;

    public Task<int?> RelaunchElevatedAsync(string[] args, TextWriter output, CancellationToken ct)
    {
        Relaunches.Add(args);
        return Task.FromResult(RelaunchExitCode);
    }
}

/// <summary>The autostart value in memory. <see cref="Calls"/> records every call in order; <see cref="OnCall"/> runs first, so a test can see what else had happened by then.</summary>
internal sealed class FakeRunKey : IRunKey
{
    public string? Value { get; set; }

    public List<string> Calls { get; } = [];

    public Action<string>? OnCall { get; set; }

    public string? Read()
    {
        Record("Read");
        return Value;
    }

    public void Write(string commandLine)
    {
        Record($"Write {commandLine}");
        Value = commandLine;
    }

    public void Delete()
    {
        Record("Delete");
        Value = null;
    }

    private void Record(string call)
    {
        OnCall?.Invoke(call);
        Calls.Add(call);
    }
}

/// <summary>Everything is a plain folder owned by Administrators unless a test says otherwise.</summary>
internal sealed class FakeInspector : IDataDirInspector
{
    public Dictionary<string, SecurityIdentifier> Owners { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SecurityIdentifier DefaultOwner { get; set; } = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    /// <summary>Owners that only show up from the second full check on, i.e. something placed between the first check and the second.</summary>
    public Dictionary<string, SecurityIdentifier> OwnersFromSecondCheck { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folder whose inspection marks the start of a check; set to enable <see cref="OwnersFromSecondCheck"/>.</summary>
    public string? CheckRoot { get; set; }

    public int Checks { get; private set; }

    public bool IsReparsePoint(string path) => Links.Contains(path);

    public SecurityIdentifier? GetOwner(string path)
    {
        if (CheckRoot is not null && string.Equals(path, CheckRoot, StringComparison.OrdinalIgnoreCase))
        {
            Checks++;
        }
        if (Checks >= 2 && OwnersFromSecondCheck.TryGetValue(path, out var late))
        {
            return late;
        }
        return Owners.TryGetValue(path, out var owner) ? owner : DefaultOwner;
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

    public FakeInspector Inspector { get; } = new();

    // Spaces on purpose: the real Program Files has one, and the quoting has to survive it.
    public string ProgramDir => Path.Combine(Root, "Program Files", "WindowsBtProxyForHA");

    public DataPaths Data => new(Path.Combine(Root, "ProgramData", "WindowsBtProxyForHA"));

    public InstallPaths Paths(string? sourceExe = null, string? programDir = null)
        => new(programDir ?? ProgramDir, Data, ApplyDataDirAcl: false, SourceExe: sourceExe ?? SourceExe, Inspector: Inspector);

    public SetupContext Context(FakeProcessRunner runner, StringWriter output, bool elevated = true, InstallPaths? paths = null, FakeElevation? elevation = null, FakeRunKey? runKey = null)
        => new(runner, paths ?? Paths(), elevation ?? new FakeElevation(elevated), output, new SetupConsoleLogger(output), runKey ?? new FakeRunKey()) { ApplyKeyAcl = false };

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
