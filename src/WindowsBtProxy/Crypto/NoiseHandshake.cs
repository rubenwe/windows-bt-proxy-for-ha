using System.Security.Cryptography;

namespace WindowsBtProxy.Crypto;

/// <summary>
/// The <c>Noise_NNpsk0_25519_ChaChaPoly_SHA256</c> handshake ESPHome uses for its Native API (spec rev 34):
/// <c>-&gt; psk, e</c> then <c>&lt;- e, ee</c>, prologue <c>"NoiseAPIInit\0\0"</c>, empty payloads. In a PSK handshake the
/// <c>e</c> token also calls MixKey(e.public_key), so both messages are a 32-byte ephemeral key plus a 16-byte tag.
/// </summary>
public abstract class NoiseHandshake : IDisposable
{
    public const int PskSize = 32;
    public const int KeySize = 32;
    public const int MessageSize = KeySize + CipherState.TagSize;

    private static ReadOnlySpan<byte> Prologue => "NoiseAPIInit\0\0"u8;

    private readonly SymmetricState _symmetric;
    private readonly byte[] _psk;
    private readonly X25519DiffieHellman _ephemeral;
    private readonly bool _ownsEphemeral;
    private byte[]? _remoteEphemeral;
    private int _step; // 0 = before A, 1 = A done, 2 = B done, -1 = failed mid-step (unusable)
    private bool _split;
    private bool _disposed;

    private protected NoiseHandshake(ReadOnlySpan<byte> psk32, X25519DiffieHellman? ephemeralForTests)
    {
        if (psk32.Length != PskSize)
        {
            throw new ArgumentException($"The Noise pre-shared key is {PskSize} bytes.", nameof(psk32));
        }
        _psk = psk32.ToArray();
        _symmetric = new SymmetricState(Prologue);
        // A caller-supplied key stays the caller's to dispose; a generated one is ours.
        _ephemeral = ephemeralForTests ?? X25519DiffieHellman.GenerateKey();
        _ownsEphemeral = ephemeralForTests is null;
    }

    public void Dispose()
    {
        _disposed = true;
        _symmetric.Dispose();
        CryptographicOperations.ZeroMemory(_psk);
        if (_ownsEphemeral)
        {
            _ephemeral.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>Message A, initiator side: psk, e, then EncryptAndHash(empty payload).</summary>
    private protected byte[] WriteFirstMessage()
    {
        Expect(0);
        _step = -1;
        _symmetric.MixKeyAndHash(_psk);
        var message = new byte[MessageSize];
        var e = WriteEphemeral(message);
        _symmetric.EncryptAndHash([]).CopyTo(message, e);
        _step = 1;
        return message;
    }

    /// <summary>Message A, responder side. Throws <see cref="CryptographicException"/> if it does not authenticate (wrong PSK or tampering).</summary>
    private protected void ReadFirstMessage(ReadOnlySpan<byte> message)
    {
        Expect(0);
        _step = -1;
        CheckLength(message, "A");
        _symmetric.MixKeyAndHash(_psk);
        ReadEphemeral(message[..KeySize]);
        // Any payload is accepted (ESPHome ignores it; clients send none) but it must authenticate.
        _symmetric.DecryptAndHash(message[KeySize..]);
        _step = 1;
    }

    /// <summary>Message B, responder side: e, ee, then EncryptAndHash(empty payload).</summary>
    private protected byte[] WriteSecondMessage()
    {
        Expect(1);
        _step = -1;
        var message = new byte[MessageSize];
        var e = WriteEphemeral(message);
        MixEphemeralEphemeral();
        _symmetric.EncryptAndHash([]).CopyTo(message, e);
        _step = 2;
        return message;
    }

    /// <summary>Message B, initiator side.</summary>
    private protected void ReadSecondMessage(ReadOnlySpan<byte> message)
    {
        Expect(1);
        _step = -1;
        CheckLength(message, "B");
        ReadEphemeral(message[..KeySize]);
        MixEphemeralEphemeral();
        _symmetric.DecryptAndHash(message[KeySize..]);
        _step = 2;
    }

    /// <summary>Splits the finished handshake into (initiator→responder, responder→initiator) ciphers and wipes the handshake state.</summary>
    private protected (CipherState InitiatorToResponder, CipherState ResponderToInitiator) SplitCore()
    {
        ObjectDisposedException.ThrowIf(_disposed && !_split, this);
        if (_step != 2 || _split)
        {
            throw new InvalidOperationException("Split requires a completed handshake and can be called once.");
        }
        _split = true;
        var ciphers = _symmetric.Split();
        Dispose();
        return ciphers;
    }

    private int WriteEphemeral(Span<byte> message)
    {
        var publicKey = _ephemeral.ExportPublicKey();
        publicKey.CopyTo(message);
        _symmetric.MixHash(publicKey);
        _symmetric.MixKey(publicKey); // PSK handshakes treat e like a key-mixing token (spec 9.2)
        return publicKey.Length;
    }

    private void ReadEphemeral(ReadOnlySpan<byte> publicKey)
    {
        _remoteEphemeral = publicKey.ToArray();
        _symmetric.MixHash(publicKey);
        _symmetric.MixKey(publicKey);
    }

    private void MixEphemeralEphemeral()
    {
        Span<byte> shared = stackalloc byte[KeySize];
        try
        {
            _ephemeral.DeriveRawSecretAgreement(_remoteEphemeral ?? throw new InvalidOperationException("No remote ephemeral key."), shared);
            _symmetric.MixKey(shared);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(shared);
        }
    }

    private void Expect(int step)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_split || _step != step)
        {
            throw new InvalidOperationException("Noise handshake step called out of order.");
        }
    }

    private static void CheckLength(ReadOnlySpan<byte> message, string name)
    {
        if (message.Length < MessageSize)
        {
            throw new InvalidDataException($"Noise handshake message {name} is {message.Length} bytes; at least {MessageSize} expected.");
        }
    }
}

/// <summary>Server side of the NNpsk0 handshake: read message A, write message B, split (send = responder→initiator).</summary>
public sealed class NoiseResponder(ReadOnlySpan<byte> psk32, X25519DiffieHellman? ephemeralForTests = null)
    : NoiseHandshake(psk32, ephemeralForTests)
{
    public void ReadMessageA(ReadOnlySpan<byte> m) => ReadFirstMessage(m);

    public byte[] WriteMessageB() => WriteSecondMessage();

    public (CipherState Send, CipherState Receive) Split()
    {
        var (i2r, r2i) = SplitCore();
        return (r2i, i2r);
    }
}

/// <summary>Client side of the NNpsk0 handshake (tests only): write message A, read message B, split (send = initiator→responder).</summary>
public sealed class NoiseInitiator(ReadOnlySpan<byte> psk32, X25519DiffieHellman? ephemeralForTests = null)
    : NoiseHandshake(psk32, ephemeralForTests)
{
    public byte[] WriteMessageA() => WriteFirstMessage();

    public void ReadMessageB(ReadOnlySpan<byte> m) => ReadSecondMessage(m);

    public (CipherState Send, CipherState Receive) Split() => SplitCore();
}
