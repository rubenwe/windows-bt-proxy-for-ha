namespace WindowsBtProxy.Api;

/// <summary>What this proxy tells Home Assistant about itself.</summary>
/// <param name="Name">mDNS / ESPHome node name, e.g. <c>windows-bt-proxy-334455</c>.</param>
/// <param name="FriendlyName">Display name shown in Home Assistant.</param>
/// <param name="BluetoothAddress">The local adapter's 48-bit address in the low bits.</param>
/// <param name="Model">Reported as the device model (the machine name).</param>
/// <param name="CompilationTime">Reported build time string.</param>
public sealed record DeviceIdentity(string Name, string FriendlyName, ulong BluetoothAddress, string Model, string CompilationTime)
{
    /// <summary>Upper-case colon-separated address, e.g. <c>02:11:22:33:44:55</c>.</summary>
    public string MacColon
    {
        get
        {
            var hex = MacHex.ToUpperInvariant();
            return $"{hex[0..2]}:{hex[2..4]}:{hex[4..6]}:{hex[6..8]}:{hex[8..10]}:{hex[10..12]}";
        }
    }

    /// <summary>Lower-case hex address without separators, 12 characters, e.g. <c>021122334455</c>.</summary>
    public string MacHex => (BluetoothAddress & 0xFFFFFFFFFFFF).ToString("x12");
}
