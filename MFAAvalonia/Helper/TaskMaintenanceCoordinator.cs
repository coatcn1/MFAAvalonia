using System;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public sealed class TaskMaintenanceCoordinator
{
    public static TaskMaintenanceCoordinator Shared { get; } = new();
    private readonly object _gate = new();
    private int _tasks;
    private bool _maintenance;
    private TaskCompletionSource _changed = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsBusy { get { lock (_gate) return _tasks > 0 || _maintenance; } }

    public static bool CheckQueuedTasks(Func<bool> read)
    {
        try { return read(); }
        // 实例集合由 UI 增删；维护线程遇到并发变更时保守等待下一轮，不同步调用 UI。
        catch (InvalidOperationException) { return true; }
    }

    public async Task<IDisposable> BeginTaskAsync(CancellationToken token)
    {
        while (true)
        {
            Task changed;
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!_maintenance)
                {
                    _tasks++;
                    return new Lease(this, false);
                }
                changed = _changed.Task;
            }
            await changed.WaitAsync(token);
        }
    }

    public IDisposable? TryBeginMaintenance(Func<bool> hasQueuedTasks)
    {
        lock (_gate)
        {
            // 排队任务优先；租约同时保护已出队但仍在执行的最后一项。
            if (_maintenance || _tasks > 0 || hasQueuedTasks()) return null;
            _maintenance = true;
            return new Lease(this, true);
        }
    }

    private void Release(bool maintenance)
    {
        lock (_gate)
        {
            if (maintenance) _maintenance = false;
            else _tasks--;
            var previous = _changed;
            _changed = NewSignal();
            previous.TrySetResult();
        }
    }

    private sealed class Lease(TaskMaintenanceCoordinator owner, bool maintenance) : IDisposable
    {
        private TaskMaintenanceCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(maintenance);
    }
}
