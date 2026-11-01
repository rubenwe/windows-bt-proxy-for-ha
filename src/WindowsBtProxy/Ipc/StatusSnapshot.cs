using System.Net;
using WindowsBtProxy.Api;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;

namespace WindowsBtProxy.Ipc;

/// <summary>Builds the <see cref="ProxyStatus"/> the status pipe serves, from the pieces the host already holds.</summary>
internal static class StatusSnapshot
{
    /// <summary>The most of a client's info string that is shown.</summary>
    public const int MaxClientInfoChars = 128;

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

        return new ClientStatus(address.ToString(), Truncate(client.ClientInfoText), client.Subscribed, client.Encrypted);
    }

    /// <summary>
    /// Cuts the client's self-reported info to <see cref="MaxClientInfoChars"/>. Any device on the LAN can send any string in its
    /// hello; the tray only needs something like "Home Assistant 2026.10.4", and the status must stay well inside a pipe line.
    /// </summary>
    private static string Truncate(string info)
    {
        if (info.Length <= MaxClientInfoChars)
        {
            return info;
        }
        var length = MaxClientInfoChars;
        // Never leave half of a surrogate pair at the end.
        if (char.IsHighSurrogate(info[length - 1]))
        {
            length--;
        }
        return info[..length];
    }
}
