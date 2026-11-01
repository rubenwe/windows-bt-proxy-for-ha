using System.Security.Cryptography;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Tests.Crypto;

public class NoiseHandshakeTests
{
    private readonly NoiseVectors _v = NoiseVectors.Load();

    [Fact]
    public void Responder_reads_vector_message_A_and_writes_vector_message_B()
    {
        using var ephemeral = X25519DiffieHellman.ImportPrivateKey(_v.ResponderEphemeralPrivate);
        using var responder = new NoiseResponder(_v.Psk, ephemeral);

        responder.ReadMessageA(_v.MessageA);

        Assert.Equal(_v.MessageB, responder.WriteMessageB());
    }

    [Fact]
    public void Initiator_writes_vector_message_A_and_reads_vector_message_B()
    {
        using var ephemeral = X25519DiffieHellman.ImportPrivateKey(_v.InitiatorEphemeralPrivate);
        using var initiator = new NoiseInitiator(_v.Psk, ephemeral);

        Assert.Equal(_v.MessageA, initiator.WriteMessageA());
        initiator.ReadMessageB(_v.MessageB);
    }

    [Fact]
    public void Split_ciphers_match_vector_transport_messages()
    {
        using var ephemeral = X25519DiffieHellman.ImportPrivateKey(_v.ResponderEphemeralPrivate);
        using var responder = new NoiseResponder(_v.Psk, ephemeral);
        responder.ReadMessageA(_v.MessageA);
        responder.WriteMessageB();

        var (send, receive) = responder.Split();
        using (send)
        using (receive)
        {
            foreach (var (plaintext, ciphertext) in _v.InitiatorToResponder)
            {
                Assert.Equal(plaintext, receive.Decrypt(ciphertext));
            }
            foreach (var (plaintext, ciphertext) in _v.ResponderToInitiator)
            {
                Assert.Equal(ciphertext, send.Encrypt(plaintext));
            }
        }
    }

    [Fact]
    public void Initiator_split_ciphers_match_vector_transport_messages()
    {
        using var ephemeral = X25519DiffieHellman.ImportPrivateKey(_v.InitiatorEphemeralPrivate);
        using var initiator = new NoiseInitiator(_v.Psk, ephemeral);
        initiator.WriteMessageA();
        initiator.ReadMessageB(_v.MessageB);

        var (send, receive) = initiator.Split();
        using (send)
        using (receive)
        {
            foreach (var (plaintext, ciphertext) in _v.InitiatorToResponder)
            {
                Assert.Equal(ciphertext, send.Encrypt(plaintext));
            }
            foreach (var (plaintext, ciphertext) in _v.ResponderToInitiator)
            {
                Assert.Equal(plaintext, receive.Decrypt(ciphertext));
            }
        }
    }

    [Fact]
    public void Wrong_psk_fails_reading_message_A()
    {
        var wrongPsk = (byte[])_v.Psk.Clone();
        wrongPsk[0] ^= 0xFF;
        using var responder = new NoiseResponder(wrongPsk);

        Assert.ThrowsAny<CryptographicException>(() => responder.ReadMessageA(_v.MessageA));
    }

    [Fact]
    public void Tampered_message_A_fails()
    {
        var tampered = (byte[])_v.MessageA.Clone();
        tampered[^1] ^= 0x01;
        using var responder = new NoiseResponder(_v.Psk);

        Assert.ThrowsAny<CryptographicException>(() => responder.ReadMessageA(tampered));
    }

    [Fact]
    public void Short_message_A_is_rejected()
    {
        using var responder = new NoiseResponder(_v.Psk);

        Assert.Throws<InvalidDataException>(() => responder.ReadMessageA(_v.MessageA.AsSpan(0, 47)));
    }

    [Fact]
    public void Initiator_and_responder_round_trip_with_random_keys()
    {
        var psk = RandomNumberGenerator.GetBytes(32);
        using var initiator = new NoiseInitiator(psk);
        using var responder = new NoiseResponder(psk);

        var a = initiator.WriteMessageA();
        responder.ReadMessageA(a);
        var b = responder.WriteMessageB();
        initiator.ReadMessageB(b);

        Assert.Equal(48, a.Length);
        Assert.Equal(48, b.Length);
        var (iSend, iReceive) = initiator.Split();
        var (rSend, rReceive) = responder.Split();
        using (iSend)
        using (iReceive)
        using (rSend)
        using (rReceive)
        {
            for (var i = 0; i < 3; i++)
            {
                var up = RandomNumberGenerator.GetBytes(10 + i);
                var down = RandomNumberGenerator.GetBytes(20 + i);
                Assert.Equal(up, rReceive.Decrypt(iSend.Encrypt(up)));
                Assert.Equal(down, iReceive.Decrypt(rSend.Encrypt(down)));
            }
        }
    }

    [Fact]
    public void Responder_steps_out_of_order_throw()
    {
        using var responder = new NoiseResponder(_v.Psk);

        Assert.Throws<InvalidOperationException>(() => responder.WriteMessageB());
        Assert.Throws<InvalidOperationException>(() => responder.Split());
    }

    [Fact]
    public void Split_can_only_be_called_once()
    {
        using var ephemeral = X25519DiffieHellman.ImportPrivateKey(_v.ResponderEphemeralPrivate);
        using var responder = new NoiseResponder(_v.Psk, ephemeral);
        responder.ReadMessageA(_v.MessageA);
        responder.WriteMessageB();
        var (send, receive) = responder.Split();
        send.Dispose();
        receive.Dispose();

        Assert.Throws<InvalidOperationException>(() => responder.Split());
    }

    [Fact]
    public void Psk_must_be_32_bytes()
    {
        Assert.Throws<ArgumentException>(() => new NoiseResponder(new byte[31]));
        Assert.Throws<ArgumentException>(() => new NoiseInitiator(new byte[33]));
    }
}
