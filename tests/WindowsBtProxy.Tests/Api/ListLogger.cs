using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using WindowsBtProxy.Api;

namespace WindowsBtProxy.Tests.Api;

/// <summary>Collects formatted log lines so tests can see what the server did.</summary>
internal sealed class ListLogger : ILogger<ApiServer>
{
    public ConcurrentQueue<string> Lines { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => Lines.Enqueue($"{logLevel}: {formatter(state, exception)}");
}
