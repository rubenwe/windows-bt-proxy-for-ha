using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Api.Proto;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api;

/// <summary>
/// One accepted client socket: picks the transport under the encryption policy (<see cref="TransportNegotiator"/>), reads
/// requests, answers them, and (once the client subscribes) streams advertisements through its own batcher.
/// Replies and batcher flushes share the codec's write lock, so frames never interleave; any failed or cancelled write ends
/// the connection (the codec is then unusable). <see cref="RunAsync"/> never throws; when it returns the caller disposes
/// the connection, which closes the socket first, then the batcher, then the codec (and its ciphers).
/// </summary>
internal sealed class ApiConnection : IAsyncDisposable
{
    internal const uint ApiVersionMajor = 1;
    internal const uint ApiVersionMinor = 13;
    internal const string EsphomeVersion = "2025.6.0";
    internal const string Manufacturer = "Windows";
    internal const uint BluetoothProxyFeatureFlags = 33; // PASSIVE_SCAN (1) | RAW_ADVERTISEMENTS (32)

    /// <summary>How long a gracefully closing connection waits for the client to close its side (see <see cref="CloseGracefullyAsync"/>).</summary>
    private static readonly TimeSpan LingerTimeout = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long the provisioning client's success answer may take to write. That session is unkeyed, so it must not outlive
    /// the key for long; a client that stopped reading would otherwise keep it open indefinitely.
    /// </summary>
    private static readonly TimeSpan ProvisioningAnswerTimeout = TimeSpan.FromSeconds(2);

