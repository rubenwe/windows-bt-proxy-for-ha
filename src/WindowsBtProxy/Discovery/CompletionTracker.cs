namespace WindowsBtProxy.Discovery;

/// <summary>
/// Counts the completion callbacks that the native DNS-SD API still owes us for one registration, and signals when it is
/// safe to free what those callbacks use (the request, the callback context and the instance).
/// <para>
/// Invariant: <see cref="WaitSettled"/> returns true only after <see cref="BeginClose"/> was called and every callback that
/// was announced with <see cref="Expect"/> has either arrived (<see cref="Arrived"/>) or been withdrawn
/// (<see cref="NoCallback"/>). Callers must call <see cref="Expect"/> before the native call that may produce the callback,
/// and <see cref="Arrived"/> must be the last thing a callback does with the registration.
/// </para>
/// </summary>
internal sealed class CompletionTracker
{
    private readonly Lock _gate = new();
    private int _outstanding;
    private bool _closing;

    // Never exposes WaitHandle, so no kernel handle exists and there is nothing to dispose; a callback that is still
    // returning from Set() after Release() therefore cannot touch a disposed object.
    private readonly ManualResetEventSlim _settled = new();

    /// <summary>A native call that will report back through the completion callback is about to be made.</summary>
    public void Expect()
    {
        lock (_gate)
        {
            _outstanding++;
        }
    }

    /// <summary>The native call returned without scheduling a callback, so the announced one will never come.</summary>
    public void NoCallback() => Complete();

    /// <summary>A completion callback is arriving; call this last.</summary>
    public void Arrived() => Complete();

    /// <summary>No further native calls will be made for this registration. Call after the final <see cref="Expect"/>.</summary>
    public void BeginClose()
    {
        lock (_gate)
        {
            _closing = true;
            SignalIfSettled();
        }
    }

    /// <summary>True if the callback that is arriving now will be the one that settles the registration.</summary>
    public bool IsLastCallback
    {
        get
        {
            lock (_gate)
            {
                return _closing && _outstanding == 1;
            }
        }
    }

    public int Outstanding
    {
        get
        {
            lock (_gate)
            {
                return _outstanding;
            }
        }
    }

    public bool WaitSettled(TimeSpan timeout) => _settled.Wait(timeout);

    private void Complete()
    {
        lock (_gate)
        {
            _outstanding--;
            SignalIfSettled();
        }
    }

    private void SignalIfSettled()
    {
        if (_closing && _outstanding == 0)
        {
            _settled.Set();
        }
    }
}
