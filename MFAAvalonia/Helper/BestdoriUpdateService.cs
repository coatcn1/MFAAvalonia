using System;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.ViewModels.UsersControls.Settings;

namespace MFAAvalonia.Helper;

public sealed record BestdoriUpdatePlan(bool Enabled, int IntervalHours, DateTimeOffset? LastSuccess);

public sealed class BestdoriUpdateService
{
    public static BestdoriUpdateService Shared { get; } = new(
        ProfileManagerClient.LoadAutoUpdatePlanAsync, ProfileManagerClient.SyncBestdoriChartsAsync,
        TaskMaintenanceCoordinator.Shared, () => TaskMaintenanceCoordinator.CheckQueuedTasks(() => MaaProcessor.Processors.Any(p => p.TaskQueue.Count > 0)));
    private readonly Func<CancellationToken, Task<BestdoriUpdatePlan>> _readPlan;
    private readonly Func<Action<string>, CancellationToken, Task> _sync;
    private readonly TaskMaintenanceCoordinator _coordinator;
    private readonly Func<bool> _hasQueuedTasks;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _checks = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _automatic;
    private int _automaticGeneration;
    private bool? _automaticAllowedByUi;
    private DateTimeOffset? _retryAfter;
    private string? _lastError;
    private Task? _loop;
    public string Status { get; private set; } = "自动更新尚未检查";
    public event Action? StatusChanged;

    public BestdoriUpdateService(Func<CancellationToken, Task<BestdoriUpdatePlan>> readPlan,
        Func<Action<string>, CancellationToken, Task> sync, TaskMaintenanceCoordinator coordinator,
        Func<bool> hasQueuedTasks, Func<DateTimeOffset>? now = null)
    {
        _readPlan = readPlan;
        _sync = sync;
        _coordinator = coordinator;
        _hasQueuedTasks = hasQueuedTasks;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    private void SetStatus(string value)
    {
        Status = value;
        StatusChanged?.Invoke();
    }

    public void Start()
    {
        lock (_gate) _loop ??= Task.Run(LoopAsync);
    }

    private async Task LoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                await CheckAsync(_shutdown.Token).ConfigureAwait(false);
                await Task.Delay(TimeSpan.FromMinutes(1), _shutdown.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public void CancelAutomatic() => SetAutomaticEnabled(false);

    public void SetAutomaticEnabled(bool enabled)
    {
        lock (_gate)
        {
            _automaticAllowedByUi = enabled;
            _automaticGeneration++;
            if (!enabled) _automatic?.Cancel();
        }
    }

    public CancellationToken ShutdownToken => _shutdown.Token;
    public void Shutdown() => _shutdown.Cancel();

    public async Task CheckAsync(CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        if (!await _checks.WaitAsync(0, linked.Token).ConfigureAwait(false)) return;
        int generation;
        lock (_gate) generation = _automaticGeneration;
        try
        {
            var plan = await _readPlan(linked.Token).ConfigureAwait(false);
            if (plan.IntervalHours is < 1 or > 720) throw new InvalidOperationException("更新间隔必须为 1..720 小时");
            var next = plan.LastSuccess?.AddHours(plan.IntervalHours);
            var history = $"上次成功：{plan.LastSuccess?.ToLocalTime().ToString("g") ?? "无"}";
            if (!plan.Enabled)
            {
                SetStatus($"自动更新已关闭；{history}");
                return;
            }
            if (_retryAfter is { } retry && _now() < retry)
            {
                SetStatus($"{history}；最近错误：{_lastError}；重试不早于 {retry.ToLocalTime():g}");
                return;
            }
            if (next is { } due && _now() < due)
            {
                SetStatus($"{history}；下次检查：{due.ToLocalTime():g}");
                return;
            }
            using var lease = _coordinator.TryBeginMaintenance(_hasQueuedTasks);
            if (lease == null)
            {
                SetStatus($"更新已到期，等待全部实例任务结束或维护完成；{history}");
                return;
            }
            using var automatic = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
            lock (_gate)
            {
                // 读取配置期间关闭开关也必须阻止这次检查继续联网。
                if (generation != _automaticGeneration || _automaticAllowedByUi == false) return;
                _automatic = automatic;
            }
            try { await RunSyncAsync("自动", automatic.Token).ConfigureAwait(false); }
            finally { lock (_gate) _automatic = null; }
        }
        catch (OperationCanceledException)
        {
            SetStatus("自动更新已取消，保留原有可用谱面");
        }
        catch (Exception ex)
        {
            _retryAfter = _now().AddMinutes(15);
            _lastError = ex.Message;
            SetStatus($"最近自动更新错误：{ex.Message}；重试不早于 {_retryAfter.Value.ToLocalTime():g}");
            LoggerHelper.Warning(Status);
        }
        finally { _checks.Release(); }
    }

    public async Task RunManualAsync(Action<string> progress, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _shutdown.Token);
        using var lease = _coordinator.TryBeginMaintenance(_hasQueuedTasks)
                          ?? throw new InvalidOperationException("所有实例任务结束且当前维护完成后才能手动同步");
        await RunSyncAsync("手动", linked.Token, progress).ConfigureAwait(false);
    }

    private async Task RunSyncAsync(string mode, CancellationToken token, Action<string>? progress = null)
    {
        LoggerHelper.Info($"Bestdori {mode}同步开始");
        SetStatus($"{mode}同步中…");
        try
        {
            await _sync(message => { SetStatus($"{mode}同步：{message}"); progress?.Invoke(message); }, token).ConfigureAwait(false);
            var plan = await _readPlan(token).ConfigureAwait(false);
            _retryAfter = null;
            SetStatus($"{mode}同步完成；上次成功：{plan.LastSuccess?.ToLocalTime().ToString("g") ?? "无"}；"
                      + $"下次检查：{plan.LastSuccess?.AddHours(plan.IntervalHours).ToLocalTime().ToString("g") ?? "待确认"}");
            LoggerHelper.Info($"Bestdori {mode}同步完成");
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{mode}同步已取消，保留原有可用谱面");
            throw;
        }
        catch (Exception ex)
        {
            _retryAfter = _now().AddMinutes(15);
            _lastError = ex.Message;
            SetStatus($"最近{mode}同步错误：{ex.Message}");
            throw;
        }
    }
}
