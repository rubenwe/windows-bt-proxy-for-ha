using System.Security.Cryptography;
using System.Text;

namespace WindowsBtProxy.Crypto;

/// <summary>
/// Noise SymmetricState (spec rev 34, section 5.2) for <c>Noise_NNpsk0_25519_ChaChaPoly_SHA256</c>: chaining key,
/// handshake hash and the handshake CipherState. HKDF is the spec's HMAC-SHA256 construction, which is exactly
/// RFC 5869 extract (salt = ck) then expand with empty info.
/// </summary>
internal sealed class SymmetricState : IDisposable
{
    public const string ProtocolName = "Noise_NNpsk0_25519_ChaChaPoly_SHA256";
    private const int HashLen = 32;

    private readonly byte[] _ck = new byte[HashLen];
    private readonly byte[] _h = new byte[HashLen];
    private CipherState? _k;

    public SymmetricState(ReadOnlySpan<byte> prologue)
    {
        // InitializeSymmetric: the 36-byte name is longer than HASHLEN, so h = HASH(name). ck = h; no key yet.
        var name = Encoding.ASCII.GetBytes(ProtocolName);
        if (name.Length <= HashLen)
        {
            name.CopyTo(_h, 0); // zero-padded; unreachable for this protocol name but keeps the rule explicit
        }
        else
        {
            SHA256.HashData(name, _h);
        }
        _h.CopyTo(_ck, 0);
        MixHash(prologue);
    }

    /// <summary>h = HASH(h || data).</summary>
    public void MixHash(ReadOnlySpan<byte> data)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(_h);
        hash.AppendData(data);
        hash.GetHashAndReset(_h);
    }

    /// <summary>ck, tempK = HKDF(ck, ikm, 2); k = tempK; n = 0.</summary>
    public void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        Span<byte> output = stackalloc byte[2 * HashLen];
        try
        {
            Hkdf(inputKeyMaterial, output);
            output[..HashLen].CopyTo(_ck);
            InitializeKey(output[HashLen..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    /// <summary>ck, tempH, tempK = HKDF(ck, ikm, 3); MixHash(tempH); k = tempK; n = 0. Used for the psk token.</summary>
    public void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
    {
        Span<byte> output = stackalloc byte[3 * HashLen];
        try
        {
            Hkdf(inputKeyMaterial, output);
            output[..HashLen].CopyTo(_ck);
            MixHash(output.Slice(HashLen, HashLen));
            InitializeKey(output[(2 * HashLen)..]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    /// <summary>Encrypts with ad = h once a key is set (plaintext passes through otherwise), then mixes the ciphertext into h.</summary>
    public byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ciphertext = _k is null ? plaintext.ToArray() : _k.EncryptWithAd(_h, plaintext);
        MixHash(ciphertext);
        return ciphertext;
    }

    /// <summary>Inverse of <see cref="EncryptAndHash"/>; throws <see cref="CryptographicException"/> if the tag does not verify.</summary>
    public byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var plaintext = _k is null ? ciphertext.ToArray() : _k.DecryptWithAd(_h, ciphertext);
        MixHash(ciphertext);
        return plaintext;
    }

    /// <summary>tempK1, tempK2 = HKDF(ck, empty, 2): the initiator-to-responder and responder-to-initiator ciphers.</summary>
    public (CipherState InitiatorToResponder, CipherState ResponderToInitiator) Split()
    {
        Span<byte> output = stackalloc byte[2 * HashLen];
        try
        {
            Hkdf([], output);
            var c1 = new CipherState(output[..HashLen], 0);
            var c2 = new CipherState(output[HashLen..], 0);
            return (c1, c2);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(output);
        }
    }

    public void Dispose()
    {
        _k?.Dispose();
        _k = null;
        CryptographicOperations.ZeroMemory(_ck);
        CryptographicOperations.ZeroMemory(_h);
    }

    private void InitializeKey(ReadOnlySpan<byte> key)
    {
        _k?.Dispose();
        _k = new CipherState(key, 0);
    }

    private void Hkdf(ReadOnlySpan<byte> inputKeyMaterial, Span<byte> output) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, inputKeyMaterial, output, salt: _ck, info: []);
}
