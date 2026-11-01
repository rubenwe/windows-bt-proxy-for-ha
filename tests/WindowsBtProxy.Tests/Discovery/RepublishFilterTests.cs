using System.Net;
using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Discovery;
using WindowsBtProxy.Hosting;

namespace WindowsBtProxy.Tests.Discovery;

public sealed class RepublishFilterTests
{
    private static NetInterface Nic(int index, string ip, NetworkInterfaceType type = NetworkInterfaceType.Ethernet)
        => new(index, $"nic{index}", "test adapter", type, IsUp: true, IPAddress.Parse(ip));

    private static (RepublishFilter Filter, Func<IReadOnlyList<NetInterface>, bool, bool> Trigger, Func<int> Count) Create()
    {
        var filter = new RepublishFilter();
        var published = 0;
        return (filter, (nics, hasKey) => filter.TryPublish(nics, hasKey, () => published++), () => published);
    }

    [Fact]
    public void The_first_trigger_publishes_and_an_identical_one_does_not()
    {
        var (_, trigger, count) = Create();
        IReadOnlyList<NetInterface> nics = [Nic(5, "192.168.1.20")];

        Assert.True(trigger(nics, false));
        Assert.False(trigger([Nic(5, "192.168.1.20")], false));
        Assert.Equal(1, count());
    }

    [Fact]
    public void An_address_an_interface_a_key_or_the_network_type_change_each_publish_again()
    {
        var (_, trigger, count) = Create();
        Assert.True(trigger([Nic(5, "192.168.1.20")], false));

        Assert.True(trigger([Nic(5, "192.168.1.21")], false)); // new address
        Assert.True(trigger([Nic(5, "192.168.1.21"), Nic(7, "10.0.0.2")], false)); // interface added
        Assert.True(trigger([Nic(7, "10.0.0.2")], false)); // interface removed
        Assert.True(trigger([Nic(7, "10.0.0.2")], true)); // key provisioned
        Assert.True(trigger([Nic(7, "10.0.0.2", NetworkInterfaceType.Wireless80211)], true)); // wifi instead of ethernet

        Assert.Equal(6, count());
        Assert.False(trigger([Nic(7, "10.0.0.2", NetworkInterfaceType.Wireless80211)], true));
    }

    [Fact]
    public void The_order_the_system_lists_interfaces_in_does_not_matter()
    {
        var (_, trigger, count) = Create();
        Assert.True(trigger([Nic(5, "192.168.1.20"), Nic(7, "10.0.0.2")], false));
        Assert.False(trigger([Nic(7, "10.0.0.2"), Nic(5, "192.168.1.20")], false));
        Assert.Equal(1, count());
    }

    [Fact]
    public void A_publish_that_failed_is_tried_again_by_the_next_identical_trigger()
    {
        var filter = new RepublishFilter();
        IReadOnlyList<NetInterface> nics = [Nic(5, "192.168.1.20")];

        Assert.Throws<InvalidOperationException>(() => filter.TryPublish(nics, false, () => throw new InvalidOperationException()));

        var ran = false;
        Assert.True(filter.TryPublish(nics, false, () => ran = true));
        Assert.True(ran);
    }

    [Fact]
    public async Task Through_the_debouncer_a_burst_of_unrelated_network_events_publishes_once()
    {
        var time = new FakeTimeProvider();
        var filter = new RepublishFilter();
        var published = 0;
        IReadOnlyList<NetInterface> nics = [Nic(5, "192.168.1.20")];
        await using var debouncer = new Debouncer(TimeSpan.FromSeconds(2), () => filter.TryPublish(nics, false, () => published++), time, NullLogger.Instance);

        for (var i = 0; i < 3; i++)
        {
            debouncer.Trigger(); // e.g. NetworkAddressChanged for a DHCP renewal
            time.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        Assert.Equal(1, published);
    }
}
