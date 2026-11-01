using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class TrayLightTests
{
    private static PipeResponse Respond(
        ScannerStatus scanner = ScannerStatus.Running,
        string friendlyName = "BT Proxy Test",
        params ClientStatus[] clients) =>
        new(PipeProtocol.Version, new ProxyStatus(
            "0.2.0", "bt-proxy-test", friendlyName, 6053, ScanMode.Passive, scanner,
            "02:11:22:33:44:55", ApiRunning: true, AdvertisementsLastMinute: 10, EncryptionMode.Required, clients));

    private static ClientStatus Client(bool subscribed) => new("192.168.1.10", "Home Assistant 2026.11", subscribed, Encrypted: true);

    [Fact]
    public void A_subscribed_client_is_green_and_says_connected()
    {
        var view = TrayLight.Evaluate(Respond(clients: Client(subscribed: true)));

        Assert.Equal(Light.Green, view.Light);
        Assert.Equal("BT Proxy Test: connected to Home Assistant", view.Tooltip);
    }

    [Fact]
    public void Clients_without_a_subscription_are_amber()
    {
        var view = TrayLight.Evaluate(Respond(clients: [Client(subscribed: false), Client(subscribed: false)]));

        Assert.Equal(Light.Amber, view.Light);
        Assert.Equal("BT Proxy Test: running, Home Assistant not connected", view.Tooltip);
    }

    [Fact]
    public void No_clients_with_a_running_scanner_is_amber()
    {
        Assert.Equal(Light.Amber, TrayLight.Evaluate(Respond()).Light);
    }

    [Fact]
    public void A_starting_scanner_is_amber()
    {
        var view = TrayLight.Evaluate(Respond(ScannerStatus.Starting));

        Assert.Equal(Light.Amber, view.Light);
        Assert.Equal("BT Proxy Test: running, Home Assistant not connected", view.Tooltip);
    }

    [Fact]
    public void A_starting_scanner_with_a_subscribed_client_is_green()
    {
        Assert.Equal(Light.Green, TrayLight.Evaluate(Respond(ScannerStatus.Starting, clients: Client(subscribed: true))).Light);
    }

    [Theory]
    [InlineData(ScannerStatus.RadioOff, "BT Proxy Test: Bluetooth is off")]
    [InlineData(ScannerStatus.NoAdapter, "BT Proxy Test: no Bluetooth adapter")]
    [InlineData(ScannerStatus.Failed, "BT Proxy Test: Bluetooth scanner failed")]
    public void A_scanner_in_a_red_state_is_red_even_with_a_subscribed_client(ScannerStatus scanner, string tooltip)
    {
        var view = TrayLight.Evaluate(Respond(scanner, clients: Client(subscribed: true)));

        Assert.Equal(Light.Red, view.Light);
        Assert.Equal(tooltip, view.Tooltip);
    }

    [Fact]
    public void An_unreachable_service_is_red_with_the_default_name()
    {
        var view = TrayLight.Evaluate(null);

        Assert.Equal(Light.Red, view.Light);
        Assert.Equal("Windows BT Proxy: service not running", view.Tooltip);
    }

    [Fact]
    public void An_error_response_is_red_and_quotes_the_error()
    {
        var view = TrayLight.Evaluate(new PipeResponse(PipeProtocol.Version, Error: PipeErrors.Version));

        Assert.Equal(Light.Red, view.Light);
        Assert.Equal("Windows BT Proxy: service answered \"version\"", view.Tooltip);
    }

    [Fact]
    public void A_response_with_neither_status_nor_error_is_treated_as_an_error()
    {
        var view = TrayLight.Evaluate(new PipeResponse(PipeProtocol.Version));

        Assert.Equal(Light.Red, view.Light);
        Assert.Equal("Windows BT Proxy: service answered \"<no status>\"", view.Tooltip);
    }

    [Fact]
    public void A_blank_friendly_name_falls_back_to_the_default_name()
    {
        var view = TrayLight.Evaluate(Respond(friendlyName: "  "));

        Assert.Equal("Windows BT Proxy: running, Home Assistant not connected", view.Tooltip);
    }

    [Fact]
    public void A_long_friendly_name_is_cut_to_127_chars_with_an_ellipsis()
    {
        var view = TrayLight.Evaluate(Respond(friendlyName: new string('x', 200)));

        Assert.Equal(127, view.Tooltip.Length);
        Assert.EndsWith("…", view.Tooltip);
        Assert.StartsWith("xxxx", view.Tooltip);
    }

    [Fact]
    public void A_tooltip_of_exactly_127_chars_is_left_alone()
    {
        const string suffix = ": running, Home Assistant not connected";
        var name = new string('x', 127 - suffix.Length);

        var view = TrayLight.Evaluate(Respond(friendlyName: name));

        Assert.Equal(name + suffix, view.Tooltip);
    }

    [Fact]
    public void Truncation_does_not_split_a_surrogate_pair()
    {
        // 125 chars of prefix text leave the ellipsis as char 126, so a pair straddling the cut must be dropped whole.
        var prefix = "Windows BT Proxy: service answered \"";
        var error = new string('x', 125 - prefix.Length) + "\U0001F600" + new string('y', 50);

        var view = TrayLight.Evaluate(new PipeResponse(PipeProtocol.Version, Error: error));

        Assert.True(view.Tooltip.Length <= 127);
        Assert.EndsWith("…", view.Tooltip);
        Assert.False(char.IsHighSurrogate(view.Tooltip[^2]));
    }
}
