using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Setup;

/// <summary>Prints install progress as plain lines: information as is, warnings and errors with a prefix. Debug and trace are dropped.</summary>
public sealed class SetupConsoleLogger(TextWriter output) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel is >= LogLevel.Information and < LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }
        var prefix = logLevel switch
        {
            LogLevel.Warning => "warning: ",
            LogLevel.Error or LogLevel.Critical => "error: ",
            _ => string.Empty,
        };
        output.WriteLine(prefix + formatter(state, exception));
    }
}
