using System.Security.Cryptography;

namespace WindowsBtProxy.Crypto;

/// <summary>The client's Noise handshake did not authenticate: almost always a client configured with a different encryption key.</summary>
public sealed class NoiseAuthenticationException : CryptographicException
{
    public NoiseAuthenticationException()
        : this("The Noise handshake did not authenticate (wrong encryption key?).")
    {
    }

    public NoiseAuthenticationException(string message)
        : base(message)
    {
    }

    public NoiseAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
