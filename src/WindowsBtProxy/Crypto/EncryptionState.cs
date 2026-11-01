namespace WindowsBtProxy.Crypto;

/// <summary>Whether API encryption is provisioned. Minimal shape for now; loading, provisioning and change notification arrive with the Noise work.</summary>
public sealed class EncryptionState
{
    private readonly byte[]? _key;

    private EncryptionState(byte[]? key) => _key = key;

    /// <summary>A state that lives in memory only, optionally with a 32-byte pre-shared key already set.</summary>
    public static EncryptionState InMemory(byte[]? key = null) => new(key);

    public bool HasKey => _key is not null;
}
