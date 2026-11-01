using WindowsBtProxy.Config;

namespace WindowsBtProxy.Tests.Config;

public sealed class NameSanitizerTests
{
    [Theory]
    [InlineData("DESKTOP-ABC123", "bt-proxy-desktop-abc123")]
    [InlineData("Media_PC", "bt-proxy-media-pc")]
    [InlineData("Küche", "bt-proxy-k-che")]
    [InlineData("---", "bt-proxy")]
    public void DefaultName_sanitizes(string machine, string expected)
    {
        Assert.Equal(expected, NameSanitizer.DefaultName(machine));
    }

    [Fact]
    public void DefaultName_is_at_most_31_chars()
    {
        var name = NameSanitizer.DefaultName(new string('x', 100));

        Assert.True(name.Length <= 31, name);
        Assert.StartsWith("bt-proxy-xxx", name);
    }

    [Fact]
    public void DefaultName_never_ends_with_a_dash_after_truncation()
    {
        // "bt-proxy-" is 9 chars, so the dash after 21 machine chars lands exactly on the 31-char cut.
        var name = NameSanitizer.DefaultName(new string('a', 21) + "-" + new string('b', 10));

        Assert.Equal("bt-proxy-" + new string('a', 21), name);
    }

    [Theory]
    [InlineData("My Proxy!", "my-proxy")]
    [InlineData("--a--b--", "a-b")]
    [InlineData("UPPER", "upper")]
    [InlineData("", "")]
    public void Sanitize_lowercases_replaces_collapses_and_trims(string raw, string expected)
    {
        Assert.Equal(expected, NameSanitizer.Sanitize(raw));
    }

    [Fact]
    public void DefaultFriendlyName_uses_machine_name_as_given()
    {
        Assert.Equal("BT Proxy DESKTOP-ABC123", NameSanitizer.DefaultFriendlyName("DESKTOP-ABC123"));
    }
}
