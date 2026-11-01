using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Tests.Ipc;

public sealed class StatusTextTests
{
    private static readonly ProxyStatus Full = new(
        "0.2.0", "bt-proxy-desktop", "BT Proxy DESKTOP", 6053, ScanMode.Passive, ScannerStatus.Running, "02:11:22:33:44:55",
        ApiRunning: true, AdvertisementsLastMinute: 112, EncryptionMode.Required,
        [new ClientStatus("192.168.1.10", "Home Assistant 2026.10.4", Subscribed: true, Encrypted: true)]);

    private static string[] Lines(ProxyStatus status) => StatusText.Format(status).Split(Environment.NewLine);

    [Fact]
    public void A_full_status_reads_as_documented()
    {
        Assert.Equal(
        [
            "BT Proxy DESKTOP (bt-proxy-desktop), version 0.2.0",
            "Bluetooth:      Running, passive scan, adapter 02:11:22:33:44:55",
            "Advertisements: 112 in the last minute",
            "API:            port 6053",
            "Encryption:     key set; only encrypted connections",
            "Clients:        192.168.1.10  Home Assistant 2026.10.4  subscribed, encrypted",
        ], Lines(Full));
    }

    [Fact]
    public void A_count_not_taken_yet_says_so()
    {
        Assert.Equal("Advertisements: not counted yet", Lines(Full with { AdvertisementsLastMinute = null })[2]);
    }

    [Fact]
    public void An_api_that_is_not_running_waits_for_an_adapter()
    {
        Assert.Equal("API:            waiting for a Bluetooth adapter", Lines(Full with { ApiRunning = false })[3]);
    }

    [Fact]
    public void Without_a_key_home_assistant_provisions_one()
    {
        Assert.Equal("Encryption:     no key yet; Home Assistant provisions one when it adds the proxy", Lines(Full with { Encryption = EncryptionMode.Provisionable })[4]);
    }

    [Fact]
    public void No_clients_is_none()
    {
        var lines = Lines(Full with { Clients = [] });

        Assert.Equal(6, lines.Length);
        Assert.Equal("Clients:        none", lines[5]);
    }

    [Fact]
    public void Each_client_gets_a_line_under_the_first()
    {
        var lines = Lines(Full with
        {
            Clients =
            [
                new ClientStatus("192.168.1.10", "Home Assistant 2026.10.4", Subscribed: true, Encrypted: true),
                new ClientStatus("192.168.1.11", "aioesphomeapi", Subscribed: false, Encrypted: false),
            ],
        });

        Assert.Equal(
        [
            "Clients:        192.168.1.10  Home Assistant 2026.10.4  subscribed, encrypted",
            "                192.168.1.11  aioesphomeapi  not subscribed, not encrypted",
        ], lines[5..]);
    }

    [Fact]
    public void A_client_without_info_leaves_it_out()
    {
        Assert.Equal("Clients:        192.168.1.10  subscribed, encrypted", Lines(Full with { Clients = [Full.Clients[0] with { Info = "" }] })[5]);
    }

    [Fact]
    public void A_client_cannot_add_lines_of_its_own()
    {
        var lines = Lines(Full with { Clients = [Full.Clients[0] with { Info = "evil\r\nEncryption:     off\t!" }] });

        Assert.Equal(6, lines.Length);
        Assert.Equal("Clients:        192.168.1.10  evil  Encryption:     off !  subscribed, encrypted", lines[5]);
    }

    [Theory]
    [InlineData(ScannerStatus.Starting, "Bluetooth:      Starting, passive scan, adapter 02:11:22:33:44:55")]
    [InlineData(ScannerStatus.RadioOff, "Bluetooth:      Radio off, passive scan, adapter 02:11:22:33:44:55")]
    [InlineData(ScannerStatus.Failed, "Bluetooth:      Failed, passive scan, adapter 02:11:22:33:44:55")]
    public void The_scanner_state_is_spelled_out(ScannerStatus scanner, string line)
    {
        Assert.Equal(line, Lines(Full with { Scanner = scanner })[1]);
    }

    [Fact]
    public void Without_an_adapter_there_is_no_address()
    {
        Assert.Equal("Bluetooth:      No adapter, active scan", Lines(Full with { Scanner = ScannerStatus.NoAdapter, ScanMode = ScanMode.Active, AdapterMac = null })[1]);
    }

    [Fact]
    public void Not_running_names_only_a_pipe_that_is_not_the_default()
    {
        Assert.Equal("The Windows BT Proxy service is not running (or not installed).", StatusText.NotRunning(PipeProtocol.DefaultPipeName));
        Assert.Equal("The Windows BT Proxy service is not running (or not installed). (pipe wbp-test-1)", StatusText.NotRunning("wbp-test-1"));
    }
}
