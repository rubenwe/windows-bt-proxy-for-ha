using Google.Protobuf;

namespace WindowsBtProxy.Api;

// AuthenticationRequest is marked deprecated in api.proto (removed upstream in ESPHome 2026.1) but is still id 3 on the wire,
// and is part of the brief's required id set.
#pragma warning disable CS0612

/// <summary>Maps between ESPHome message ids and the generated protobuf types (no reflection, AOT-safe).</summary>
public static class MessageRegistry
{
    /// <summary>Returns the wire id of <paramref name="m"/>. Throws <see cref="ArgumentException"/> for a type this service does not speak.</summary>
    public static int IdOf(IMessage m) => m switch
    {
        HelloRequest => MessageIds.HelloRequest,
        HelloResponse => MessageIds.HelloResponse,
        AuthenticationRequest => MessageIds.AuthenticationRequest,
        DisconnectRequest => MessageIds.DisconnectRequest,
        DisconnectResponse => MessageIds.DisconnectResponse,
        PingRequest => MessageIds.PingRequest,
        PingResponse => MessageIds.PingResponse,
        DeviceInfoRequest => MessageIds.DeviceInfoRequest,
        DeviceInfoResponse => MessageIds.DeviceInfoResponse,
        ListEntitiesRequest => MessageIds.ListEntitiesRequest,
        ListEntitiesDoneResponse => MessageIds.ListEntitiesDoneResponse,
        SubscribeBluetoothLEAdvertisementsRequest => MessageIds.SubscribeBluetoothLEAdvertisementsRequest,
        UnsubscribeBluetoothLEAdvertisementsRequest => MessageIds.UnsubscribeBluetoothLEAdvertisementsRequest,
        BluetoothLERawAdvertisementsResponse => MessageIds.BluetoothLERawAdvertisementsResponse,
        NoiseEncryptionSetKeyRequest => MessageIds.NoiseEncryptionSetKeyRequest,
        NoiseEncryptionSetKeyResponse => MessageIds.NoiseEncryptionSetKeyResponse,
        _ => throw new ArgumentException($"No message id registered for {m.GetType().Name}.", nameof(m)),
    };

    /// <summary>Parses <paramref name="payload"/> as message <paramref name="id"/>; returns null if the id is not one this service speaks.</summary>
    public static IMessage? Parse(int id, ReadOnlySpan<byte> payload) => id switch
    {
        MessageIds.HelloRequest => HelloRequest.Parser.ParseFrom(payload),
        MessageIds.HelloResponse => HelloResponse.Parser.ParseFrom(payload),
        MessageIds.AuthenticationRequest => AuthenticationRequest.Parser.ParseFrom(payload),
        MessageIds.DisconnectRequest => DisconnectRequest.Parser.ParseFrom(payload),
        MessageIds.DisconnectResponse => DisconnectResponse.Parser.ParseFrom(payload),
        MessageIds.PingRequest => PingRequest.Parser.ParseFrom(payload),
        MessageIds.PingResponse => PingResponse.Parser.ParseFrom(payload),
        MessageIds.DeviceInfoRequest => DeviceInfoRequest.Parser.ParseFrom(payload),
        MessageIds.DeviceInfoResponse => DeviceInfoResponse.Parser.ParseFrom(payload),
        MessageIds.ListEntitiesRequest => ListEntitiesRequest.Parser.ParseFrom(payload),
        MessageIds.ListEntitiesDoneResponse => ListEntitiesDoneResponse.Parser.ParseFrom(payload),
        MessageIds.SubscribeBluetoothLEAdvertisementsRequest => SubscribeBluetoothLEAdvertisementsRequest.Parser.ParseFrom(payload),
        MessageIds.UnsubscribeBluetoothLEAdvertisementsRequest => UnsubscribeBluetoothLEAdvertisementsRequest.Parser.ParseFrom(payload),
        MessageIds.BluetoothLERawAdvertisementsResponse => BluetoothLERawAdvertisementsResponse.Parser.ParseFrom(payload),
        MessageIds.NoiseEncryptionSetKeyRequest => NoiseEncryptionSetKeyRequest.Parser.ParseFrom(payload),
        MessageIds.NoiseEncryptionSetKeyResponse => NoiseEncryptionSetKeyResponse.Parser.ParseFrom(payload),
        _ => null,
    };
}
