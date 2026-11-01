using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;

namespace WindowsBtProxy.Ipc;

/// <summary>How the API treats clients that do not use encryption.</summary>
/// <remarks>Mirrors the API server: with no key stored, clients may provision one over plaintext; once a key is stored, only encrypted clients are accepted.</remarks>
public enum EncryptionMode
{
    /// <summary>No key stored yet; Home Assistant may still provision one.</summary>
    Provisionable,

    /// <summary>A key is stored; only encrypted clients are accepted.</summary>
    Required,
}

/// <summary>One connected API client, reduced to what the tray shows.</summary>
/// <param name="Address">The client's IP address only (an IPv4-mapped IPv6 address appears as plain IPv4); the port is ephemeral noise.</param>
/// <param name="Info">The client's self-reported info string (for example the Home Assistant version).</param>
/// <param name="Subscribed">Whether the client has subscribed to Bluetooth advertisements.</param>
/// <param name="Encrypted">Whether the connection uses the Noise transport.</param>
public sealed record ClientStatus(string Address, string Info, bool Subscribed, bool Encrypted);

/// <summary>A point-in-time view of the proxy, served over the local status pipe to the tray app.</summary>
/// <param name="Version">The service's version string.</param>
/// <param name="Name">The ESPHome node name.</param>
/// <param name="FriendlyName">The display name shown in Home Assistant.</param>
/// <param name="Port">The ESPHome API TCP port.</param>
/// <param name="ScanMode">Passive or active scanning.</param>
/// <param name="Scanner">State of the Bluetooth scanner.</param>
/// <param name="AdapterMac">The local adapter's address as <c>02:11:22:33:44:55</c>, or null when there is no adapter.</param>
/// <param name="ApiRunning">Whether the API server is listening.</param>
/// <param name="AdvertisementsLastMinute">Advertisements seen in the last minute, or null when not measured.</param>
/// <param name="Encryption">Whether a Noise key is stored.</param>
/// <param name="Clients">Currently connected API clients.</param>
public sealed record ProxyStatus(
    string Version,
    string Name,
    string FriendlyName,
    int Port,
    ScanMode ScanMode,
    ScannerStatus Scanner,
    string? AdapterMac,
    bool ApiRunning,
    long? AdvertisementsLastMinute,
    EncryptionMode Encryption,
    IReadOnlyList<ClientStatus> Clients);
