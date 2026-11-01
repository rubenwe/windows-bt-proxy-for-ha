using System.Text.Json;

namespace WindowsBtProxy.Tests.Crypto;

/// <summary>Vectors from tests/vectors/noise_nnpsk0.json, produced by the reference <c>noiseprotocol</c> library.</summary>
internal sealed class NoiseVectors
{
    public required byte[] Psk { get; init; }
    public required byte[] InitiatorEphemeralPrivate { get; init; }
    public required byte[] ResponderEphemeralPrivate { get; init; }
    public required byte[] MessageA { get; init; }
    public required byte[] MessageB { get; init; }
    public required (byte[] Plaintext, byte[] Ciphertext)[] InitiatorToResponder { get; init; }
    public required (byte[] Plaintext, byte[] Ciphertext)[] ResponderToInitiator { get; init; }

    public static NoiseVectors Load()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "vectors", "noise_nnpsk0.json")));
        var root = doc.RootElement;
        byte[] Hex(string name) => Convert.FromHexString(root.GetProperty(name).GetString()!);
        (byte[], byte[])[] Pairs(string name) => root.GetProperty(name).EnumerateArray()
            .Select(e => (Convert.FromHexString(e.GetProperty("plaintext").GetString()!), Convert.FromHexString(e.GetProperty("ciphertext").GetString()!)))
            .ToArray();
        return new NoiseVectors
        {
            Psk = Hex("psk"),
            InitiatorEphemeralPrivate = Hex("initiator_ephemeral_private"),
            ResponderEphemeralPrivate = Hex("responder_ephemeral_private"),
            MessageA = Hex("message_a"),
            MessageB = Hex("message_b"),
            InitiatorToResponder = Pairs("initiator_to_responder"),
            ResponderToInitiator = Pairs("responder_to_initiator"),
        };
    }
}
