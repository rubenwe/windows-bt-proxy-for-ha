using System.Buffers.Binary;
using System.Security.Cryptography;

namespace WindowsBtProxy.Crypto;

/// <summary>
/// A Noise CipherState (spec rev 34, section 5.1) for ChaChaPoly: ChaCha20-Poly1305 keyed once, with a 64-bit message
/// counter as the nonce (four zero bytes, then the counter little-endian). Each successful call advances the counter.
/// Not thread-safe: callers serialize use per direction.
/// </summary>
public sealed class CipherState : IDisposable
{
    public const int KeySize = 32;
    public const int TagSize = 16;

    private readonly ChaCha20Poly1305 _aead;
    private ulong _nonce;

    /// <summary>Creates a cipher keyed with <paramref name="key"/> (32 bytes) and counter 0. The key is copied.</summary>
    public CipherState(byte[] key)
        : this(key, 0)
    {
    }

    internal CipherState(ReadOnlySpan<byte> key, ulong initialNonce)
    {
        if (key.Length != KeySize)
        {
            throw new ArgumentException($"A ChaChaPoly key is {KeySize} bytes.", nameof(key));
        }
        _aead = new ChaCha20Poly1305(key);
        _nonce = initialNonce;
    }

    /// <summary>Encrypts with empty associated data; returns ciphertext followed by the 16-byte tag.</summary>
    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => EncryptWithAd([], plaintext);

    /// <summary>Decrypts ciphertext-plus-tag with empty associated data. Throws <see cref="CryptographicException"/> if it does not authenticate.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => DecryptWithAd([], ciphertext);

    /// <summary>Encrypts <paramref name="plaintext"/> into <paramref name="destination"/> (plaintext length + 16 bytes); returns bytes written.</summary>
    public int Encrypt(ReadOnlySpan<byte> plaintext, Span<byte> destination) => EncryptWithAd([], plaintext, destination);

    internal byte[] EncryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext)
    {
        var output = new byte[plaintext.Length + TagSize];
        EncryptWithAd(associatedData, plaintext, output);
        return output;
    }

    internal int EncryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> plaintext, Span<byte> destination)
    {
        if (destination.Length < plaintext.Length + TagSize)
        {
            throw new ArgumentException("Destination is too small for the ciphertext and tag.", nameof(destination));
        }
        Span<byte> nonce = stackalloc byte[12];
        TakeNonce(nonce);
        _aead.Encrypt(nonce, plaintext, destination[..plaintext.Length], destination.Slice(plaintext.Length, TagSize), associatedData);
        return plaintext.Length + TagSize;
    }

    internal byte[] DecryptWithAd(ReadOnlySpan<byte> associatedData, ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.Length < TagSize)
        {
            throw new CryptographicException("Ciphertext is shorter than the authentication tag.");
        }
        var plaintextLength = ciphertext.Length - TagSize;
        var plaintext = new byte[plaintextLength];
        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce);
        // On a tag mismatch the BCL zeroes the output and throws AuthenticationTagMismatchException (a CryptographicException);
        // the counter is left unchanged, as the spec requires.
        _aead.Decrypt(nonce, ciphertext[..plaintextLength], ciphertext[plaintextLength..], plaintext, associatedData);
        _nonce++;
        return plaintext;
    }

    public void Dispose() => _aead.Dispose();

    private void TakeNonce(Span<byte> nonce)
    {
        WriteNonce(nonce);
        _nonce++;
    }

    private void WriteNonce(Span<byte> nonce)
    {
        // 2^64-1 is reserved (spec 5.1); refusing it also means the counter can never wrap and reuse a nonce.
        if (_nonce == ulong.MaxValue)
        {
            throw new CryptographicException("Noise nonce exhausted; the session must be re-established.");
        }
        nonce[..4].Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _nonce);
    }
}
