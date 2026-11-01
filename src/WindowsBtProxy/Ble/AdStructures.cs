namespace WindowsBtProxy.Ble;

/// <summary>Builds BLE advertisement payloads from AD structures (the format ESPHome's raw advertisements carry).</summary>
public static class AdStructures
{
    /// <summary>Concatenates <c>[length][type][data]</c> for each section, where length = 1 + data length.</summary>
    /// <exception cref="ArgumentException">A section's data is longer than 254 bytes, so its length does not fit in one byte.</exception>
    public static byte[] Build(IEnumerable<(byte Type, ReadOnlyMemory<byte> Data)> sections)
    {
        var result = new List<byte>();
        foreach (var (type, data) in sections)
        {
            if (data.Length > byte.MaxValue - 1)
            {
                throw new ArgumentException($"AD structure data is {data.Length} bytes; the maximum is {byte.MaxValue - 1}.", nameof(sections));
            }

            result.Add((byte)(data.Length + 1));
            result.Add(type);
            result.AddRange(data.ToArray());
        }

        return result.ToArray();
    }
}
