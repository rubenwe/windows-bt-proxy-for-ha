using Microsoft.Extensions.Time.Testing;

namespace WindowsBtProxy.Tests.Ble;

/// <summary>
/// A <see cref="FakeTimeProvider"/> that tells a test when code has registered a timer, so the test can advance time only once
/// the code under test is actually waiting (a delay registered after <c>Advance</c> would otherwise be missed).
/// </summary>
internal sealed class TimerCountingTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    private readonly SemaphoreSlim _created = new(0);

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = inner.CreateTimer(callback, state, dueTime, period);
        _created.Release();
        return timer;
    }

    public void Advance(TimeSpan by) => inner.Advance(by);

    /// <summary>Completes once one more timer has been created than the previous wait consumed.</summary>
    public async Task NextTimerAsync()
    {
        if (!await _created.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("No timer was created.");
    }
}
