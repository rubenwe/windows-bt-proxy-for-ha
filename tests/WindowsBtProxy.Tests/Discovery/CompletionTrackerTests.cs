using WindowsBtProxy.Discovery;

namespace WindowsBtProxy.Tests.Discovery;

public class CompletionTrackerTests
{
    private static readonly TimeSpan None = TimeSpan.Zero;

    [Fact]
    public void Is_not_settled_before_close_even_with_nothing_outstanding()
    {
        var t = new CompletionTracker();
        t.Expect();
        t.Arrived();

        Assert.False(t.WaitSettled(None));
    }

    [Fact]
    public void Late_register_callback_during_deregistration_does_not_settle_early()
    {
        // register pending, then deregister announced and closed; the register callback lands before the deregister one.
        var t = new CompletionTracker();
        t.Expect();            // register
        t.Expect();            // deregister
        t.BeginClose();

        Assert.Equal(2, t.Outstanding);
        t.Arrived();           // late register callback
        Assert.False(t.WaitSettled(None));
        Assert.True(t.IsLastCallback);

        t.Arrived();           // deregister callback
        Assert.True(t.WaitSettled(None));
    }

    [Fact]
    public void Synchronous_deregister_still_waits_for_an_outstanding_register_callback()
    {
        var t = new CompletionTracker();
        t.Expect();            // register, still pending
        t.Expect();            // deregister
        t.BeginClose();
        t.NoCallback();        // deregister returned without a callback

        Assert.False(t.WaitSettled(None));
        t.Arrived();
        Assert.True(t.WaitSettled(None));
    }

    [Fact]
    public void Synchronous_everything_settles_on_close()
    {
        var t = new CompletionTracker();
        t.Expect();
        t.NoCallback();        // register completed synchronously
        t.Expect();
        t.BeginClose();
        t.NoCallback();

        Assert.True(t.WaitSettled(None));
    }

    [Fact]
    public async Task Concurrent_callbacks_settle_exactly_once_after_all_arrive()
    {
        for (var round = 0; round < 200; round++)
        {
            var t = new CompletionTracker();
            t.Expect();
            t.Expect();
            t.BeginClose();
            var a = Task.Run(t.Arrived, TestContext.Current.CancellationToken);
            var b = Task.Run(t.Arrived, TestContext.Current.CancellationToken);
            await Task.WhenAll(a, b);

            Assert.True(t.WaitSettled(TimeSpan.FromSeconds(1)));
            Assert.Equal(0, t.Outstanding);
        }
    }
}
