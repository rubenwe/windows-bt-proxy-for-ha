using System.Reflection;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using WindowsBtProxy.Api;
using WindowsBtProxy.Api.Proto;

namespace WindowsBtProxy.Tests.Api;

public class MessageIdsTests
{
    [Fact]
    public void Every_constant_matches_the_proto_id_option()
    {
        var constants = typeof(MessageIds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(int))
            .ToList();
        Assert.Equal(16, constants.Count);

        var messageTypes = typeof(HelloRequest).Assembly.GetTypes()
            .Where(t => typeof(IMessage).IsAssignableFrom(t) && !t.IsAbstract)
            .ToDictionary(t => t.Name);

        foreach (var constant in constants)
        {
            Assert.True(messageTypes.TryGetValue(constant.Name, out var type), $"no generated message named {constant.Name}");
            var descriptor = (MessageDescriptor)type!.GetProperty("Descriptor", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;
            var id = descriptor.GetOptions().GetExtension(ApiOptionsExtensions.Id);
            Assert.Equal((int)id, (int)constant.GetRawConstantValue()!);
        }
    }

    [Theory]
    [InlineData(MessageIds.HelloRequest)]
    [InlineData(MessageIds.HelloResponse)]
    [InlineData(MessageIds.AuthenticationRequest)]
    [InlineData(MessageIds.DisconnectRequest)]
    [InlineData(MessageIds.DisconnectResponse)]
    [InlineData(MessageIds.PingRequest)]
    [InlineData(MessageIds.PingResponse)]
    [InlineData(MessageIds.DeviceInfoRequest)]
    [InlineData(MessageIds.DeviceInfoResponse)]
    [InlineData(MessageIds.ListEntitiesRequest)]
    [InlineData(MessageIds.ListEntitiesDoneResponse)]
    [InlineData(MessageIds.SubscribeBluetoothLEAdvertisementsRequest)]
    [InlineData(MessageIds.UnsubscribeBluetoothLEAdvertisementsRequest)]
    [InlineData(MessageIds.BluetoothLERawAdvertisementsResponse)]
    [InlineData(MessageIds.NoiseEncryptionSetKeyRequest)]
    [InlineData(MessageIds.NoiseEncryptionSetKeyResponse)]
    public void Registry_round_trips_every_message_id(int id)
    {
        var message = MessageRegistry.Parse(id, ReadOnlySpan<byte>.Empty);
        Assert.NotNull(message);
        Assert.Equal(id, MessageRegistry.IdOf(message));
    }

    [Fact]
    public void Registry_round_trips_a_message()
    {
        var hello = new HelloRequest { ClientInfo = "x", ApiVersionMajor = 1, ApiVersionMinor = 10 };
        Assert.Equal(MessageIds.HelloRequest, MessageRegistry.IdOf(hello));

        var parsed = Assert.IsType<HelloRequest>(MessageRegistry.Parse(MessageIds.HelloRequest, hello.ToByteArray()));
        Assert.Equal("x", parsed.ClientInfo);
        Assert.Equal(10u, parsed.ApiVersionMinor);
    }

    [Fact]
    public void Registry_returns_null_for_unknown_ids()
    {
        Assert.Null(MessageRegistry.Parse(9999, ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Registry_parses_empty_payload_messages()
    {
        Assert.IsType<PingRequest>(MessageRegistry.Parse(MessageIds.PingRequest, ReadOnlySpan<byte>.Empty));
    }
}
