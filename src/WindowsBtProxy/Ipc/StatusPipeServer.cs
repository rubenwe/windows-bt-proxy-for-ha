using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Ipc;

/// <summary>
/// Serves the local status pipe: one request line, one response line, hang up. Status goes to anyone the ACL lets in
/// (<see cref="PipeAcl"/>); the encryption key only to elevated administrators.
/// </summary>
/// <param name="pipeName">The pipe name; <see cref="PipeProtocol.DefaultPipeName"/> in production, a unique name in tests.</param>
/// <param name="status">Builds the status to answer with; called once per <c>status</c> request.</param>
/// <param name="key">Reads the stored key; called only after the caller has been found to be an administrator.</param>
/// <param name="log">Where refusals and dropped connections are reported.</param>
/// <param name="isAdministrator">Decides whether the connected client is an elevated administrator; <see cref="ClientToken.IsAdministrator"/> by default. A test seam.</param>
public sealed class StatusPipeServer(
    string pipeName,
    Func<ProxyStatus> status,
    Func<KeyReply> key,
    ILogger log,
    Func<NamedPipeServerStream, bool>? isAdministrator = null)
{
    private const int MaxInstances = 4;

    private readonly Func<NamedPipeServerStream, bool> _isAdministrator = isAdministrator ?? ClientToken.IsAdministrator;

    /// <summary>
    /// Serves until <paramref name="ct"/> is cancelled, then returns true once every connection has been closed. Returns false at
    /// once (after logging a warning) when the pipe cannot be created, which normally means another process already owns the name.
    /// </summary>
    /// <remarks>
    /// The first instance is created before this method first yields, with <see cref="PipeOptions.FirstPipeInstance"/>, so a pipe
    /// someone else created first is never joined. Each later instance is created while an earlier one is still open, so the name
    /// never lapses: if it did, anyone could create it anew (with their own ACL) and receive our clients' requests.
    /// </remarks>
    public async Task<bool> RunAsync(CancellationToken ct)
    {
        var security = PipeAcl.Create();
        // One slot per pipe instance, listening or connected; the system enforces the same maximum.
        using var slots = new SemaphoreSlim(MaxInstances);
        slots.Wait(CancellationToken.None); // the first instance's; always free here
        NamedPipeServerStream listener;
        try
        {
            listener = CreateInstance(security, first: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Not serving status: could not create the pipe {PipeName} ({Reason}). Another copy of the service probably owns it.", pipeName, e.Message);
            return false;
        }

        try
        {
            while (true)
            {
                var accepted = true;
                try
                {
                    await listener.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (IOException e)
                {
                    // A client that came and went before the instance was listening leaves the instance unusable; replace it.
                    log.LogDebug("Status pipe connection failed: {Reason}", e.Message);
                    accepted = false;
                }

                // The next instance first, while this one still holds the name (see remarks). Until a slot frees up, the
                // connected client waits; every handler ends within a few seconds.
                await slots.WaitAsync(ct).ConfigureAwait(false);
                NamedPipeServerStream next;
                try
                {
                    next = CreateInstance(security, first: false);
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                var current = listener;
                listener = next;
                if (accepted)
                {
                    _ = Task.Run(() => HandleAsync(current, slots, ct), CancellationToken.None);
                }
                else
                {
                    current.Dispose();
                    slots.Release();
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            // Whichever instance the loop still holds, connected or not, has not been handed to a handler.
            listener.Dispose();
            slots.Release();
            // Every handler gives its slot back when it is done, so holding all of them means none is still running.
            for (var i = 0; i < MaxInstances; i++)
            {
                await slots.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        return true;
    }

    private NamedPipeServerStream CreateInstance(PipeSecurity security, bool first) =>
        NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 0,
            outBufferSize: 0,
            security);

    /// <summary>Answers one connection, then closes it and frees its slot. Never throws: one bad client must not stop the server.</summary>
    private async Task HandleAsync(NamedPipeServerStream pipe, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            string? line;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(PipeProtocol.RequestTimeout);
                line = await PipeProtocol.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
            if (line is null)
            {
                return;
            }

            var response = Answer(line, pipe);

            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                deadline.CancelAfter(PipeProtocol.RequestTimeout);
                await PipeProtocol.WriteLineAsync(pipe, JsonSerializer.Serialize(response, IpcJsonContext.Default.PipeResponse), PipeProtocol.MaxResponseBytes, deadline.Token).ConfigureAwait(false);
                await WaitForHangUpAsync(pipe, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception e)
        {
            log.LogDebug("Status pipe client dropped: {Reason}", e.Message);
        }
        finally
        {
            pipe.Dispose();
            slots.Release();
        }
    }

    /// <summary>
    /// Waits until the client has closed its end, which it does once it has read the answer. Closing first could discard an answer
    /// still in the pipe. This stands in for <see cref="PipeStream.WaitForPipeDrain"/>, which blocks a thread with no time limit
    /// for as long as a client declines to read; this is asynchronous and gives up at <paramref name="ct"/>.
    /// </summary>
    private static async Task WaitForHangUpAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var sink = new byte[64];
        while (await pipe.ReadAsync(sink, ct).ConfigureAwait(false) > 0)
        {
            // Anything after the request line is not part of the protocol; drop it.
        }
    }

    private PipeResponse Answer(string line, NamedPipeServerStream pipe)
    {
        PipeRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, IpcJsonContext.Default.PipeRequest);
        }
        catch (JsonException)
        {
            return Error(PipeErrors.BadRequest);
        }

        // JSON can leave out or null a non-nullable member, so Type may be null despite its declaration.
        if (request?.Type is null)
        {
            return Error(PipeErrors.BadRequest);
        }
        if (request.V != PipeProtocol.Version)
        {
            return Error(PipeErrors.Version);
        }

        switch (request.Type)
        {
            case PipeRequestTypes.Status:
                return new PipeResponse(PipeProtocol.Version, Status: status());
            case PipeRequestTypes.Key:
                if (!IsAdministrator(pipe))
                {
                    log.LogInformation("Refused the encryption key to a status pipe client that is not an elevated administrator");
                    return Error(PipeErrors.Denied);
                }
                log.LogInformation("Gave the encryption key to an elevated administrator over the status pipe");
                return new PipeResponse(PipeProtocol.Version, Key: key());
            default:
                return Error(PipeErrors.UnknownRequest);
        }
    }

    /// <summary>The admin check, failing closed: a token that cannot be read is not an administrator's.</summary>
    private bool IsAdministrator(NamedPipeServerStream pipe)
    {
        try
        {
            return _isAdministrator(pipe);
        }
        catch (Exception e)
        {
            log.LogDebug("Could not read the status pipe client's token: {Reason}", e.Message);
            return false;
        }
    }

    private static PipeResponse Error(string error) => new(PipeProtocol.Version, Error: error);
}
