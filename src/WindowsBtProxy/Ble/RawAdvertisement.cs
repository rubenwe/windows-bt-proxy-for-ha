namespace WindowsBtProxy.Ble;

/// <summary>One BLE advertisement as the ESPHome raw-advertisements message carries it.</summary>
/// <param name="Address">48-bit device address in the low bits.</param>
/// <param name="Rssi">Signal strength in dBm.</param>
/// <param name="AddressType">0 = public, 1 = random.</param>
/// <param name="Data">The advertisement payload: concatenated length/type/data AD structures.</param>
public readonly record struct RawAdvertisement(ulong Address, int Rssi, uint AddressType, byte[] Data);
