namespace WindowsBtProxy.Api;

/// <summary>ESPHome Native API message ids (the <c>(id)</c> option in api.proto) for the messages this service speaks.</summary>
public static class MessageIds
{
    public const int HelloRequest = 1;
    public const int HelloResponse = 2;
    public const int AuthenticationRequest = 3;
    public const int DisconnectRequest = 5;
    public const int DisconnectResponse = 6;
    public const int PingRequest = 7;
    public const int PingResponse = 8;
    public const int DeviceInfoRequest = 9;
    public const int DeviceInfoResponse = 10;
    public const int ListEntitiesRequest = 11;
    public const int ListEntitiesDoneResponse = 19;
    public const int SubscribeBluetoothLEAdvertisementsRequest = 66;
    public const int UnsubscribeBluetoothLEAdvertisementsRequest = 87;
    public const int BluetoothLERawAdvertisementsResponse = 93;
    public const int NoiseEncryptionSetKeyRequest = 124;
    public const int NoiseEncryptionSetKeyResponse = 125;
}