    private static readonly string ServerInfo = "windows-bt-proxy " +
        (typeof(ApiConnection).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");

    private readonly TcpClient _tcp;
    private readonly DeviceIdentity _identity;
    private readonly EncryptionState _encryption;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly Action _changed;
    private readonly Action<ApiConnection> _closeOtherUnkeyedSessions;
    private readonly TimeSpan _handshakeTimeout;
    private readonly NetworkStream _stream;
    private readonly CancellationTokenSource _lifetime = new();
    private IFrameCodec? _codec; // set by RunAsync before any request is handled or any subscription exists
    private int _mode; // TransportMode; published with a full fence, see NegotiateAsync
    private AdvertisementBatcher? _batcher; // written by the read loop (and dispose), read by the source thread
    private string _clientInfoText = "";
    private int _disposed;

    /// <param name="closeOtherUnkeyedSessions">Called as soon as this client provisioned the key: closes every other plaintext and zero-PSK session.</param>
    public ApiConnection(
        TcpClient tcp,
        IPEndPoint remote,
        DeviceIdentity identity,
        EncryptionState encryption,
        ILogger log,
        TimeProvider time,
        TimeSpan handshakeTimeout,
        Action changed,
        Action<ApiConnection> closeOtherUnkeyedSessions)
    {
        _tcp = tcp;
        Remote = remote;
        _identity = identity;
        _encryption = encryption;
        _log = log;
        _time = time;
        _handshakeTimeout = handshakeTimeout;
        _changed = changed;
        _closeOtherUnkeyedSessions = closeOtherUnkeyedSessions;
        _stream = tcp.GetStream();
    }

    public IPEndPoint Remote { get; }

    public TransportMode Mode => (TransportMode)Volatile.Read(ref _mode);

    private IFrameCodec Codec => _codec ?? throw new InvalidOperationException("The transport has not been negotiated.");

    public ClientInfo Info => new(
        Remote,
        Volatile.Read(ref _clientInfoText),
        Volatile.Read(ref _batcher) is not null,
        Encrypted: Mode is TransportMode.ZeroPskNoise or TransportMode.KeyedNoise);

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
        var closeGracefully = false;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
            closeGracefully = await ServeAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutting down, the advertisement stream failed, or a key was provisioned and this session must go
        }
        catch (TimeoutException e)
        {
            _log.LogDebug("Dropping client {Remote}: {Reason}", Remote, e.Message);
        }
        catch (NoiseAuthenticationException)
        {
            _log.LogWarning("Client {Remote} failed the Noise handshake: it uses a different encryption key", Remote);
        }
        catch (CryptographicException e)
        {
            _log.LogWarning("Dropping client {Remote}: encryption error: {Reason}", Remote, e.Message);
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

        if (closeGracefully)
        {
            await CloseGracefullyAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Negotiates the transport, then answers requests. Returns true when the server ends the session on purpose (close gracefully).</summary>
    private async Task<bool> ServeAsync(CancellationToken ct)
    {
        if (!await NegotiateAsync(ct).ConfigureAwait(false))
        {
            return true;
        }

        var codec = Codec;
        while (await codec.ReadAsync(ct).ConfigureAwait(false) is { } frame)
        {
            // Parse throws InvalidProtocolBufferException on a malformed payload: a protocol error that ends this connection.
            var message = MessageRegistry.Parse(frame.MessageId, frame.Payload.Span);
            if (!await HandleAsync(frame.MessageId, message, ct).ConfigureAwait(false))
            {
                return true;
            }
        }
        return false; // the client closed
    }

    /// <summary>Picks the transport within the pre-authentication timeout. False when the connection should close.</summary>
    private async Task<bool> NegotiateAsync(CancellationToken ct)
    {
        Negotiation transport;
        using (var timeout = new CancellationTokenSource(_handshakeTimeout, _time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
        {
            try
            {
                transport = await TransportNegotiator.NegotiateAsync(_stream, _encryption, _identity, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"no completed handshake within {_handshakeTimeout.TotalSeconds:0.#} s");
            }
        }

        switch (transport.Outcome)
        {
            case NegotiationOutcome.ClientLeft:
                return false;
            case NegotiationOutcome.PlaintextRefused:
                _log.LogInformation("Refused plaintext client {Remote}: this device requires encryption", Remote);
                return false;
        }

        _codec = transport.Codec;
        var mode = transport.Mode;
        // Full fence, paired with the one in EncryptionState.TryProvision: either we see the new key here, or the
        // provisioning sweep sees this mode and closes us. A session negotiated without a key never outlives it.
        Interlocked.Exchange(ref _mode, (int)mode);
        if (mode != TransportMode.KeyedNoise && _encryption.HasKey)
        {
            _log.LogInformation("Closing {Mode} client {Remote}: a key was provisioned while it connected", mode, Remote);
            return false;
        }

        _log.LogDebug("Client {Remote} uses {Mode}", Remote, mode);
        _changed();
        return true;
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
                await Codec.WriteAsync(MessageIds.HelloResponse, new HelloResponse
                {
                    ApiVersionMajor = ApiVersionMajor,
                    ApiVersionMinor = ApiVersionMinor,
                    ServerInfo = ServerInfo,
                    Name = _identity.Name,
                }, ct).ConfigureAwait(false);
                return true;

            case DeviceInfoRequest:
                await Codec.WriteAsync(MessageIds.DeviceInfoResponse, new DeviceInfoResponse
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
                await Codec.WriteAsync(MessageIds.ListEntitiesDoneResponse, new ListEntitiesDoneResponse(), ct).ConfigureAwait(false);
                return true;

            case PingRequest:
                await Codec.WriteAsync(MessageIds.PingResponse, new PingResponse(), ct).ConfigureAwait(false);
                return true;

            case DisconnectRequest:
                await Codec.WriteAsync(MessageIds.DisconnectResponse, new DisconnectResponse(), ct).ConfigureAwait(false);
                return false;

            case NoiseEncryptionSetKeyRequest setKey:
                return await SetKeyAsync(setKey, ct).ConfigureAwait(false);

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

    /// <summary>
    /// Home Assistant's provisioning request. On success every other unkeyed session is closed at once, before anything
    /// that could fail or block; this session then gets its answer (bounded by <see cref="ProvisioningAnswerTimeout"/>) and
    /// closes too, whether or not the answer could be written.
    /// </summary>
    private async ValueTask<bool> SetKeyAsync(NoiseEncryptionSetKeyRequest request, CancellationToken ct)
    {
        var mode = Mode;
        bool success;
        try
        {
            success = _encryption.TryProvision(request.Key.Span, sessionIsZeroPsk: mode == TransportMode.ZeroPskNoise);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.LogError("Could not store the encryption key from client {Remote}: {Reason}", Remote, e.Message);
            success = false;
        }

        // Never log the key field itself.
        if (success)
        {
            _log.LogInformation("Client {Remote} provisioned an encryption key; the API is Noise-only from now on", Remote);
        }
        else
        {
            _log.LogWarning("Refused an encryption key from client {Remote} ({Mode} session, key already set: {HasKey})", Remote, mode, _encryption.HasKey);
        }

        if (!success)
        {
            await Codec.WriteAsync(MessageIds.NoiseEncryptionSetKeyResponse, new NoiseEncryptionSetKeyResponse { Success = false }, ct).ConfigureAwait(false);
            return true;
        }

        // The key is in effect: no other plaintext or zero-PSK session may keep being served, whatever happens below.
        _closeOtherUnkeyedSessions(this);
        try
        {
            using var bound = new CancellationTokenSource(ProvisioningAnswerTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, bound.Token);
            try
            {
                await Codec.WriteAsync(MessageIds.NoiseEncryptionSetKeyResponse, new NoiseEncryptionSetKeyResponse { Success = true }, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (bound.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new TimeoutException($"the provisioning answer could not be written within {ProvisioningAnswerTimeout.TotalSeconds:0.#} s");
            }
            return false; // answered: close gracefully so the answer is not lost
        }
        finally
        {
            // This session is unkeyed too: it ends on every path, including a failed or timed-out answer.
            Cancel();
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
        return Codec.WriteAsync(MessageIds.BluetoothLERawAdvertisementsResponse, message, _lifetime.Token);
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

    /// <summary>
    /// Ends a session the server chose to end (Disconnect, refused plaintext, provisioning) without losing the last answer:
    /// send FIN, then read and discard until the client closes or <see cref="LingerTimeout"/> passes. Closing with unread
    /// input would send a reset, which can make the client drop data it has not read yet. Never throws.
    /// </summary>
    private async Task CloseGracefullyAsync(CancellationToken ct)
    {
        try
        {
            _tcp.Client.Shutdown(SocketShutdown.Send);
            using var linger = new CancellationTokenSource(LingerTimeout, _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, linger.Token);
            var sink = new byte[256];
            while (await _stream.ReadAsync(sink, linked.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
        {
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
        // Read loop and batcher are done: nothing uses the codec any more, so its ciphers can go.
        (_codec as IDisposable)?.Dispose();
        _lifetime.Dispose();
    }
}
