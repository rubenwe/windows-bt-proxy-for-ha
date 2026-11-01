using System.Net;
using System.Net.NetworkInformation;
using WindowsBtProxy.Discovery;

namespace WindowsBtProxy.Tests.Discovery;

public class InterfaceSelectorTests
{
    private static NetInterface Nic(int index, string name, string description, NetworkInterfaceType type, bool up = true, string? ip = "192.168.1.10")
        => new(index, name, description, type, up, ip is null ? null : IPAddress.Parse(ip));

    private static readonly NetInterface Ethernet = Nic(3, "Ethernet", "Intel(R) I225-V", NetworkInterfaceType.Ethernet);
    private static readonly NetInterface WiFi = Nic(4, "WLAN", "Intel(R) Wi-Fi 6E AX211", NetworkInterfaceType.Wireless80211, ip: "192.168.1.11");
    private static readonly NetInterface Tailscale = Nic(5, "Tailscale", "Tailscale Tunnel", NetworkInterfaceType.Ethernet, ip: "100.64.0.2");
    private static readonly NetInterface HyperV = Nic(6, "vEthernet (WSL)", "Hyper-V Virtual Ethernet Adapter", NetworkInterfaceType.Ethernet, ip: "172.20.0.1");
    private static readonly NetInterface Loopback = Nic(1, "Loopback Pseudo-Interface 1", "Software Loopback Interface 1", NetworkInterfaceType.Loopback, ip: "127.0.0.1");
    private static readonly NetInterface DownNic = Nic(7, "Ethernet 2", "Realtek PCIe GbE", NetworkInterfaceType.Ethernet, up: false);
    private static readonly NetInterface NoIp = Nic(8, "Ethernet 3", "Broadcom NetXtreme", NetworkInterfaceType.Ethernet, ip: null);
    private static readonly NetInterface TunnelNic = Nic(9, "isatap", "Microsoft ISATAP Adapter", NetworkInterfaceType.Tunnel, ip: "10.0.0.5");

    [Fact]
    public void Excludes_tailscale_hyperv_loopback_and_down_interfaces()
    {
        var all = new[] { Loopback, Ethernet, Tailscale, HyperV, DownNic, NoIp, TunnelNic, WiFi };

        var selected = InterfaceSelector.Select(all, null);

        Assert.Equal([Ethernet, WiFi], selected);
    }

    [Fact]
    public void Pinned_interface_wins_even_if_virtual()
    {
        var all = new[] { Ethernet, Tailscale, HyperV };

        var selected = InterfaceSelector.Select(all, ["tailscale"]);

        Assert.Equal([Tailscale], selected);
    }

    [Fact]
    public void Pinned_name_that_matches_nothing_falls_back_to_automatic_selection()
    {
        var selected = InterfaceSelector.Select([Ethernet, Tailscale], ["No Such Adapter"]);

        Assert.Equal([Ethernet], selected);
    }

    [Fact]
    public void Empty_pinned_list_means_automatic_selection()
    {
        var selected = InterfaceSelector.Select([Ethernet, Tailscale], []);

        Assert.Equal([Ethernet], selected);
    }

    [Fact]
    public void FromSystem_returns_interfaces_without_throwing()
    {
        var all = NetInterface.FromSystem();

        Assert.All(all, i => Assert.True(i.Index > 0));
    }
}
