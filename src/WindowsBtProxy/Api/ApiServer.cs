using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Api.Framing;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api;

/// <summary>A connected API client as the UI and diagnostics see it.</summary>
public sealed record ClientInfo(IPEndPoint Remote, string ClientInfoText, bool Subscribed, bool Encrypted);

/// <summary>
/// ESPHome Native API server: accepts clients, serves each on its own <see cref="ApiConnection"/> and fans advertisements from
/// <c>source</c> out to every subscribed client. It does not start the source; the host does.
/// Encryption policy (<see cref="TransportNegotiator"/>): plaintext and zero-PSK Noise only while <c>encryption</c> has no key;
/// when a client provisions one, every plaintext and zero-PSK session is closed and only keyed Noise is served from then on.
/// At most <see cref="MaxConnections"/> connections are open at once, counting those still negotiating (pre-authentication, so a
/// flood of idle sockets cannot exhaust the process); further sockets are closed as soon as they are accepted.
/// </summary>
public sealed class ApiServer(
    IPEndPoint endpoint,
    DeviceIdentity identity,
    IAdvertisementSource source,
    EncryptionState encryption,
    ILogger<ApiServer> log,
    TimeProvider time)
{
    /// <summary>Home Assistant opens one connection per proxy; the rest is headroom for reconnects and diagnostics.</summary>
    internal const int MaxConnections = 8;

    private readonly ConcurrentDictionary<ApiConnection, byte> _connections = new();
    private int _open; // accepted and not yet finished, including connections still negotiating
    private volatile IPEndPoint? _localEndpoint;

    /// <summary>The bound address (useful with port 0). Valid as soon as <see cref="RunAsync"/> has been called.</summary>
    public IPEndPoint LocalEndpoint =>
        _localEndpoint ?? throw new InvalidOperationException("The server has not been started.");

    /// <summary>Snapshot of the connected clients.</summary>
    public IReadOnlyList<ClientInfo> Clients => [.. _connections.Keys.Select(c => c.Info)];

    /// <summary>Raised (from any thread) when a client connects, negotiates its transport, disconnects, says hello, or (un)subscribes.</summary>
    public event Action? ClientsChanged;

    /// <summary>Time a new connection gets to send its first byte and finish the Noise handshake (pre-authentication). Set before <see cref="RunAsync"/>.</summary>
    internal TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Listens until <paramref name="ct"/> is cancelled, then closes all clients and returns. The listener is bound before the first await.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var listener = new TcpListener(endpoint);
        listener.Start();
        _localEndpoint = (IPEndPoint)listener.LocalEndpoint;
        log.LogInformation("ESPHome API listening on {Endpoint}", _localEndpoint);

        var serving = new ConcurrentDictionary<Task, byte>();
        source.Received += OnAdvertisement;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                }
                catch (SocketException e)
                {
                    log.LogWarning("Accept failed: {Reason}", e.Message);
                    await Task.Delay(TimeSpan.FromMilliseconds(200), time, ct).ConfigureAwait(false);
                    continue;
                }

                if (Interlocked.Increment(ref _open) > MaxConnections)
                {
                    Interlocked.Decrement(ref _open);
                    RefuseOverCap(client);
                    continue;
                }

                var task = ServeAsync(client, ct);
                serving.TryAdd(task, 0);
                _ = task.ContinueWith(t => serving.TryRemove(t, out _), TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        finally
        {
            source.Received -= OnAdvertisement;
            listener.Stop();
            foreach (var connection in _connections.Keys)
            {
                connection.Cancel();
            }
            await Task.WhenAll(serving.Keys).ConfigureAwait(false);
        }
    }

    private void RefuseOverCap(TcpClient client)
    {
        try
        {
            log.LogDebug("Refusing {Remote}: {Max} connections are already open", client.Client.RemoteEndPoint, MaxConnections);
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            // already gone
        }
        client.Dispose();
    }

    private void OnAdvertisement(RawAdvertisement ad)
    {
        foreach (var connection in _connections.Keys)
        {
            connection.Offer(ad);
        }
    }

    /// <summary>
    /// Right after <paramref name="provisioner"/> provisioned the key: close every other session that was not opened with it.
    /// The provisioner closes itself once it has answered.
    /// </summary>
    private void CloseOtherUnkeyedSessions(ApiConnection provisioner)
    {
        foreach (var connection in _connections.Keys)
        {
            // Sessions still negotiating re-check HasKey once their mode is published (see ApiConnection.NegotiateAsync).
            if (connection != provisioner && connection.Mode is TransportMode.Plaintext or TransportMode.ZeroPskNoise)
            {
                connection.Cancel();
            }
        }
    }

    /// <summary>Owns one client from accept to close. Never throws.</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var remote = new IPEndPoint(IPAddress.None, 0);
        ApiConnection? connection = null;
        try
        {
            // RemoteEndPoint throws if the peer already reset the connection.
            remote = client.Client.RemoteEndPoint as IPEndPoint ?? remote;
            client.NoDelay = true;
            EnableKeepAlive(client.Client, remote);
            connection = new ApiConnection(client, remote, identity, encryption, log, time, HandshakeTimeout, RaiseClientsChanged, CloseOtherUnkeyedSessions);
            _connections[connection] = 0;
            log.LogDebug("Client {Remote} connected", remote);
            RaiseClientsChanged();
            await connection.RunAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log.LogError(e, "Failed to serve client {Remote}", remote);
        }
        finally
        {
            if (connection is not null)
            {
                _connections.TryRemove(connection, out _);
                try
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    log.LogError(e, "Failed to close client {Remote}", remote);
                }
            }
            else
            {
                client.Dispose();
            }
            log.LogDebug("Client {Remote} disconnected", remote);
            Interlocked.Decrement(ref _open);
            RaiseClientsChanged();
        }
    }

    /// <summary>Detects peers that vanished without a FIN or RST (HA host crash, PC resume after sleep) within about a minute.</summary>
    private void EnableKeepAlive(Socket socket, IPEndPoint remote)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 30); // seconds idle before the first probe
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5); // seconds between probes
        }
        catch (SocketException e)
        {
            log.LogDebug("Could not enable TCP keep-alive for {Remote}: {Reason}", remote, e.Message);
        }
    }

    private void RaiseClientsChanged()
    {
        try
        {
            ClientsChanged?.Invoke();
        }
        catch (Exception e)
        {
            log.LogError(e, "A ClientsChanged handler threw");
        }
    }
}
