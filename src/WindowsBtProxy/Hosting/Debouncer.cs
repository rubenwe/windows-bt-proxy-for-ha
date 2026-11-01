using Microsoft.Extensions.Logging;

namespace WindowsBtProxy.Hosting;

/// <summary>
/// Runs <c>action</c> once <c>delay</c> after the last <see cref="Trigger"/>, on a thread-pool thread, never two runs at once
/// (a trigger that arrives during a run schedules another run afterwards). <see cref="Trigger"/> is cheap and never blocks, so
/// it is safe to call from an event handler; the action may block. A throwing action is logged and does not stop later runs.
/// </summary>
public sealed class Debouncer : IAsyncDisposable
{
    private readonly TimeSpan _delay;
    private readonly Action _action;
    private readonly ILogger _log;
    private readonly ITimer _timer;
    private readonly Lock _gate = new();
    private Task _running = Task.CompletedTask;
    private bool _isRunning;
    private bool _again;
    private bool _disposed;

    public Debouncer(TimeSpan delay, Action action, TimeProvider time, ILogger log)
    {
        _delay = delay;
        _action = action;
        _log = log;
        _timer = time.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>(Re)starts the countdown. Does nothing once disposed.</summary>
    public void Trigger()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            if (_isRunning)
            {
                _again = true;
                return;
            }
            _timer.Change(_delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnTimer(object? state)
    {
        TaskCompletionSource done;
        lock (_gate)
        {
            if (_disposed || _isRunning)
            {
                return;
            }
            _isRunning = true;
            done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _running = done.Task;
        }

        try
        {
            _action();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "A debounced action failed");
        }
        finally
        {
            lock (_gate)
            {
                _isRunning = false;
                if (_again && !_disposed)
                {
                    _again = false;
                    _timer.Change(_delay, Timeout.InfiniteTimeSpan);
                }
            }
            done.SetResult();
        }
    }

    /// <summary>Cancels a pending run and waits for one in progress to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        Task running;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            running = _running;
        }
        await _timer.DisposeAsync().ConfigureAwait(false);
        await running.ConfigureAwait(false);
    }
}
