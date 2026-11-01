using WindowsBtProxy.Config;
using WindowsBtProxy.Hosting;

namespace WindowsBtProxy.Tests.Hosting;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_means_the_defaults_for_a_console_run()
    {
        Assert.True(CommandLine.TryParseHostOptions([], asService: false, out var options, out var error));
        Assert.Null(error);
        Assert.Equal(new ProxyHostOptions(DataPaths.Default, AsService: false, FakeScanner: false, NoMdns: false, PortOverride: null), options);
    }

    [Fact]
    public void Service_verb_sets_AsService()
    {
        Assert.True(CommandLine.TryParseHostOptions([], asService: true, out var options, out _));
        Assert.True(options!.AsService);
    }

    [Fact]
    public void All_flags_are_read_case_insensitively()
    {
        var dir = Path.Combine(Path.GetTempPath(), "wbp-data");
        Assert.True(CommandLine.TryParseHostOptions(["--Fake-Scanner", "--no-mdns", "--port", "6060", "--DATA-DIR", dir], asService: false, out var options, out _));
        Assert.Equal(new ProxyHostOptions(new DataPaths(dir), false, true, true, 6060), options);
    }

    [Fact]
    public void The_key_file_is_protected_unless_a_test_asks_otherwise()
    {
        Assert.True(CommandLine.TryParseHostOptions([], asService: false, out var normal, out _));
        Assert.True(normal!.ProtectKeyFile);
        var dir = Path.Combine(Path.GetTempPath(), "wbp-data");
        Assert.True(CommandLine.TryParseHostOptions(["--no-key-acl", "--data-dir", dir], asService: false, out var test, out _));
        Assert.False(test!.ProtectKeyFile);
        Assert.True(CommandLine.TryParseHostOptions(["--data-dir", dir, "--no-key-acl"], asService: false, out var reversed, out _));
        Assert.False(reversed!.ProtectKeyFile);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_key_acl_needs_a_non_default_data_dir(bool explicitDefault)
    {
        string[] args = explicitDefault
            ? ["--no-key-acl", "--data-dir", DataPaths.Default.Root + Path.DirectorySeparatorChar]
            : ["--no-key-acl"];

        Assert.False(CommandLine.TryParseHostOptions(args, asService: false, out var options, out var error));
        Assert.Null(options);
        Assert.Equal("--no-key-acl is only allowed with a non-default --data-dir", error);
    }

    [Theory]
    [InlineData("--port")]
    [InlineData("--port", "abc")]
    [InlineData("--port", "0")]
    [InlineData("--port", "65536")]
    [InlineData("--port", "-1")]
    [InlineData("--data-dir")]
    [InlineData("--frobnicate")]
    [InlineData("stray")]
    public void Bad_arguments_are_rejected_with_a_message(params string[] args)
    {
        Assert.False(CommandLine.TryParseHostOptions(args, asService: false, out var options, out var error));
        Assert.Null(options);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }
}
