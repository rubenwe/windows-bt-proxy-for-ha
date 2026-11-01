using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Crypto;

namespace WindowsBtProxy.Api;

/// <summary>A connected API client as the UI and diagnostics see it.</summary>
public sealed record ClientInfo(IPEndPoint Remote, string ClientInfoText, bool Subscribed, bool Encrypted);

/// <summary>
/// ESPHome Native API server: accepts clients, serves each on its own <see cref="ApiConnection"/> and fans advertisements from
/// <c>source</c> out to every subscribed client. It does not start the source; the host does.
/// </summary>
public sealed class ApiServer(
    IPEndPoint endpoint,
    DeviceIdentity identity,
    IAdvertisementSource source,
    EncryptionState encryption,
    ILogger<ApiServer> log,
    TimeProvider time)
{
    private readonly ConcurrentDictionary<ApiConnection, byte> _connections = new();
    private volatile IPEndPoint? _localEndpoint;

    /// <summary>The bound address (useful with port 0). Valid as soon as <see cref="RunAsync"/> has been called.</summary>
    public IPEndPoint LocalEndpoint =>
        _localEndpoint ?? throw new InvalidOperationException("The server has not been started.");

    /// <summary>Snapshot of the connected clients.</summary>
    public IReadOnlyList<ClientInfo> Clients => [.. _connections.Keys.Select(c => c.Info)];

    /// <summary>Raised (from any thread) when a client connects, disconnects, says hello, or (un)subscribes.</summary>
    public event Action? ClientsChanged;

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

    private void OnAdvertisement(RawAdvertisement ad)
    {
        foreach (var connection in _connections.Keys)
        {
            connection.Offer(ad);
        }
    }

    /// <summary>Owns one client from accept to close. Never throws.</summary>
    private async Task ServeAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint as IPEndPoint ?? new IPEndPoint(IPAddress.None, 0);
        ApiConnection? connection = null;
        try
        {
            client.NoDelay = true;
            connection = new ApiConnection(client, remote, identity, encryption, log, time, RaiseClientsChanged);
            _connections[connection] = 0;
            log.LogInformation("Client {Remote} connected", remote);
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
            log.LogInformation("Client {Remote} disconnected", remote);
            RaiseClientsChanged();
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
