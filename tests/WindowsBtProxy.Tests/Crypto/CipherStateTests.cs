using System.Security.Cryptography;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Crypto;

public class CipherStateTests
{
    [Fact]
    public void Nonce_is_little_endian_counter_after_four_zero_bytes()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var cipher = new CipherState(key);
        using var aead = new ChaCha20Poly1305(key);

        cipher.Encrypt([1]);
        var actual = cipher.Encrypt([2, 3]);

        var nonce = new byte[12];
        nonce[4] = 1; // counter = 1: four zero bytes, then the 64-bit counter little-endian
        var expected = new byte[2 + 16];
        aead.Encrypt(nonce, [2, 3], expected.AsSpan(0, 2), expected.AsSpan(2), []);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Failed_decrypt_throws_CryptographicException()
    {
        using var cipher = new CipherState(RandomNumberGenerator.GetBytes(32));

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(new byte[20]));
    }

    [Fact]
    public void Ciphertext_shorter_than_tag_throws_CryptographicException()
    {
        using var cipher = new CipherState(RandomNumberGenerator.GetBytes(32));

        Assert.ThrowsAny<CryptographicException>(() => cipher.Decrypt(new byte[15]));
    }

    [Fact]
    public void Nonce_two_to_the_64_minus_one_is_never_used()
    {
        using var cipher = new CipherState(RandomNumberGenerator.GetBytes(32), ulong.MaxValue - 1);

        cipher.Encrypt([1]);
        Assert.ThrowsAny<CryptographicException>(() => cipher.Encrypt([2]));
    }

    [Fact]
    public void Key_must_be_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => new CipherState(new byte[16]));
    }
}
