using WindowsBtProxy.Ipc;

namespace WindowsBtProxy.Tray;

/// <summary>
/// Polls the service and keeps the tray icon in step with the answer. Owns no window and no timer: <see cref="TrayWindow"/> calls
/// <see cref="Tick"/> from its timer, and every method here runs on the UI thread. That keeps the rules (one request at a time, a
/// failure is "not running", touch the shell only when something changed) testable with a fake icon and a fake request.
/// </summary>
/// <param name="icon">The notification-area icon.</param>
/// <param name="request">Asks the service for its status; null or a failed task means it cannot be reached.</param>
/// <param name="time">Clock for <see cref="RequestDeadline"/>.</param>
/// <param name="dispatch">
/// Brings a finished request back to the UI thread (the window posts itself a message). Null runs it on whatever thread finished the
/// request, which is what the tests want.
/// </param>
internal sealed class TrayController(
    INotifyIcon icon,
    Func<CancellationToken, Task<PipeResponse?>> request,
    TimeProvider time,
    Action<Action>? dispatch = null) : IDisposable
{
    /// <summary>
    /// A request still pending after this long is cancelled. The pipe client has a shorter timeout of its own; this is the safety net
    /// that keeps one stuck request from ending the polling for good, since no new request starts while one is in flight.
    /// </summary>
    public static readonly TimeSpan RequestDeadline = TimeSpan.FromSeconds(10);

    private CancellationTokenSource? _inFlight;
    private bool _disposed;

    /// <summary>What the icon shows now.</summary>
    public TrayView Current { get; private set; } = TrayLight.Evaluate(null);

    /// <summary>The last answer, or null when the last request failed (or none has finished yet). The status window shows it.</summary>
    public PipeResponse? LastResponse { get; private set; }

    /// <summary>Raised on the UI thread after every finished request, whether or not the light changed.</summary>
    public event Action? Changed;

    /// <summary>Shows the icon as red "service not running" until the first answer arrives, and asks for that answer.</summary>
    public void Start()
    {
        icon.Add(Current);
        Tick();
    }

    /// <summary>Starts a request unless one is still in flight (a slow service must not pile up connections).</summary>
    public void Tick()
    {
        if (_inFlight is not null || _disposed)
        {
            return;
        }

        var deadline = new CancellationTokenSource(RequestDeadline, time);
        _inFlight = deadline;
        Task<PipeResponse?> task;
        try
        {
            task = request(deadline.Token);
        }
        catch (Exception e)
        {
            task = Task.FromException<PipeResponse?>(e);
        }

        _ = task.ContinueWith(
            finished =>
            {
                if (dispatch is null)
                {
                    Complete(deadline, finished);
                }
                else
                {
                    dispatch(() => Complete(deadline, finished));
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>Explorer restarted and lost every icon: add ours again, as it looks now.</summary>
    public void OnTaskbarCreated() => icon.Add(Current);

    /// <summary>Cancels a request in flight; its answer, if it still comes, is ignored.</summary>
    public void Dispose()
    {
        _disposed = true;
        _inFlight?.Cancel();
    }

    private void Complete(CancellationTokenSource deadline, Task<PipeResponse?> finished)
    {
        deadline.Dispose();
        if (ReferenceEquals(_inFlight, deadline))
        {
            _inFlight = null;
        }
        if (_disposed)
        {
            return;
        }

        // Any failure (a fault, the deadline) means the same as no answer: the service cannot be asked.
        LastResponse = finished.IsCompletedSuccessfully ? finished.Result : null;
        var view = TrayLight.Evaluate(LastResponse);
        if (view != Current)
        {
            Current = view;
            icon.Update(view);
        }
        Changed?.Invoke();
    }
}
