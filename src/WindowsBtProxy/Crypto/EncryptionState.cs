using System.Security.Cryptography;

namespace WindowsBtProxy.Crypto;

/// <summary>
/// The API encryption key, if one is provisioned, and the policy for provisioning one over the network the way
/// Home Assistant does (<c>NoiseEncryptionSetKeyRequest</c> over a zero-PSK Noise session). Thread-safe: concurrent
/// <see cref="TryProvision"/> calls produce exactly one winner. Once set, the key never changes in this process.
/// </summary>
public sealed class EncryptionState
{
    private readonly KeyStore? _store;
    private readonly Lock _gate = new();
    private byte[]? _key;

    private EncryptionState(KeyStore? store, byte[]? key)
    {
        _store = store;
        _key = key;
    }

    /// <summary>Loads the key from <paramref name="store"/> (none if the file is absent). Provisioned keys are saved there. Throws <see cref="KeyStoreCorruptException"/>.</summary>
    public static EncryptionState Load(KeyStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return new(store, store.Load());
    }

    /// <summary>A state that lives in memory only, optionally with a 32-byte, not all-zero, pre-shared key already set.</summary>
    public static EncryptionState InMemory(byte[]? key = null)
    {
        if (key is not null)
        {
            CheckKey(key);
        }
        return new(null, key?.ToArray());
    }

    /// <summary>Raised (on the provisioning thread, after the key is stored) when a key is provisioned. A throwing handler is ignored.</summary>
    public event Action? KeyChanged;

    public bool HasKey => Volatile.Read(ref _key) is not null;

    /// <summary>The 32-byte key, or empty when none is provisioned. Read it once and use that snapshot.</summary>
    public ReadOnlyMemory<byte> Key => Volatile.Read(ref _key) ?? ReadOnlyMemory<byte>.Empty;

    /// <summary>
    /// Applies a <c>NoiseEncryptionSetKeyRequest</c>. Succeeds only if no key exists yet, the request arrived over a
    /// zero-PSK Noise session, and <paramref name="keyFieldBytes"/> (base64 text, as Home Assistant sends it) decodes to
    /// 32 bytes that are not all zero. An empty key (ESPHome's "clear") is refused: clearing is a local operation.
    /// On success the key is persisted before it takes effect. Store IO failures propagate and leave no key set.
    /// </summary>
    public bool TryProvision(ReadOnlySpan<byte> keyFieldBytes, bool sessionIsZeroPsk)
    {
        if (!sessionIsZeroPsk || HasKey)
        {
            return false;
        }

        Span<byte> decoded = stackalloc byte[KeyStore.KeySize];
        try
        {
            if (!LenientBase64.TryDecodeExactly(keyFieldBytes, decoded) || KeyStore.IsAllZero(decoded))
            {
                return false;
            }

            lock (_gate)
            {
                if (_key is not null)
                {
                    return false;
                }
                _store?.Save(decoded);
                // Full fence: pairs with the fence in ApiConnection after it publishes its transport mode, so either that
                // connection sees HasKey or the post-provisioning sweep sees its mode.
                Interlocked.Exchange(ref _key, decoded.ToArray());
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }

        RaiseKeyChanged();
        return true;
    }

    private void RaiseKeyChanged()
    {
        if (KeyChanged is not { } handlers)
        {
            return;
        }
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception)
            {
                // The key is already persisted and in effect; a failing observer must not make the caller report failure.
            }
        }
    }

    private static void CheckKey(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeyStore.KeySize)
        {
            throw new ArgumentException($"A Noise key is {KeyStore.KeySize} bytes.", nameof(key));
        }
        if (KeyStore.IsAllZero(key))
        {
            throw new ArgumentException("The all-zero key is reserved for unprovisioned devices.", nameof(key));
        }
    }
}
