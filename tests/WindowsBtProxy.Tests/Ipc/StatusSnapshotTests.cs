using System.Net;
using System.Text.Json;
using WindowsBtProxy.Api;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Tests.Ipc;

public sealed class StatusSnapshotTests
{
    private static ProxyStatus Build(
        ulong adapterAddress = 0x021122334455,
        bool hasKey = false,
        IReadOnlyList<ClientInfo>? clients = null,
        long? advertisementsLastMinute = 1234) =>
        StatusSnapshot.Build(
            "0.2.0",
            new ProxySettings("bt-proxy-test", "BT Proxy Test", 6053, ScanMode.Passive),
            6053,
            ScannerStatus.Running,
            adapterAddress,
            apiRunning: true,
            advertisementsLastMinute,
            hasKey,
            clients ?? []);

    [Fact]
    public void Adapter_mac_is_colon_upper_case_and_null_without_adapter()
    {
        Assert.Equal("02:11:22:33:44:55", Build(adapterAddress: 0x021122334455).AdapterMac);
        Assert.Equal("AA:BB:CC:DD:EE:FF", Build(adapterAddress: 0xAABBCCDDEEFF).AdapterMac);
        Assert.Null(Build(adapterAddress: 0).AdapterMac);
    }

    [Fact]
    public void Long_client_info_is_cut_to_128_chars()
    {
        var remote = new IPEndPoint(IPAddress.Parse("192.168.1.10"), 50123);
        var exact = new string('a', 128);
        var surrogateAtCut = new string('b', 127) + "😀" + "tail"; // the emoji's high surrogate is char 128

        var status = Build(clients:
        [
            new ClientInfo(remote, exact + new string('z', 4000), false, false),
            new ClientInfo(remote, exact, false, false),
            new ClientInfo(remote, surrogateAtCut, false, false),
        ]);

        Assert.Equal(exact, status.Clients[0].Info);
        Assert.Equal(exact, status.Clients[1].Info);
        Assert.Equal(new string('b', 127), status.Clients[2].Info); // no half pair left at the end
    }

    [Fact]
    public void Clients_carry_ip_only_info_and_flags()
    {
        var status = Build(clients: [new ClientInfo(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 50123), "Home Assistant 2026.10.4", true, true)]);

        Assert.Equal([new ClientStatus("192.168.1.10", "Home Assistant 2026.10.4", true, true)], status.Clients);
    }

    [Fact]
    public void Ipv4_mapped_ipv6_remote_is_shown_as_plain_ipv4()
    {
        var mapped = IPAddress.Parse("192.168.1.10").MapToIPv6();
        var status = Build(clients:
        [
            new ClientInfo(new IPEndPoint(mapped, 50123), "ha", false, false),
            new ClientInfo(new IPEndPoint(IPAddress.Parse("fe80::1"), 50124), "other", true, false),
        ]);

        Assert.Equal("192.168.1.10", status.Clients[0].Address);
        Assert.Equal("fe80::1", status.Clients[1].Address);
    }

    [Fact]
    public void Encryption_follows_key()
    {
        Assert.Equal(EncryptionMode.Provisionable, Build(hasKey: false).Encryption);
        Assert.Equal(EncryptionMode.Required, Build(hasKey: true).Encryption);
    }

    [Fact]
    public void Build_copies_the_scalar_fields()
    {
        var status = Build();

        Assert.Equal("0.2.0", status.Version);
        Assert.Equal("bt-proxy-test", status.Name);
        Assert.Equal("BT Proxy Test", status.FriendlyName);
        Assert.Equal(6053, status.Port);
        Assert.Equal(ScanMode.Passive, status.ScanMode);
        Assert.Equal(ScannerStatus.Running, status.Scanner);
        Assert.True(status.ApiRunning);
        Assert.Equal(1234, status.AdvertisementsLastMinute);
    }

    [Fact]
    public void Round_trips_through_source_generated_json_with_string_enums()
    {
        var original = Build(hasKey: true, clients: [new ClientInfo(new IPEndPoint(IPAddress.Parse("10.0.0.2"), 1), "HA", true, true)]);

        var json = JsonSerializer.Serialize(original, IpcJsonContext.Default.ProxyStatus);

        Assert.Contains("\"scanner\":\"Running\"", json);
        // ScanMode keeps its settings.json spelling (JsonStringEnumMemberName), so the pipe says "passive", not "Passive".
        Assert.Contains("\"scanMode\":\"passive\"", json);
        Assert.Contains("\"encryption\":\"Required\"", json);

        var copy = JsonSerializer.Deserialize(json, IpcJsonContext.Default.ProxyStatus);
        Assert.NotNull(copy);
        Assert.Equal(original with { Clients = [] }, copy with { Clients = [] });
        Assert.Equal(original.Clients, copy.Clients);
    }

    [Fact]
    public void Null_adapter_mac_and_advertisement_count_are_left_out_of_the_json()
    {
        var json = JsonSerializer.Serialize(Build(adapterAddress: 0, advertisementsLastMinute: null), IpcJsonContext.Default.ProxyStatus);

        Assert.DoesNotContain("adapterMac", json);
        Assert.DoesNotContain("advertisementsLastMinute", json);
    }
}
