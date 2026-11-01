using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Hosting;

/// <summary>
/// Plain-text rolling log: <c>proxy.log</c> in the log directory, rolled to <c>proxy.1.log</c> ... <c>proxy.{n-1}.log</c> when a line
/// would push it past the size cap; the oldest file is deleted. One line per entry, <c>{UTC time} {LEVEL} {category}: {message}</c>,
/// with any exception text on the following lines. Thread-safe; logging never throws, so a full disk or a locked file just drops lines.
/// </summary>
public sealed class RollingFileLogger : ILoggerProvider
{
    public const long DefaultMaxFileBytes = 1024 * 1024;
    public const int DefaultMaxFiles = 5;

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly LogLevel _minLevel;
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new();
    private readonly Lock _gate = new();
    private long _currentSize = -1; // -1: not measured yet
    private bool _disposed;

    public RollingFileLogger(string logDirectory, LogLevel minLevel, long maxFileBytes = DefaultMaxFileBytes, int maxFiles = DefaultMaxFiles, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(logDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFiles, 1);
        _directory = logDirectory;
        _minLevel = minLevel;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Maps the settings.json <c>logLevel</c> string to a level; anything unrecognised means info.</summary>
    public static LogLevel ParseLevel(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "trace" => LogLevel.Trace,
        "debug" => LogLevel.Debug,
        "info" or "information" => LogLevel.Information,
        "warn" or "warning" => LogLevel.Warning,
        "error" => LogLevel.Error,
        "critical" => LogLevel.Critical,
        _ => LogLevel.Information,
    };

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private string CurrentFile => Path.Combine(_directory, "proxy.log");

    private string RolledFile(int index) => Path.Combine(_directory, $"proxy.{index.ToString(CultureInfo.InvariantCulture)}.log");

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        try
        {
            var text = new StringBuilder();
            text.Append(_time.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                .Append(' ').Append(LevelName(level)).Append(' ').Append(category).Append(": ").Append(message).Append('\n');
            if (exception is not null)
            {
                text.Append(exception).Append('\n');
            }
            var bytes = Utf8.GetBytes(text.ToString());

            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                try
                {
                    AppendLocked(bytes);
                }
                catch
                {
                    // The file may have changed under us (or the disk is full): re-measure next time.
                    _currentSize = -1;
                    throw;
                }
            }
        }
        catch
        {
            // Logging must never take the service down.
        }
    }

    private void AppendLocked(byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        if (_currentSize < 0)
        {
            _currentSize = SizeOf(CurrentFile);
        }
        if (_currentSize > 0 && _currentSize + bytes.Length > _maxFileBytes)
        {
            Roll();
            _currentSize = SizeOf(CurrentFile);
        }
        using (var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            stream.Write(bytes);
        }
        _currentSize += bytes.Length;
    }

    /// <summary>proxy.log becomes proxy.1.log, proxy.1.log becomes proxy.2.log, and so on; whatever falls off the end is deleted.</summary>
    private void Roll()
    {
        try
        {
            File.Delete(RolledFile(_maxFiles - 1));
            for (var i = _maxFiles - 2; i >= 1; i--)
            {
                if (File.Exists(RolledFile(i)))
                {
                    File.Move(RolledFile(i), RolledFile(i + 1), overwrite: true);
                }
            }
            if (_maxFiles > 1)
            {
                File.Move(CurrentFile, RolledFile(1), overwrite: true);
            }
            else
            {
                File.Delete(CurrentFile);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A locked file (an editor with the log open) just means this roll is skipped; the next line tries again.
        }
    }

    private static long SizeOf(string file)
    {
        var info = new FileInfo(file);
        return info.Exists ? info.Length : 0;
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT",
        _ => "INFO",
    };

    private sealed class FileLogger(RollingFileLogger owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= owner._minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch
            {
                message = "(log message formatting failed)";
            }
            owner.Write(logLevel, category, message, exception);
        }
    }
}
