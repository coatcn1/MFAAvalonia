using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

static class LiveStopConfirmationTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Line(long taskId, bool active, string token = "round-1") =>
        LivePerformanceState.ProtocolPrefix + new JObject
        {
            ["version"] = 1, ["task_id"] = taskId, ["run_token"] = token, ["active"] = active
        }.ToString(Newtonsoft.Json.Formatting.None);

    public static async Task RunAsync()
    {
        var state = new LivePerformanceState();
        var tasker = new object();
        var otherTasker = new object();
        var source = state.RegisterAgent(tasker);
        var otherSource = state.RegisterAgent(otherTasker);
        var generation = state.BeginTask(tasker);
        state.ObserveCallback(tasker, "Tasker.Task.Starting", new JObject { ["task_id"] = 101 });
        state.BindTask(generation, 101);
        Assert(state.Capture() is { Performing: false }, "selection must not be marked performing");
        state.ConsumeAgentLine(otherSource, Line(101, true));
        state.ConsumeAgentLine(source, Line(102, true));
        state.ConsumeAgentLine(source, LivePerformanceState.ProtocolPrefix + "{broken");
        state.ConsumeAgentLine(source, LivePerformanceState.ProtocolPrefix + "{\"version\":2}");
        Assert(state.Capture() is { Performing: false }, "wrong source/task or malformed protocol was accepted");
        Assert(!state.ConsumeAgentLine(source, "RealtimeProfilePlay started"), "ordinary log became a protocol");
        state.ConsumeAgentLine(source, Line(101, true));
        Assert(state.Capture() is { Performing: true }, "realtime entry not accepted");
        state.ConsumeAgentLine(source, Line(101, false, "old-round"));
        Assert(state.Capture() is { Performing: true }, "old round exit cleared current round");

        var coordinator = new LiveStopConfirmationCoordinator();
        var stopped = 0;
        var asked = 0;
        await coordinator.RequestAsync(state, () => { asked++; return Task.FromResult(false); }, _ => stopped++);
        Assert(asked == 1 && stopped == 0 && state.Capture() is { Performing: true },
            "cancel must preserve playback and phase");
        await coordinator.RequestAsync(state, () => throw new InvalidOperationException("dialog unavailable"), _ => stopped++);
        Assert(stopped == 0, "dialog failure stopped playback");

        var answer = new TaskCompletionSource<bool>();
        var pending = coordinator.RequestAsync(state, () => { asked++; return answer.Task; }, _ => stopped++);
        await coordinator.RequestAsync(state, () => throw new Exception("duplicate dialog"), _ => stopped++);
        state.ConsumeAgentLine(source, Line(101, false));
        answer.SetResult(true);
        await pending;
        Assert(stopped == 1 && asked == 2, "confirmation during same-task result must stop exactly once");

        state.ConsumeAgentLine(source, Line(101, true, "round-2"));
        var lateAnswer = new TaskCompletionSource<bool>();
        var late = coordinator.RequestAsync(state, () => lateAnswer.Task, _ => stopped++);
        state.EndTask(generation);
        generation = state.BeginTask(tasker);
        state.BindTask(generation, 102);
        state.EndTask(generation - 1);
        lateAnswer.SetResult(true);
        await late;
        Assert(stopped == 1, "late confirmation stopped a new native task");
        state.ConsumeAgentLine(source, Line(101, true));
        Assert(state.Capture() is { Performing: false }, "previous task stdout contaminated next task");
        state.BeginRun();
        generation = state.BeginTask(tasker);
        state.BindTask(generation, 102);

        state.ConsumeAgentLine(source, Line(102, true));
        Action? queuedStop = null;
        await coordinator.RequestAsync(state, () => Task.FromResult(true), snapshot =>
            queuedStop = () => { if (state.TryClaimStop(snapshot!.Value)) stopped++; });
        Assert(queuedStop != null, "confirmed stop was not queued");
        state.EndTask(generation);
        generation = state.BeginTask(tasker);
        state.BindTask(generation, 103);
        queuedStop!();
        Assert(stopped == 1, "stop queued for old task stopped a new task at execution time");

        // 自动演出依赖实际节点回调，不能按整项任务名或日志猜测。
        JObject Node(string name) => new() { ["task_id"] = 103, ["name"] = name, ["action_id"] = 7 };
        state.ObserveCallback(otherTasker, "Node.Action.Succeeded", Node("AutoLiveStart"));
        Assert(state.Capture() is { Performing: false }, "old tasker callback changed phase");
        state.ObserveCallback(tasker, "Node.Action.Succeeded", Node("AutoLiveStart"));
        Assert(state.Capture() is { Performing: true }, "AutoLive start not accepted");
        state.ObserveCallback(tasker, "Node.Recognition.Succeeded", Node("AutoLiveResult"));
        Assert(state.Capture() is { Performing: false }, "AutoLive result recognition did not clear phase");
        state.ObserveCallback(tasker, "Node.Action.Succeeded", Node("AutoLiveStart"));
        state.ObserveCallback(tasker, "Node.Action.Starting", Node("AutoLiveResult"));
        Assert(state.Capture() is { Performing: false }, "AutoLive result action did not clear phase");
        state.ObserveCallback(tasker, "Node.Action.Succeeded", Node("AutoLiveStart"));
        state.ObserveCallback(tasker, "Node.Action.Failed", Node("AutoLiveStart"));
        Assert(state.Capture() is { Performing: false }, "failed AutoLive action retained phase");
        state.ConsumeAgentLine(source, Line(103, true));
        state.ObserveCallback(tasker, "Tasker.Task.Failed", Node("ignored"));
        Assert(state.Capture() is { Performing: false }, "task failure retained realtime phase");
        state.ConsumeAgentLine(source, Line(103, true));
        Assert(state.Capture() is { Performing: false }, "late stdout revived a failed task");
        state.EndTask(generation);
        generation = state.BeginTask(tasker);
        state.BindTask(generation, 103);
        state.ConsumeAgentLine(source, Line(103, true));
        state.UnregisterAgent(source);
        Assert(state.Capture() is { Performing: false }, "Agent exit retained realtime phase");
        state.ConsumeAgentLine(source, Line(103, true));
        Assert(state.Capture() is { Performing: false }, "dead Agent source was accepted");

        var dialogsBefore = asked;
        await coordinator.RequestAsync(state, () => { asked++; return Task.FromResult(true); }, _ => stopped++);
        Assert(stopped == 2 && asked == dialogsBefore, "non-performance stop should be immediate");
        state.Reset();
        Assert(state.Capture() == null, "stop/reset did not invalidate task");
        await coordinator.RequestAsync(state, () => throw new Exception("unexpected dialog"), _ => stopped++);
        Assert(stopped == 3, "missing status must preserve original stop");

        generation = state.BeginTask(tasker);
        state.BindTask(generation, 104);
        var claimSnapshot = state.Capture()!.Value;
        Assert(state.TryClaimStop(claimSnapshot), "current task could not claim stop");
        Assert(!state.TryClaimStop(claimSnapshot), "stop claim was not one-shot");
        Assert(state.BeginTask(tasker) == 0, "claimed stop allowed next BeginTask");
        var posted = false;
        try
        {
            state.PostTaskIfCurrent(generation, () => { posted = true; return 1; });
            throw new InvalidOperationException("claimed stop allowed native append");
        }
        catch (OperationCanceledException) { }
        Assert(!posted, "stop claim dispatched native input");
        state.Reset();
        Assert(state.BeginTask(tasker) == 0, "stop/reset accidentally unlocked an old loop");
        state.BeginRun();
        generation = state.BeginTask(tasker);
        Assert(generation > 0 && state.PostTaskIfCurrent(generation, () => 17) == 17,
            "explicit new start did not release stop claim");

        var (host, explicitStop) = LiveStopConfirmationDialog.Create();
        var buttons = host.ActionButtonsSource!;
        Assert(buttons.Count == 2 && buttons[0].IsDefault && buttons[0].IsCancel,
            "default Enter and Esc must choose continue");
        Assert(ReferenceEquals(buttons[1], explicitStop) && !explicitStop.IsDefault && !explicitStop.IsCancel,
            "only explicit stop button may authorize stopping");
        KeyboardRoutesChooseContinue();
    }

    private static void KeyboardRoutesChooseContinue()
    {
        foreach (var key in new[] { Key.Enter, Key.Escape })
        {
            var (host, explicitStop) = LiveStopConfirmationDialog.Create();
            var cancel = host.ActionButtonsSource![0];
            Button? selected = null;
            cancel.Click += (_, _) => selected = cancel;
            explicitStop.Click += (_, _) => selected = explicitStop;
            using var window = new TestWindow
            {
                ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
                Width = 300, Height = 100,
                Content = new StackPanel { Children = { cancel, explicitStop } }
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.RaiseEvent(new KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = key });
            Dispatcher.UIThread.RunJobs();
            Assert(ReferenceEquals(selected, cancel), $"real Window {key} did not choose continue");
        }
        var (closeHost, stop) = LiveStopConfirmationDialog.Create();
        var clicked = false;
        foreach (var button in closeHost.ActionButtonsSource!) button.Click += (_, _) => clicked = true;
        using var closeWindow = new TestWindow
        {
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            Content = new StackPanel { Children = { closeHost.ActionButtonsSource[0], stop } }
        };
        closeWindow.Show();
        closeWindow.Close();
        Assert(!clicked, "window close synthesized stop confirmation");
    }

    private sealed class TestWindow : Window, IDisposable
    {
        public void Dispose() => Close();
    }
}
