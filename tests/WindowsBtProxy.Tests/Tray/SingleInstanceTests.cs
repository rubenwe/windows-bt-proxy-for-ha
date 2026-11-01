using System.Security.AccessControl;
using System.Security.Principal;
using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class SingleInstanceTests
{
    private static string UniqueName() => $@"Local\wbp-test-{Guid.NewGuid():N}";

    // The mutex is recursive, so "someone else holds it" has to be another thread (in real life it is another process).
    private static T OnOtherThread<T>(Func<T> work)
    {
        T result = default!;
        var thread = new Thread(() => result = work());
        thread.Start();
        thread.Join();
        return result;
    }

    [Fact]
    public void A_free_mutex_is_acquired()
    {
        using var instance = SingleInstance.TryAcquire(UniqueName());

        Assert.NotNull(instance);
    }

    [Fact]
    public void A_held_mutex_is_not_acquired()
    {
        var name = UniqueName();
        using var holder = SingleInstance.TryAcquire(name);

        var second = OnOtherThread(() => SingleInstance.TryAcquire(name));

        Assert.NotNull(holder);
        Assert.Null(second);
    }

    [Fact]
    public void After_the_holder_disposes_the_next_acquire_succeeds_at_once()
    {
        var name = UniqueName();
        var holder = SingleInstance.TryAcquire(name);
        Assert.NotNull(holder);

        holder.Dispose();
        var acquired = OnOtherThread(() =>
        {
            using var next = SingleInstance.TryAcquire(name);
            return next is not null;
        });

        Assert.True(acquired);
    }

    [Fact]
    public void A_mutex_still_open_elsewhere_but_not_owned_is_acquired()
    {
        // The RestartInto race: the old tray released ownership but its handle may still be open when the new one starts.
        var name = UniqueName();
        using var openButNotOwned = new Mutex(initiallyOwned: false, name);

        var acquired = OnOtherThread(() =>
        {
            using var next = SingleInstance.TryAcquire(name);
            return next is not null;
        });

        Assert.True(acquired);
    }

    [Fact]
    public void An_abandoned_mutex_is_acquired()
    {
        var name = UniqueName();
        using var keepAlive = new Mutex(initiallyOwned: false, name);
        // A thread that takes the mutex and ends without releasing it: what a crashed tray leaves behind.
        OnOtherThread(() => keepAlive.WaitOne(0));

        using var instance = SingleInstance.TryAcquire(name);

        Assert.NotNull(instance);
    }

    [Fact]
    public void A_mutex_this_user_may_not_open_counts_as_held()
    {
        // What an elevated tray leaves for an unelevated one: a mutex whose DACL does not let this token wait on it.
        var name = UniqueName();
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(
            WindowsIdentity.GetCurrent().User!, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Deny));
        using var restricted = MutexAcl.Create(initiallyOwned: false, name, out var createdNew, security);
        Assert.True(createdNew);

        var instance = SingleInstance.TryAcquire(name);

        Assert.Null(instance);
    }
}
