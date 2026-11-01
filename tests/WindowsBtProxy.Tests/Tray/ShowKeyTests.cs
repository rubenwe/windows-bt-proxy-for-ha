using WindowsBtProxy.Ipc;
using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class ShowKeyTests
{
    private const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    [Fact]
    public void A_key_is_shown_and_offered_for_the_clipboard()
    {
        var box = TrayCommand.KeyMessage(new PipeResponse(PipeProtocol.Version, Key: new KeyReply(Key)));

        Assert.Equal(
            $"Encryption key:\n\n{Key}\n\nHome Assistant asks for it only if you add the proxy by hand with a key. Copy it to the clipboard?",
            box.Text);
        Assert.True(box.OfferCopy);
        Assert.False(box.Warning);
    }

    [Fact]
    public void No_stored_key_says_home_assistant_provisions_one()
    {
        var box = TrayCommand.KeyMessage(new PipeResponse(PipeProtocol.Version, Key: new KeyReply(null)));

        Assert.Equal("No key yet. Home Assistant provisions one when it adds the proxy.", box.Text);
        Assert.False(box.OfferCopy);
        Assert.False(box.Warning);
    }

    [Fact]
    public void A_denied_request_says_only_an_administrator_sees_the_key()
    {
        var box = TrayCommand.KeyMessage(new PipeResponse(PipeProtocol.Version, Error: PipeErrors.Denied));

        Assert.Equal("The service only shows the key to an administrator.", box.Text);
        Assert.False(box.OfferCopy);
        Assert.True(box.Warning);
    }

    [Fact]
    public void No_answer_says_the_service_is_not_running()
    {
        var box = TrayCommand.KeyMessage(null);

        Assert.Equal("The Windows BT Proxy service is not running.", box.Text);
        Assert.False(box.OfferCopy);
        Assert.True(box.Warning);
    }

    [Theory]
    [InlineData(PipeErrors.Version, "The service answered with an error: version.")]
    [InlineData(PipeErrors.UnknownRequest, "The service answered with an error: unknown-request.")]
    [InlineData(null, "The service answered with an error: no key in the answer.")]
    public void Any_other_answer_shows_the_error_and_offers_nothing(string? error, string expected)
    {
        var box = TrayCommand.KeyMessage(new PipeResponse(PipeProtocol.Version, Error: error));

        Assert.Equal(expected, box.Text);
        Assert.False(box.OfferCopy);
        Assert.True(box.Warning);
    }

    [Fact]
    public void The_relaunch_on_the_default_pipe_passes_no_pipe_name()
    {
        Assert.Equal("tray --show-key", TrayCommand.ShowKeyArguments(PipeProtocol.DefaultPipeName));
    }

    [Theory]
    [InlineData("wbp-test-1", "tray --show-key --pipe-name wbp-test-1")]
    [InlineData("with space", "tray --show-key --pipe-name \"with space\"")]
    [InlineData("say \"hi\"", "tray --show-key --pipe-name \"say \\\"hi\\\"\"")]
    [InlineData("end\\ x\\", "tray --show-key --pipe-name \"end\\ x\\\\\"")]
    public void The_relaunch_passes_another_pipe_name_quoted_as_needed(string pipeName, string expected)
    {
        Assert.Equal(expected, TrayCommand.ShowKeyArguments(pipeName));
    }

    [Theory]
    [InlineData(new string[0], PipeProtocol.DefaultPipeName, false)]
    [InlineData(new[] { "--show-key" }, PipeProtocol.DefaultPipeName, true)]
    [InlineData(new[] { "--SHOW-KEY", "--pipe-name", "p" }, "p", true)]
    [InlineData(new[] { "--pipe-name", "p", "--show-key" }, "p", true)]
    [InlineData(new[] { "--pipe-name", "p" }, "p", false)]
    public void Tray_arguments_parse(string[] args, string pipeName, bool showKey)
    {
        Assert.True(TrayCommand.TryParse(args, out var parsedPipeName, out var parsedShowKey));
        Assert.Equal(pipeName, parsedPipeName);
        Assert.Equal(showKey, parsedShowKey);
    }

    [Theory]
    [InlineData("--pipe-name")]
    [InlineData("--pipe-name", " ")]
    [InlineData("--show-key", "--show-key")]
    [InlineData("--other")]
    public void Unknown_or_incomplete_tray_arguments_are_refused(params string[] args)
    {
        Assert.False(TrayCommand.TryParse(args, out _, out _));
    }
}
