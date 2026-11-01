using WindowsBtProxy.Setup;

namespace WindowsBtProxy.Tests.Setup;

/// <summary>Only runs cmd.exe with harmless commands; the install commands themselves are never run by tests.</summary>
public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task RunAsync_returns_the_exit_code_without_throwing_for_non_zero()
    {
        var runner = new ProcessRunner();

        Assert.Equal(0, (await runner.RunAsync("cmd.exe", "/c exit 0", CancellationToken.None)).ExitCode);
        Assert.Equal(3, (await runner.RunAsync("cmd.exe", "/c exit 3", CancellationToken.None)).ExitCode);
    }

    [Fact]
    public async Task RunAsync_decodes_output_in_the_oem_code_page()
    {
        // Make cmd write in the code page the runner decodes with, then echo a character that exists in the common OEM pages.
        var cp = ProcessRunner.OemEncoding.CodePage;
        var result = await new ProcessRunner().RunAsync("cmd.exe", $"/c chcp {cp} >nul & echo Löschen", CancellationToken.None);

        Assert.Equal("Löschen", result.Output);
    }

    [Fact]
    public async Task RunAsync_does_not_block_on_chatty_output()
    {
        var code = (await new ProcessRunner().RunAsync("cmd.exe", "/c for /l %i in (1,1,3000) do @echo line %i", CancellationToken.None)).ExitCode;

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task RunAsync_keeps_the_last_ten_lines_of_output_from_stdout_and_stderr()
    {
        var result = await new ProcessRunner().RunAsync("cmd.exe", "/c for /l %i in (1,1,25) do @echo line %i", CancellationToken.None);

        var lines = result.Output.Split('\n');
        Assert.Equal(10, lines.Length);
        Assert.Equal("line 25", lines[^1]);
        Assert.Equal("line 16", lines[0]);

        var err = await new ProcessRunner().RunAsync("cmd.exe", "/c echo it broke 1>&2 & exit 4", CancellationToken.None);
        Assert.Equal(4, err.ExitCode);
        Assert.Equal("it broke", err.Output);
    }

    [Fact]
    public async Task StartDetached_runs_in_the_system_folder_so_a_long_lived_child_holds_no_user_folder()
    {
        // The tray that install starts lives until logoff; its current folder must not be the one install was run from.
        var dir = Directory.CreateTempSubdirectory("detached-tests-").FullName;
        try
        {
            var file = Path.Combine(dir, "cwd.txt");
            Assert.NotEqual(Environment.SystemDirectory, Environment.CurrentDirectory, StringComparer.OrdinalIgnoreCase);

            new ProcessRunner().StartDetached("cmd.exe", $"/c cd > \"{file}.tmp\" & move /y \"{file}.tmp\" \"{file}\" >nul");

            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(file) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.Equal(Environment.SystemDirectory, File.ReadAllText(file).Trim(), StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
