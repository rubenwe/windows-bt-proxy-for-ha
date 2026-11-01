using System.Buffers;
using System.Text;

namespace WindowsBtProxy.Ipc;

/// <summary>
/// The local status pipe's wire format: one JSON request line from the client, one JSON response line back, then both hang up.
/// Lines are UTF-8 ending in <c>\n</c> and short, so a reader can refuse anything longer without buffering it.
/// </summary>
public static class PipeProtocol
{
    /// <summary>The pipe the installed service owns (<c>\\.\pipe\WindowsBtProxyForHA</c>). Tests must never use it.</summary>
    public const string DefaultPipeName = "WindowsBtProxyForHA";

    /// <summary>Protocol version carried in every request and response as <c>v</c>.</summary>
    public const int Version = 1;

    /// <summary>The longest line either side sends or accepts, in bytes, including its <c>\n</c>.</summary>
    public const int MaxLineBytes = 4096;

    /// <summary>How long the server waits for a request (and for the client to take the answer) before hanging up.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reads one line, up to and without its <c>\n</c>. Returns null at end of stream before a <c>\n</c> (a truncated line is not a line).
    /// Throws <see cref="PipeLineTooLongException"/> once <see cref="MaxLineBytes"/> bytes have arrived with no <c>\n</c>.
    /// </summary>
    /// <remarks>
    /// Reads a byte at a time so nothing past the <c>\n</c> is taken from the stream: the next line stays for the next call.
    /// Lines are a few hundred bytes and each connection carries one, so the extra reads cost nothing that matters.
    /// </remarks>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(MaxLineBytes);
        try
        {
            var length = 0;
            while (true)
            {
                if (await stream.ReadAsync(buffer.AsMemory(length, 1), ct).ConfigureAwait(false) == 0)
                {
                    return null;
                }
                if (buffer[length] == (byte)'\n')
                {
                    return Encoding.UTF8.GetString(buffer, 0, length);
                }
                length++;
                if (length == MaxLineBytes)
                {
                    throw new PipeLineTooLongException();
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Writes <paramref name="json"/> and a <c>\n</c> in one write, then flushes.</summary>
    /// <exception cref="ArgumentException"><paramref name="json"/> contains a <c>\n</c>, which would split it into two lines.</exception>
    /// <exception cref="PipeLineTooLongException">The line would be longer than <see cref="MaxLineBytes"/>, so the peer would refuse it.</exception>
    public static async Task WriteLineAsync(Stream stream, string json, CancellationToken ct)
    {
        if (json.Contains('\n', StringComparison.Ordinal))
        {
            throw new ArgumentException("A pipe line cannot contain a newline.", nameof(json));
        }
        var length = Encoding.UTF8.GetByteCount(json) + 1;
        if (length > MaxLineBytes)
        {
            throw new PipeLineTooLongException();
        }

        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Encoding.UTF8.GetBytes(json, buffer);
            buffer[length - 1] = (byte)'\n';
            await stream.WriteAsync(buffer.AsMemory(0, length), ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>A pipe line longer than <see cref="PipeProtocol.MaxLineBytes"/>: the peer is broken or hostile, so the connection is dropped.</summary>
public sealed class PipeLineTooLongException() : IOException($"A status pipe line is longer than {PipeProtocol.MaxLineBytes} bytes.");

/// <summary>The request types the status pipe answers.</summary>
public static class PipeRequestTypes
{
    /// <summary>The proxy's status; anyone allowed to open the pipe may ask.</summary>
    public const string Status = "status";

    /// <summary>The stored encryption key; answered only to elevated administrators.</summary>
    public const string Key = "key";
}

/// <summary>Values of <see cref="PipeResponse.Error"/>.</summary>
public static class PipeErrors
{
    /// <summary>The caller is not an elevated administrator, so the key is not given out.</summary>
    public const string Denied = "denied";

    /// <summary>The line was not JSON, or had no <c>type</c>.</summary>
    public const string BadRequest = "bad-request";

    /// <summary>The <c>type</c> is not one this server knows.</summary>
    public const string UnknownRequest = "unknown-request";

    /// <summary>The request's <c>v</c> is not <see cref="PipeProtocol.Version"/>.</summary>
    public const string Version = "version";
}

/// <summary>A request line: <c>{"v":1,"type":"status"}</c>.</summary>
/// <param name="V">Protocol version; must be <see cref="PipeProtocol.Version"/>.</param>
/// <param name="Type">One of <see cref="PipeRequestTypes"/>. Null when the line had none, which JSON allows even though the type does not.</param>
public sealed record PipeRequest(int V, string Type);

/// <summary>The answer to a <c>key</c> request.</summary>
/// <param name="Key">The 32-byte Noise key as base64, or null when no key is stored (distinct from the reply being absent).</param>
public sealed record KeyReply(string? Key);

/// <summary>A response line. <see cref="V"/> is always <see cref="PipeProtocol.Version"/>; exactly one of the others is set.</summary>
/// <param name="V">Protocol version of the server.</param>
/// <param name="Status">The answer to <c>status</c>.</param>
/// <param name="Key">The answer to <c>key</c>.</param>
/// <param name="Error">One of <see cref="PipeErrors"/> when the request was not answered.</param>
public sealed record PipeResponse(int V, ProxyStatus? Status = null, KeyReply? Key = null, string? Error = null);
