namespace WindowsBtProxy.Tray;

/// <summary>
/// Ownership of a named mutex, which makes the tray one per session. It is ownership, not whether the mutex exists: a tray that is
/// restarting into the installed exe must not keep the newcomer out just because its handle is still open.
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;

    private SingleInstance(Mutex mutex) => _mutex = mutex;

    /// <summary>
    /// Takes the mutex without waiting. Returns null while another process (or thread) owns it. A mutex left abandoned by a tray that
    /// crashed counts as taken: the OS hands its ownership to us.
    /// </summary>
    /// <remarks>
    /// The mutex is recursive, so the thread that owns it can take it again; across processes that never happens. Dispose on the thread
    /// that took it.
    /// </remarks>
    public static SingleInstance? TryAcquire(string name)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(initiallyOwned: false, name);
        }
        catch (UnauthorizedAccessException)
        {
            // An elevated tray created it with an admin-only DACL, so a tray runs in this session already.
            return null;
        }

        try
        {
            if (!mutex.WaitOne(0))
            {
                mutex.Dispose();
                return null;
            }
        }
        catch (AbandonedMutexException)
        {
            // Its previous owner exited without releasing it; WaitOne gave the ownership to us all the same.
        }
        return new SingleInstance(mutex);
    }

    /// <summary>Releases ownership and closes the handle, so the next tray can take it at once.</summary>
    public void Dispose()
    {
        _mutex.ReleaseMutex();
        _mutex.Dispose();
    }
}
