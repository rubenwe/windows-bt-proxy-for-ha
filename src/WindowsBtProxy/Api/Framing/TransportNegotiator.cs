using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api.Framing;

/// <summary>How a connection's frames are carried, decided by the client's first byte.</summary>
internal enum TransportMode
{
    /// <summary>Not decided yet (the first byte has not arrived or the handshake is running).</summary>
    Pending,

    /// <summary>Plaintext frames: only served while no key is provisioned.</summary>
    Plaintext,

    /// <summary>Noise with the all-zero PSK: only while no key is provisioned; the one session type that may provision a key.</summary>
    ZeroPskNoise,

    /// <summary>Noise with the provisioned key.</summary>
    KeyedNoise,
}

/// <summary>What became of a new connection's first bytes.</summary>
internal enum NegotiationOutcome
{
    /// <summary>A transport is ready: <see cref="Negotiation.Codec"/> and <see cref="Negotiation.Mode"/> are set.</summary>
    Established,

    /// <summary>The client closed before sending its first byte.</summary>
    ClientLeft,

    /// <summary>A plaintext client reached a keyed device and was sent <c>01 00 00</c>; close the connection.</summary>
    PlaintextRefused,
}

/// <summary>The result of <see cref="TransportNegotiator.NegotiateAsync"/>.</summary>
internal readonly record struct Negotiation(NegotiationOutcome Outcome, IFrameCodec? Codec = null, TransportMode Mode = TransportMode.Pending);

/// <summary>
/// Applies the encryption policy to a new connection. The first byte picks the transport:
/// <list type="bullet">
/// <item><c>0x00</c> (plaintext): served only while no key exists; otherwise the client gets the frame <c>01 00 00</c>
/// (aioesphomeapi reports <c>RequiresEncryptionAPIError</c>) and the connection should close.</item>
/// <item><c>0x01</c> (Noise): handshake with the provisioned key, or with the all-zero PSK while none exists.</item>
/// <item>anything else: <see cref="InvalidDataException"/>.</item>
/// </list>
/// The key is read once, so a key provisioned meanwhile is not used; the caller re-checks after publishing the mode.
/// </summary>
internal static class TransportNegotiator
{
    private static readonly byte[] RequiresEncryptionFrame = [NoiseFrameCodec.Indicator, 0x00, 0x00];
    private static readonly byte[] ZeroPsk = new byte[NoiseHandshake.PskSize];

    /// <summary>
    /// Reads the first byte and sets up the transport it asks for, as the policy allows. Handshake failures throw, as in
    /// <see cref="NoiseServerHandshake"/>. The caller bounds this with the pre-authentication timeout.
    /// </summary>
    public static async Task<Negotiation> NegotiateAsync(Stream s, EncryptionState encryption, DeviceIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(encryption);
        ArgumentNullException.ThrowIfNull(identity);

        var first = await NoiseFrameCodec.ReadIndicatorAsync(s, ct).ConfigureAwait(false);
        if (first is null)
        {
            return new(NegotiationOutcome.ClientLeft);
        }

        var key = encryption.Key; // one snapshot for the whole decision
        switch (first.Value)
        {
            case PlaintextFrameCodec.Preamble when key.IsEmpty:
                return new(NegotiationOutcome.Established, new PlaintextFrameCodec(s, firstPreambleConsumed: true), TransportMode.Plaintext);

            case PlaintextFrameCodec.Preamble:
                await s.WriteAsync(RequiresEncryptionFrame, ct).ConfigureAwait(false);
                await s.FlushAsync(ct).ConfigureAwait(false);
                return new(NegotiationOutcome.PlaintextRefused);

            case NoiseFrameCodec.Indicator:
                var keyed = !key.IsEmpty;
                var codec = await NoiseServerHandshake.RunAfterIndicatorAsync(s, keyed ? key : ZeroPsk, identity.Name, identity.MacHex, ct).ConfigureAwait(false);
                return new(NegotiationOutcome.Established, codec, keyed ? TransportMode.KeyedNoise : TransportMode.ZeroPskNoise);

            default:
                throw new InvalidDataException($"First byte 0x{first.Value:X2} is neither the plaintext (0x00) nor the Noise (0x01) preamble.");
        }
    }
}
