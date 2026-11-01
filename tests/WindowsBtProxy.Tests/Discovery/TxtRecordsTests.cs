using System.Net;
using System.Net.NetworkInformation;
using WindowsBtProxy.Api;
using WindowsBtProxy.Discovery;

namespace WindowsBtProxy.Tests.Discovery;

public class TxtRecordsTests
{
    private const string Noise = "Noise_NNpsk0_25519_ChaChaPoly_SHA256";

    private static readonly DeviceIdentity Id = new(
        "windows-bt-proxy-334455", "Windows BT Proxy", 0x021122334455, "DESKTOP-1", "Nov  1 2026, 12:00:00");

    [Fact]
    public void Txt_without_key_has_supported_and_zero_psk()
    {
        var txt = TxtRecords.Build(Id, hasKey: false);

        Assert.Equal(Noise, txt["api_encryption_supported"]);
        Assert.Equal("zero-psk", txt["api_provisioning"]);
        Assert.DoesNotContain("api_encryption", txt.Keys);
        Assert.Equal("Windows BT Proxy", txt["friendly_name"]);
        Assert.Equal("2026.10.0", txt["version"]);
        Assert.Equal("021122334455", txt["mac"]);
        Assert.Equal("Windows", txt["platform"]);
        Assert.Equal("windows", txt["board"]);
        Assert.Equal("ethernet", txt["network"]);
    }

    [Fact]
    public void Txt_with_key_has_api_encryption_only()
    {
        var txt = TxtRecords.Build(Id, hasKey: true);

        Assert.Equal(Noise, txt["api_encryption"]);
        Assert.DoesNotContain("api_encryption_supported", txt.Keys);
        Assert.DoesNotContain("api_provisioning", txt.Keys);
    }

    [Fact]
    public void Network_is_wifi_only_when_first_selected_interface_is_wireless()
    {
        var wifi = new NetInterface(4, "WLAN", "Wi-Fi", NetworkInterfaceType.Wireless80211, true, IPAddress.Loopback);
        var eth = new NetInterface(3, "Ethernet", "Ethernet", NetworkInterfaceType.Ethernet, true, IPAddress.Loopback);

        Assert.Equal("wifi", TxtRecords.NetworkFor([wifi, eth]));
        Assert.Equal("ethernet", TxtRecords.NetworkFor([eth, wifi]));
        Assert.Equal("ethernet", TxtRecords.NetworkFor([]));
        Assert.Equal("wifi", TxtRecords.Build(Id, false, "wifi")["network"]);
    }
}
