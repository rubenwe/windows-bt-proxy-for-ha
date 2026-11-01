using System.Net;
using WindowsBtProxy.Api;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;

namespace WindowsBtProxy.Ipc;

/// <summary>Builds the <see cref="ProxyStatus"/> the status pipe serves, from the pieces the host already holds.</summary>
internal static class StatusSnapshot
{
    public static ProxyStatus Build(
        string version,
        ProxySettings settings,
        int port,
        ScannerStatus scanner,
        ulong adapterAddress,
        bool apiRunning,
        long? advertisementsLastMinute,
        bool hasKey,
        IReadOnlyList<ClientInfo> clients) =>
        new(
            version,
            settings.Name,
            settings.FriendlyName,
            port,
            settings.ScanMode,
            scanner,
            (adapterAddress & 0xFFFFFFFFFFFF) == 0 ? null : DeviceIdentity.FormatMacColon(adapterAddress),
            apiRunning,
            advertisementsLastMinute,
            hasKey ? EncryptionMode.Required : EncryptionMode.Provisionable,
            [.. clients.Select(ToStatus)]);

    private static ClientStatus ToStatus(ClientInfo client)
    {
        var address = client.Remote.Address;
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return new ClientStatus(address.ToString(), client.ClientInfoText, client.Subscribed, client.Encrypted);
    }
}
