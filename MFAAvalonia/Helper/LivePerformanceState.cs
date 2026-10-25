using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public readonly record struct LiveTaskSnapshot(long Generation, bool Performing);

public sealed class LivePerformanceState
{
    public const string ProtocolPrefix = "MBD_PERFORMANCE_STATE ";
    private readonly object _sync = new();
    private readonly Dictionary<object, object> _sources = new();
    private long _generation;
    private object? _tasker;
    private long _taskId;
    private object? _performingSource;
    private string? _runToken;
    private bool _autoLive;
    private bool _stopClaimed;
    private bool _taskFinished;

    public object RegisterAgent(object tasker)
    {
        lock (_sync)
        {
            var source = new object();
            _sources.Add(source, tasker);
            return source;
        }
    }

    public void UnregisterAgent(object source)
    {
        lock (_sync)
        {
            var removed = _sources.Remove(source, out var tasker);
            if (ReferenceEquals(source, _performingSource)
                || (removed && _autoLive && ReferenceEquals(tasker, _tasker))) ClearPhase();
        }
    }

    public long BeginTask(object tasker)
    {
        lock (_sync)
        {
            if (_stopClaimed) return 0;
            ClearPhase();
            _tasker = tasker;
            _taskId = 0;
            _taskFinished = false;
            return ++_generation;
        }
    }

    public void BindTask(long generation, long taskId)
    {
        lock (_sync)
        {
            if (generation != _generation || _tasker == null || taskId <= 0) return;
            if (_taskId != 0 && _taskId != taskId) ClearPhase();
            _taskId = taskId;
        }
    }

    public void EndTask(long generation)
    {
        lock (_sync)
            if (generation == _generation) ResetCore();
    }

    public void Reset()
    {
        lock (_sync) ResetCore();
    }

    public void BeginRun()
    {
        lock (_sync)
        {
            ResetCore();
            _stopClaimed = false;
        }
    }

    public T PostTaskIfCurrent<T>(long generation, Func<T> post)
    {
        lock (_sync)
        {
            if (_stopClaimed || generation != _generation || _tasker == null)
                throw new OperationCanceledException("已确认停止，禁止派发后续任务");
            // 只包围立即返回 job 的提交，不在锁内等待执行、停止或 UI。
            return post();
        }
    }

    private void ResetCore()
    {
        ++_generation;
        _tasker = null;
        _taskId = 0;
        ClearPhase();
    }

    private void ClearPhase()
    {
        _performingSource = null;
        _runToken = null;
        _autoLive = false;
    }

    public LiveTaskSnapshot? Capture()
    {
        lock (_sync)
            return _tasker == null ? null : new(_generation, _autoLive || _runToken != null);
    }

    public bool IsCurrent(LiveTaskSnapshot snapshot)
    {
        lock (_sync)
            return !_stopClaimed && _tasker != null && snapshot.Generation == _generation;
    }

    public bool TryClaimStop(LiveTaskSnapshot snapshot)
    {
        lock (_sync)
        {
            if (_stopClaimed || _tasker == null || snapshot.Generation != _generation) return false;
            // 只校验任务代次；同一任务自然进入结算后，原确认仍可停止它。
            // Claim 阻止旧异步循环派发新任务，只有新 Start 才能解除。
            _stopClaimed = true;
            return true;
        }
    }

    public bool ConsumeAgentLine(object source, string line)
    {
        if (!line.StartsWith(ProtocolPrefix, StringComparison.Ordinal)) return false;
        try
        {
            var data = JObject.Parse(line[ProtocolPrefix.Length..]);
            if (data["version"]?.Type != JTokenType.Integer || (int?)data["version"] != 1
                || data["task_id"]?.Type != JTokenType.Integer
                || data["active"]?.Type != JTokenType.Boolean
                || data["run_token"]?.Type != JTokenType.String) return true;
            var id = (long?)data["task_id"];
            var run = (string?)data["run_token"];
            if (id is not > 0 || string.IsNullOrWhiteSpace(run) || run.Length > 128) return true;
            lock (_sync)
            {
                if (!_sources.TryGetValue(source, out var tasker)
                    || _taskFinished || !ReferenceEquals(tasker, _tasker) || id != _taskId) return true;
                if ((bool)data["active"]!)
                {
                    _performingSource = source;
                    _runToken = run;
                }
                else if (ReferenceEquals(source, _performingSource) && run == _runToken)
                {
                    _performingSource = null;
                    _runToken = null;
                }
            }
        }
        catch (Exception)
        {
            // 缺失或损坏的通知不改变停止能力，也不进入普通 UI 日志。
        }
        return true;
    }

    public void ObserveCallback(object? tasker, string message, JObject detail)
    {
        try { ObserveCallbackCore(tasker, message, detail); }
        catch (Exception)
        {
            // 框架回调的附加观察失败不得传播到任务生命周期。
        }
    }

    private void ObserveCallbackCore(object? tasker, string message, JObject detail)
    {
        if (detail["task_id"]?.Type != JTokenType.Integer) return;
        var id = (long?)detail["task_id"];
        lock (_sync)
        {
            if (_tasker == null || !ReferenceEquals(tasker, _tasker) || id is not > 0) return;
            // Starting 可先于 AppendTask 返回；实际 job.Id 随后会再次核验。
            if (message == "Tasker.Task.Starting" && _taskId == 0) _taskId = id.Value;
            if (id != _taskId) return;
            if (message is "Tasker.Task.Succeeded" or "Tasker.Task.Failed")
            {
                _taskFinished = true;
                ClearPhase();
                return;
            }
            if (_taskFinished) return;
            var name = (string?)detail["name"];
            if (name == "AutoLiveStart" && message == "Node.Action.Succeeded") _autoLive = true;
            else if ((name == "AutoLiveResult" && message is "Node.Recognition.Succeeded" or "Node.Action.Starting")
                || message is "Node.Action.Failed" or "Node.PipelineNode.Failed") _autoLive = false;
        }
    }
}

public sealed class LiveStopConfirmationCoordinator
{
    private int _pending;

    public async Task RequestAsync(LivePerformanceState state, Func<Task<bool>> confirm, Action<LiveTaskSnapshot?> stop,
        Action<Exception>? onError = null)
    {
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        try
        {
            var snapshot = state.Capture();
            if (snapshot == null)
            {
                stop(null);
                return;
            }
            if ((!snapshot.Value.Performing || await confirm()) && state.IsCurrent(snapshot.Value))
                stop(snapshot);
        }
        catch (Exception ex)
        {
            // 确认窗口不可用时保留演奏；内部停止仍走原入口。
            onError?.Invoke(ex);
        }
        finally { Volatile.Write(ref _pending, 0); }
    }
}
