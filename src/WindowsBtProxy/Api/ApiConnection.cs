using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api;

/// <summary>
/// One accepted client socket: reads requests, answers them, and (once the client subscribes) streams advertisements through its own batcher.
/// Replies and batcher flushes share the codec's write lock, so frames never interleave. <see cref="RunAsync"/> never throws;
/// when it returns the caller disposes the connection, which closes the socket first and the batcher second.
/// </summary>
internal sealed class ApiConnection : IAsyncDisposable
{
    internal const uint ApiVersionMajor = 1;
    internal const uint ApiVersionMinor = 13;
    internal const string EsphomeVersion = "2025.6.0";
    internal const string Manufacturer = "Windows";
    internal const uint BluetoothProxyFeatureFlags = 33; // PASSIVE_SCAN (1) | RAW_ADVERTISEMENTS (32)

    private static readonly string ServerInfo = "windows-bt-proxy " +
        (typeof(ApiConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    private readonly TcpClient _tcp;
    private readonly DeviceIdentity _identity;
    private readonly EncryptionState _encryption;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Action _changed;
    private readonly PlaintextFrameCodec _codec;
    private readonly CancellationTokenSource _lifetime = new();
    private AdvertisementBatcher? _batcher; // written by the read loop (and dispose), read by the source thread
    private string _clientInfoText = "";
    private int _disposed;

    public ApiConnection(TcpClient tcp, IPEndPoint remote, DeviceIdentity identity, EncryptionState encryption, ILogger log, TimeProvider time, Action changed)
    {
        _tcp = tcp;
        Remote = remote;
        _identity = identity;
        _encryption = encryption;
        _log = log;
        _time = time;
        _changed = changed;
        _codec = new PlaintextFrameCodec(tcp.GetStream());
    }

    public IPEndPoint Remote { get; }

    public ClientInfo Info => new(Remote, Volatile.Read(ref _clientInfoText), Volatile.Read(ref _batcher) is not null, Encrypted: false);

    /// <summary>Hands an advertisement to this client if it is subscribed.</summary>
    public void Offer(RawAdvertisement ad) => Volatile.Read(ref _batcher)?.Enqueue(ad);

    /// <summary>Asks the read loop to stop, which ends <see cref="RunAsync"/>.</summary>
    public void Cancel()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Serves the client until it disconnects, errs, or <paramref name="ct"/> is cancelled. Never throws.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            var token = linked.Token;
            while (await _codec.ReadAsync(token).ConfigureAwait(false) is { } frame)
            {
                // Parse throws InvalidProtocolBufferException on a malformed payload: a protocol error that ends this connection.
                var message = MessageRegistry.Parse(frame.MessageId, frame.Payload.Span);
                if (!await HandleAsync(frame.MessageId, message, token).ConfigureAwait(false))
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down, or the advertisement stream failed and cancelled us
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException or EndOfStreamException)
        {
            _log.LogDebug("Client {Remote} disconnected: {Reason}", Remote, e.Message);
        }
        catch (Exception e) when (e is InvalidDataException or InvalidProtocolBufferException)
        {
            _log.LogWarning("Dropping client {Remote}: protocol error: {Reason}", Remote, e.Message);
        }
        catch (Exception e)
        {
            _log.LogError(e, "Unexpected error serving client {Remote}", Remote);
        }
    }

    /// <summary>Handles one request. Returns false when the connection should close.</summary>
    private async ValueTask<bool> HandleAsync(int id, IMessage? message, CancellationToken ct)
    {
        switch (message)
        {
            case HelloRequest hello:
                Volatile.Write(ref _clientInfoText, hello.ClientInfo);
                _log.LogInformation("Client {Remote} says hello: {ClientInfo} (API {Major}.{Minor})", Remote, hello.ClientInfo, hello.ApiVersionMajor, hello.ApiVersionMinor);
                _changed();
                await _codec.WriteAsync(MessageIds.HelloResponse, new HelloResponse
                {
                    ApiVersionMajor = ApiVersionMajor,
                    ApiVersionMinor = ApiVersionMinor,
                    ServerInfo = ServerInfo,
                    Name = _identity.Name,
                }, ct).ConfigureAwait(false);
                return true;

            case DeviceInfoRequest:
                await _codec.WriteAsync(MessageIds.DeviceInfoResponse, new DeviceInfoResponse
                {
                    Name = _identity.Name,
                    FriendlyName = _identity.FriendlyName,
                    MacAddress = _identity.MacColon,
                    BluetoothMacAddress = _identity.MacColon,
                    EsphomeVersion = EsphomeVersion,
                    CompilationTime = _identity.CompilationTime,
                    Manufacturer = Manufacturer,
                    Model = _identity.Model,
                    BluetoothProxyFeatureFlags = BluetoothProxyFeatureFlags,
                    ApiEncryptionSupported = true,
                    ApiEncryptionProvisionable = !_encryption.HasKey,
                }, ct).ConfigureAwait(false);
                return true;

            case ListEntitiesRequest:
                await _codec.WriteAsync(MessageIds.ListEntitiesDoneResponse, new ListEntitiesDoneResponse(), ct).ConfigureAwait(false);
                return true;

            case PingRequest:
                await _codec.WriteAsync(MessageIds.PingResponse, new PingResponse(), ct).ConfigureAwait(false);
                return true;

            case DisconnectRequest:
                await _codec.WriteAsync(MessageIds.DisconnectResponse, new DisconnectResponse(), ct).ConfigureAwait(false);
                return false;

            case SubscribeBluetoothLEAdvertisementsRequest:
                Subscribe();
                return true;

            case UnsubscribeBluetoothLEAdvertisementsRequest:
                await UnsubscribeAsync().ConfigureAwait(false);
                return true;

            default:
                // Includes AuthenticationRequest (no password support) and every message this proxy does not implement.
                _log.LogDebug("Ignoring message {Id} ({Type}) from {Remote}", id, message?.GetType().Name ?? "unknown", Remote);
                return true;
        }
    }

    private void Subscribe()
    {
        if (Volatile.Read(ref _batcher) is not null) return;

        var batcher = new AdvertisementBatcher(FlushAsync, _time);
        Volatile.Write(ref _batcher, batcher);
        _ = WatchAsync(batcher);
        _log.LogInformation("Client {Remote} subscribed to advertisements", Remote);
        _changed();
    }

    private async Task UnsubscribeAsync()
    {
        var batcher = Interlocked.Exchange(ref _batcher, null);
        if (batcher is null) return;

        await batcher.DisposeAsync().ConfigureAwait(false);
        _log.LogInformation("Client {Remote} unsubscribed from advertisements", Remote);
        _changed();
    }

    private ValueTask FlushAsync(IReadOnlyList<RawAdvertisement> batch)
    {
        var message = new BluetoothLERawAdvertisementsResponse();
        foreach (var ad in batch)
        {
            message.Advertisements.Add(new BluetoothLERawAdvertisement
            {
                Address = ad.Address,
                Rssi = ad.Rssi,
                AddressType = ad.AddressType,
                Data = ByteString.CopyFrom(ad.Data),
            });
        }
        return _codec.WriteAsync(MessageIds.BluetoothLERawAdvertisementsResponse, message, _lifetime.Token);
    }

    /// <summary>A failing flush (dead socket) ends the connection; a batcher that was disposed normally just completes.</summary>
    private async Task WatchAsync(AdvertisementBatcher batcher)
    {
        try
        {
            await batcher.Completion.ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.LogDebug("Advertisement stream to {Remote} failed: {Reason}", Remote, e.Message);
            Cancel();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        Cancel();
        // Close the socket before the batcher: its flush takes no token and may be blocked writing to this socket.
        _tcp.Dispose();
        var batcher = Interlocked.Exchange(ref _batcher, null);
        if (batcher is not null)
        {
            await batcher.DisposeAsync().ConfigureAwait(false);
        }
        _lifetime.Dispose();
    }
}
