using System.Net.NetworkInformation;
using WindowsBtProxy.Api;

namespace WindowsBtProxy.Discovery;

/// <summary>The mDNS TXT records ESPHome nodes publish, so Home Assistant's zeroconf discovery recognises us.</summary>
public static class TxtRecords
{
    public const string NoiseProtocol = "Noise_NNpsk0_25519_ChaChaPoly_SHA256";

    /// <param name="network"><c>ethernet</c> or <c>wifi</c>; see <see cref="NetworkFor"/>.</param>
    public static IReadOnlyDictionary<string, string> Build(DeviceIdentity id, bool hasKey, string network = "ethernet")
    {
        var records = new Dictionary<string, string>
        {
            ["friendly_name"] = id.FriendlyName,
            ["version"] = "2025.6.0",
            ["mac"] = id.MacHex,
            ["platform"] = "Windows",
            ["board"] = "windows",
            ["network"] = network,
        };

        if (hasKey)
        {
            records["api_encryption"] = NoiseProtocol;
        }
        else
        {
            records["api_encryption_supported"] = NoiseProtocol;
            records["api_provisioning"] = "zero-psk";
        }

        return records;
    }

    /// <summary><c>wifi</c> if the first selected interface is wireless, otherwise <c>ethernet</c>.</summary>
    public static string NetworkFor(IReadOnlyList<NetInterface> selected)
        => selected is [{ Type: NetworkInterfaceType.Wireless80211 }, ..] ? "wifi" : "ethernet";
}
