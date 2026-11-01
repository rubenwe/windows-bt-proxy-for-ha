using Microsoft.Extensions.Time.Testing;
using WindowsBtProxy.Ble;
using WindowsBtProxy.Config;
using WindowsBtProxy.Ipc;
using WindowsBtProxy.Tray;

namespace WindowsBtProxy.Tests.Tray;

public sealed class TrayControllerTests
{
    private static readonly TrayView NotRunning = TrayLight.Evaluate(null);

    private static PipeResponse Respond(bool subscribed = false) =>
        new(PipeProtocol.Version, new ProxyStatus(
            "0.2.0", "bt-proxy-test", "BT Proxy Test", 6053, ScanMode.Passive, ScannerStatus.Running,
            "02:11:22:33:44:55", ApiRunning: true, AdvertisementsLastMinute: 10, EncryptionMode.Required,
            subscribed ? [new ClientStatus("192.168.1.10", "Home Assistant 2026.11", Subscribed: true, Encrypted: true)] : []));

    [Fact]
    public void Start_adds_red_icon_then_updates_after_first_response()
    {
        var harness = new Harness();

        harness.Controller.Start();

        Assert.Equal([("Add", NotRunning)], harness.Icon.Calls);
        Assert.Single(harness.Requests);

        var amber = Respond();
        harness.Requests[0].SetResult(amber);

        Assert.Equal([("Add", NotRunning), ("Update", TrayLight.Evaluate(amber))], harness.Icon.Calls);
        Assert.Equal(Light.Amber, harness.Controller.Current.Light);
        Assert.Same(amber, harness.Controller.LastResponse);
        Assert.Equal(1, harness.ChangedCount);
    }

    [Fact]
    public void Only_one_request_in_flight()
    {
        var harness = new Harness();
        harness.Controller.Start();

        harness.Controller.Tick();
        harness.Controller.Tick();
        harness.Controller.Tick();

        Assert.Single(harness.Requests);

        harness.Requests[0].SetResult(Respond());
        harness.Controller.Tick();

        Assert.Equal(2, harness.Requests.Count);
    }

    [Fact]
    public void Request_failure_counts_as_unreachable()
    {
        var harness = new Harness();
        harness.Controller.Start();
        harness.Requests[0].SetResult(Respond());

        harness.Controller.Tick();
        harness.Requests[1].SetException(new InvalidOperationException("boom"));

        Assert.Equal(NotRunning, harness.Controller.Current);
        Assert.Null(harness.Controller.LastResponse);
        Assert.Equal(("Update", NotRunning), harness.Icon.Calls[^1]);

        harness.Controller.Tick();

        Assert.Equal(3, harness.Requests.Count);
    }

    [Fact]
    public void A_request_that_throws_before_returning_a_task_counts_as_unreachable()
    {
        var calls = 0;
        var icon = new RecordingIcon();
        var controller = new TrayController(icon, _ => { calls++; throw new IOException("no pipe"); }, new FakeTimeProvider());

        controller.Start();
        controller.Tick();

        Assert.Equal(2, calls);
        Assert.Equal(NotRunning, controller.Current);
        Assert.Equal([("Add", NotRunning)], icon.Calls);
    }

    [Fact]
    public void Taskbar_recreated_re_adds_icon_with_current_light()
    {
        var harness = new Harness();
        harness.Controller.Start();
        var green = Respond(subscribed: true);
        harness.Requests[0].SetResult(green);

        harness.Controller.OnTaskbarCreated();

        Assert.Equal(("Add", TrayLight.Evaluate(green)), harness.Icon.Calls[^1]);
        Assert.Equal(Light.Green, harness.Icon.Calls[^1].View.Light);
    }

    [Fact]
    public void Update_only_when_view_changes()
    {
        var harness = new Harness();
        harness.Controller.Start();

        harness.Requests[0].SetResult(Respond());
        harness.Controller.Tick();
        harness.Requests[1].SetResult(Respond());
        harness.Controller.Tick();
        harness.Requests[2].SetResult(Respond(subscribed: true));

        Assert.Equal(
            ["Add", "Update", "Update"],
            harness.Icon.Calls.Select(c => c.Op));
        Assert.Equal(Light.Green, harness.Icon.Calls[^1].View.Light);
        // The status window still hears about every answer, because its text can change while the light does not.
        Assert.Equal(3, harness.ChangedCount);
    }

    [Fact]
    public void A_request_still_pending_at_the_deadline_is_cancelled()
    {
        var harness = new Harness();
        harness.Controller.Start();

        Assert.False(harness.Tokens[0].IsCancellationRequested);
        harness.Time.Advance(TrayController.RequestDeadline);

        Assert.True(harness.Tokens[0].IsCancellationRequested);
    }

    [Fact]
    public void Finished_requests_are_handed_to_the_dispatcher()
    {
        var posted = new List<Action>();
        var requests = new List<TaskCompletionSource<PipeResponse?>>();
        var icon = new RecordingIcon();
        var controller = new TrayController(icon, _ =>
        {
            var tcs = new TaskCompletionSource<PipeResponse?>();
            requests.Add(tcs);
            return tcs.Task;
        }, new FakeTimeProvider(), posted.Add);

        controller.Start();
        requests[0].SetResult(Respond());

        Assert.Equal(NotRunning, controller.Current);
        Assert.Single(posted);

        posted[0]();

        Assert.Equal(Light.Amber, controller.Current.Light);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Controller = new TrayController(Icon, ct =>
            {
                var tcs = new TaskCompletionSource<PipeResponse?>();
                Requests.Add(tcs);
                Tokens.Add(ct);
                return tcs.Task;
            }, Time);
            Controller.Changed += () => ChangedCount++;
        }

        public RecordingIcon Icon { get; } = new();

        public FakeTimeProvider Time { get; } = new();

        public List<TaskCompletionSource<PipeResponse?>> Requests { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public TrayController Controller { get; }

        public int ChangedCount { get; private set; }
    }

    private sealed class RecordingIcon : INotifyIcon
    {
        public List<(string Op, TrayView View)> Calls { get; } = [];

        public void Add(TrayView view) => Calls.Add(("Add", view));

        public void Update(TrayView view) => Calls.Add(("Update", view));

        public void Remove() => Calls.Add(("Remove", default));
    }
}
