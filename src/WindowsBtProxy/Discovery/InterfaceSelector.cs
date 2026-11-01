using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace WindowsBtProxy.Discovery;

/// <summary>A network interface reduced to what DNS-SD advertising cares about.</summary>
public sealed record NetInterface(
    int Index,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool IsUp,
    IPAddress? IPv4)
{
    /// <summary>Snapshot of the interfaces the operating system reports right now.</summary>
    public static IReadOnlyList<NetInterface> FromSystem()
    {
        var result = new List<NetInterface>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPAddress? ipv4 = null;
            var index = 0;
            try
            {
                var props = ni.GetIPProperties();
                ipv4 = PickIPv4(props);
                index = props.GetIPv4Properties()?.Index ?? 0;
            }
            catch (NetworkInformationException)
            {
                // IPv4 not enabled on this interface (or it vanished): keep it with no address.
            }

            if (index == 0)
            {
                continue; // dnsapi needs an IPv4 interface index to register on.
            }

            result.Add(new NetInterface(index, ni.Name, ni.Description, ni.NetworkInterfaceType,
                ni.OperationalStatus == OperationalStatus.Up, ipv4));
        }

        return result;
    }

    private static IPAddress? PickIPv4(IPInterfaceProperties props)
    {
        IPAddress? linkLocal = null;
        foreach (var unicast in props.UnicastAddresses)
        {
            var address = unicast.Address;
            if (address.AddressFamily != AddressFamily.InterNetwork)
            {
                continue;
            }

            var bytes = address.GetAddressBytes();
            if (bytes[0] == 169 && bytes[1] == 254)
            {
                linkLocal ??= address; // APIPA: only if nothing better exists.
                continue;
            }

            return address;
        }

        return linkLocal;
    }
}

/// <summary>Chooses the interfaces the proxy advertises itself on.</summary>
public static class InterfaceSelector
{
    private static readonly string[] VirtualMarkers =
    [
        "tailscale", "wireguard", "wintun", "hyper-v", "vethernet", "wsl", "vpn", "tap-", "zerotier", "virtualbox", "vmware",
    ];

    /// <summary>
    /// Pinned names (case-insensitive match on <see cref="NetInterface.Name"/>) win and are taken as-is. If none of them
    /// exists, or nothing is pinned, every up interface with an IPv4 address that is not loopback, tunnel or a known
    /// virtual adapter is selected.
    /// </summary>
    public static IReadOnlyList<NetInterface> Select(IEnumerable<NetInterface> all, string[]? pinned)
    {
        var list = all as IReadOnlyCollection<NetInterface> ?? all.ToList();

        if (pinned is { Length: > 0 })
        {
            var chosen = list
                .Where(i => pinned.Contains(i.Name, StringComparer.OrdinalIgnoreCase))
                .ToList();
            if (chosen.Count > 0)
            {
                return chosen;
            }
        }

        return list.Where(IsAdvertisable).ToList();
    }

    private static bool IsAdvertisable(NetInterface i)
        => i.IsUp
           && i.IPv4 is not null
           && i.Type is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
           && !IsVirtual(i);

    private static bool IsVirtual(NetInterface i)
        => VirtualMarkers.Any(m =>
            i.Name.Contains(m, StringComparison.OrdinalIgnoreCase)
            || i.Description.Contains(m, StringComparison.OrdinalIgnoreCase));
}
