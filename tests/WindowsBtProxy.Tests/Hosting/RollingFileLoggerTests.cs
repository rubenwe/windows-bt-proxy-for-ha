using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Hosting;

namespace WindowsBtProxy.Tests.Hosting;

public sealed class RollingFileLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Directory.CreateTempSubdirectory("log-tests-").FullName, "logs");

    [Fact]
    public void Default_limits_are_1MB_and_5_files()
    {
        Assert.Equal(1024 * 1024, RollingFileLogger.DefaultMaxFileBytes);
        Assert.Equal(5, RollingFileLogger.DefaultMaxFiles);
    }

    [Fact]
    public void Rolls_after_1MB_and_keeps_5_files()
    {
        // The real limit is 1 MB; the test injects 1 KB so it does not write megabytes.
        using var provider = new RollingFileLogger(_dir, LogLevel.Information, maxFileBytes: 1024);
        var log = provider.CreateLogger("Test");

        for (var i = 0; i < 400; i++)
        {
            log.LogInformation("line {Index} {Padding}", i, new string('x', 80));
        }

        var files = Directory.GetFiles(_dir).Select(f => Path.GetFileName(f)).Order().ToArray();
        Assert.Equal(["proxy.1.log", "proxy.2.log", "proxy.3.log", "proxy.4.log", "proxy.log"], files);
        foreach (var file in files)
        {
            Assert.True(new FileInfo(Path.Combine(_dir, file)).Length <= 1024, file);
        }
        // The newest line is last in proxy.log; the oldest of the 400 have been deleted.
        var current = File.ReadAllLines(Path.Combine(_dir, "proxy.log"));
        Assert.Contains("line 399 ", current[^1]);
        Assert.DoesNotContain(files, f => File.ReadAllText(Path.Combine(_dir, f)).Contains("line 0 "));
    }

    [Fact]
    public void Writes_formatted_lines_with_exception_text()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 12, 30, 45, 123, TimeSpan.Zero));
        using (var provider = new RollingFileLogger(_dir, LogLevel.Information, timeProvider: time))
        {
            var log = provider.CreateLogger("Api.Server");
            log.LogWarning("hello {Who}", "world");
            log.LogError(new InvalidOperationException("boom"), "failed");
        }

        var lines = File.ReadAllLines(Path.Combine(_dir, "proxy.log"));
        Assert.Equal("2026-11-01T12:30:45.123Z WARN Api.Server: hello world", lines[0]);
        Assert.Equal("2026-11-01T12:30:45.123Z ERROR Api.Server: failed", lines[1]);
        Assert.Contains("System.InvalidOperationException: boom", lines[2]);
    }

    [Fact]
    public void Filters_below_the_minimum_level()
    {
        using (var provider = new RollingFileLogger(_dir, LogLevel.Warning))
        {
            var log = provider.CreateLogger("T");
            Assert.False(log.IsEnabled(LogLevel.Information));
            Assert.True(log.IsEnabled(LogLevel.Error));
            log.LogInformation("hidden");
            log.LogError("shown");
        }

        var text = File.ReadAllText(Path.Combine(_dir, "proxy.log"));
        Assert.DoesNotContain("hidden", text);
        Assert.Contains("shown", text);
    }

    [Theory]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("info", LogLevel.Information)]
    [InlineData("INFO", LogLevel.Information)]
    [InlineData("warning", LogLevel.Warning)]
    [InlineData("error", LogLevel.Error)]
    [InlineData("nonsense", LogLevel.Information)]
    [InlineData(null, LogLevel.Information)]
    public void ParseLevel_maps_settings_strings(string? text, LogLevel expected)
    {
        Assert.Equal(expected, RollingFileLogger.ParseLevel(text));
    }

    [Fact]
    public void Continues_the_existing_file_across_restarts()
    {
        using (var provider = new RollingFileLogger(_dir, LogLevel.Information))
        {
            provider.CreateLogger("T").LogInformation("first");
        }
        using (var provider = new RollingFileLogger(_dir, LogLevel.Information))
        {
            provider.CreateLogger("T").LogInformation("second");
        }

        Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir, "proxy.log")).Length);
    }

    [Fact]
    public void Never_throws_when_the_log_cannot_be_written()
    {
        // The log directory path is occupied by a file, so it can be neither created nor written.
        Directory.CreateDirectory(Directory.GetParent(_dir)!.FullName);
        File.WriteAllText(_dir, "not a directory");

        using var provider = new RollingFileLogger(_dir, LogLevel.Information);
        provider.CreateLogger("T").LogError(new Exception("x"), "cannot be written");
    }

    [Fact]
    public async Task Is_safe_to_use_from_many_threads()
    {
        using (var provider = new RollingFileLogger(_dir, LogLevel.Information, maxFileBytes: 2048))
        {
            var log = provider.CreateLogger("T");
            await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (var i = 0; i < 100; i++)
                {
                    log.LogInformation("thread {T} line {I}", t, i);
                }
            })));
        }

        var lines = Directory.GetFiles(_dir).SelectMany(f => File.ReadAllLines(f)).ToArray();
        Assert.NotEmpty(lines);
        Assert.All(lines, l => Assert.Matches(@"^\d{4}-\d\d-\d\dT[\d:.]+Z INFO T: thread \d line \d+$", l));
    }

    public void Dispose()
    {
        Directory.Delete(Directory.GetParent(_dir)!.FullName, recursive: true);
    }
}
