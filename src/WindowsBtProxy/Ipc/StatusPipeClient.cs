using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace WindowsBtProxy.Ipc;

/// <summary>Asks the service one question over the status pipe; used by the tray app and the <c>status</c> and <c>key</c> commands.</summary>
public static class StatusPipeClient
{
    /// <summary>
    /// Connects, sends <c>{"v":1,"type":<paramref name="type"/>}</c> and returns the answer. Returns null when nothing listens on
    /// <paramref name="pipeName"/>, the pipe refuses this user, or no well-formed answer arrives within <paramref name="timeout"/>
    /// (all of which mean "the service cannot be asked"). Cancelling <paramref name="ct"/> throws as usual.
    /// </summary>
    /// <remarks>
    /// Always connects at <see cref="TokenImpersonationLevel.Identification"/>: the server may read this user's token to check for
    /// administrators, but can never act as this user with it. Without that flag Windows would hand the server an impersonation token.
    /// </remarks>
    public static async Task<PipeResponse?> RequestAsync(string pipeName, string type, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
            await using (pipe.ConfigureAwait(false))
            {
                await pipe.ConnectAsync(Timeout.Infinite, deadline.Token).ConfigureAwait(false);
                var request = JsonSerializer.Serialize(new PipeRequest(PipeProtocol.Version, type), IpcJsonContext.Default.PipeRequest);
                await PipeProtocol.WriteLineAsync(pipe, request, deadline.Token).ConfigureAwait(false);
                var line = await PipeProtocol.ReadLineAsync(pipe, deadline.Token).ConfigureAwait(false);
                return line is null ? null : JsonSerializer.Deserialize(line, IpcJsonContext.Default.PipeResponse);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException or JsonException)
        {
            return null;
        }
    }
}
