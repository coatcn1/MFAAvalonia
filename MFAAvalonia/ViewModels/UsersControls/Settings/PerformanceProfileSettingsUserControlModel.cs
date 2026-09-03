using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls.Notifications;
using MFAAvalonia.Helper;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SukiUI.Toasts;
using SukiUI.MessageBox;
using SukiUI.Controls;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public sealed partial class PerformanceProfileSettingsUserControlModel : ViewModelBase
{
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly DebouncedAsyncAction _runtimeAutoSave;
    private readonly DebouncedAsyncAction _profileAutoSave;
    private readonly HashSet<string> _acceptedProfileEditConfirmed = [];
    private JObject? _pendingRuntimeOptions;
    private ProfileSaveRequest? _pendingProfileSave;
    private int _suspendAutoSave;

    public string[] Difficulties { get; } = ["Easy", "Normal", "Hard", "Expert", "Special"];
    public ObservableCollection<PerformanceProfileItem> Profiles { get; } = [];
    public ObservableCollection<ArtifactLocationItem> ArtifactLocations { get; } = [];

    [ObservableProperty] private string _difficulty = "Easy";
    [ObservableProperty] private PerformanceProfileItem? _selectedProfile;
    [ObservableProperty] private string _selectionText = "尚未加载";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private string _chartCatalogText = "尚未读取本地谱面清单";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _targetFps = 60;
    [ObservableProperty] private int _timingOffsetMs;
    [ObservableProperty] private int _frameTimeoutMs = 150;
    [ObservableProperty] private int _playfieldTimeoutMs = 1500;
    [ObservableProperty] private bool _lifeSafetyEnabled = true;
    [ObservableProperty] private int _lifeExitThresholdPercent = 20;
    [ObservableProperty] private bool _rehearsalIgnoreLifeSafety = true;
    [ObservableProperty] private bool _skipProcessConflictCleanup;
    [ObservableProperty] private bool _gameEffectSettingsEnabled = true;
    [ObservableProperty] private int _noteSkinType = 1;
    [ObservableProperty] private bool _judgementAssistEffect = true;
    [ObservableProperty] private int _tapEffect = 1;
    [ObservableProperty] private bool _chartPredictionEnabled = true;
    [ObservableProperty] private bool _chartPredictPresses = true;
    [ObservableProperty] private bool _nativeRealtimeEnabled;
    [ObservableProperty] private int _playFailureRetryCount = 1;
    [ObservableProperty] private decimal _easyCalibrationSpeed = 2.00m;
    [ObservableProperty] private decimal _normalCalibrationSpeed = 2.00m;
    [ObservableProperty] private decimal _hardCalibrationSpeed = 2.00m;
    [ObservableProperty] private decimal _expertCalibrationSpeed = 5.00m;
    [ObservableProperty] private decimal _specialCalibrationSpeed = 5.00m;

    public PerformanceProfileSettingsUserControlModel()
    {
        var debounce = TimeSpan.FromMilliseconds(500);
        _runtimeAutoSave = new DebouncedAsyncAction(
            SavePendingRuntimeOptionsAsync,
            debounce,
            exception => ReportAutoSaveFailureAsync("演出运行设置", exception));
        _profileAutoSave = new DebouncedAsyncAction(
            SavePendingProfileAsync,
            debounce,
            exception => ReportAutoSaveFailureAsync("Profile 参数", exception));
    }

    partial void OnDifficultyChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedProfileChanged(PerformanceProfileItem? value)
    {
        if (value == null) return;
        _suspendAutoSave++;
        try
        {
            LoadProfileSettings(value);
        }
        finally
        {
            _suspendAutoSave--;
        }
    }

    partial void OnTargetFpsChanged(int value) => ScheduleProfileAutoSave();
    partial void OnTimingOffsetMsChanged(int value) => ScheduleProfileAutoSave();
    partial void OnFrameTimeoutMsChanged(int value) => ScheduleProfileAutoSave();
    partial void OnPlayfieldTimeoutMsChanged(int value) => ScheduleProfileAutoSave();
    partial void OnLifeSafetyEnabledChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnLifeExitThresholdPercentChanged(int value) => ScheduleRuntimeAutoSave();
    partial void OnRehearsalIgnoreLifeSafetyChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnSkipProcessConflictCleanupChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnGameEffectSettingsEnabledChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnNoteSkinTypeChanged(int value) => ScheduleRuntimeAutoSave();
    partial void OnJudgementAssistEffectChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnTapEffectChanged(int value) => ScheduleRuntimeAutoSave();
    partial void OnChartPredictionEnabledChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnChartPredictPressesChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnNativeRealtimeEnabledChanged(bool value) => ScheduleRuntimeAutoSave();
    partial void OnPlayFailureRetryCountChanged(int value) => ScheduleRuntimeAutoSave();
    partial void OnEasyCalibrationSpeedChanged(decimal value) => ScheduleRuntimeAutoSave();
    partial void OnNormalCalibrationSpeedChanged(decimal value) => ScheduleRuntimeAutoSave();
    partial void OnHardCalibrationSpeedChanged(decimal value) => ScheduleRuntimeAutoSave();
    partial void OnExpertCalibrationSpeedChanged(decimal value) => ScheduleRuntimeAutoSave();
    partial void OnSpecialCalibrationSpeedChanged(decimal value) => ScheduleRuntimeAutoSave();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _suspendAutoSave++;
        try
        {
            await RunAsync(async () =>
            {
                var result = await ProfileManagerClient.InvokeAsync(new JObject
                {
                    ["operation"] = "list", ["difficulty"] = Difficulty
                });
                Profiles.Clear();
                foreach (var token in result["profiles"] as JArray ?? [])
                    Profiles.Add(new PerformanceProfileItem((JObject)token));

                var runtime = (JObject?)result["runtime_options"];
                LifeSafetyEnabled = runtime?.Value<bool?>("life_safety_enabled") ?? true;
                LifeExitThresholdPercent = Math.Clamp(
                    (runtime?.Value<int?>("life_exit_threshold") ?? 200) / 10, 1, 99);
                RehearsalIgnoreLifeSafety =
                    runtime?.Value<bool?>("rehearsal_ignore_life_safety") ?? true;
                SkipProcessConflictCleanup =
                    runtime?.Value<bool?>("skip_process_conflict_cleanup") ?? false;
                GameEffectSettingsEnabled =
                    runtime?.Value<bool?>("game_effect_settings_enabled") ?? true;
                NoteSkinType = Math.Clamp(
                    runtime?.Value<int?>("note_skin_type") ?? 1, 1, 7);
                JudgementAssistEffect =
                    runtime?.Value<bool?>("judgement_assist_effect") ?? true;
                TapEffect = Math.Clamp(
                    runtime?.Value<int?>("tap_effect") ?? 1, 1, 5);
                ChartPredictionEnabled =
                    runtime?.Value<bool?>("chart_prediction_enabled") ?? true;
                ChartPredictPresses =
                    runtime?.Value<bool?>("chart_predict_presses") ?? true;
                NativeRealtimeEnabled =
                    runtime?.Value<bool?>("native_realtime_enabled") ?? false;
                PlayFailureRetryCount = Math.Clamp(
                    runtime?.Value<int?>("play_failure_retry_count") ?? 1, 0, 3);
                var speeds = (JObject?)runtime?["calibration_note_speeds"];
                EasyCalibrationSpeed = speeds?.Value<decimal?>("Easy") ?? 2.00m;
                NormalCalibrationSpeed = speeds?.Value<decimal?>("Normal") ?? 2.00m;
                HardCalibrationSpeed = speeds?.Value<decimal?>("Hard") ?? 2.00m;
                ExpertCalibrationSpeed = speeds?.Value<decimal?>("Expert") ?? 5.00m;
                SpecialCalibrationSpeed = speeds?.Value<decimal?>("Special") ?? 5.00m;
                ArtifactLocations.Clear();
                foreach (var item in await ProfileManagerClient.LoadArtifactLocationsAsync())
                    ArtifactLocations.Add(item);
                ChartCatalogText = await ProfileManagerClient.LoadChartCatalogStatusAsync();

                var selection = (JObject?)result["selection"];
                var selectedName = selection?.Value<string>("profile");
                SelectedProfile = Profiles.FirstOrDefault(profile => profile.Filename == selectedName)
                                  ?? Profiles.FirstOrDefault();
                var mode = selection?.Value<string>("mode") == "pinned" ? "钉选" : "自动";
                var source = selection?.Value<string>("source_difficulty") ?? "无";
                var error = selection?.Value<string>("error");
                SelectionText = string.IsNullOrEmpty(error)
                    ? $"{mode} · {selectedName ?? "无可用 Profile"} · 来源难度 {source}"
                    : $"{mode}已阻止正式演奏：{error}";
                StatusText = $"共 {Profiles.Count} 个本机 Profile；修改后自动保存";
            });
        }
        finally
        {
            _suspendAutoSave--;
        }
    }

    private void LoadProfileSettings(PerformanceProfileItem value)
    {
        TargetFps = value.Settings.Value<int?>("target_fps") ?? 60;
        TimingOffsetMs = value.Settings.Value<int?>("timing_offset_ms") ?? 0;
        FrameTimeoutMs = value.Settings.Value<int?>("frame_timeout_ms") ?? 150;
        PlayfieldTimeoutMs = value.Settings.Value<int?>("playfield_timeout_ms") ?? 1500;
    }

    private JObject CaptureRuntimeOptions() => new()
    {
        ["life_safety_enabled"] = LifeSafetyEnabled,
        ["life_exit_threshold"] = LifeExitThresholdPercent * 10,
        ["rehearsal_ignore_life_safety"] = RehearsalIgnoreLifeSafety,
        ["skip_process_conflict_cleanup"] = SkipProcessConflictCleanup,
        ["game_effect_settings_enabled"] = GameEffectSettingsEnabled,
        ["note_skin_type"] = NoteSkinType,
        ["judgement_assist_effect"] = JudgementAssistEffect,
        ["tap_effect"] = TapEffect,
        ["chart_prediction_enabled"] = ChartPredictionEnabled,
        ["chart_predict_presses"] = ChartPredictPresses,
        ["native_realtime_enabled"] = NativeRealtimeEnabled,
        ["play_failure_retry_count"] = PlayFailureRetryCount,
        ["calibration_note_speeds"] = new JObject
        {
            ["Easy"] = EasyCalibrationSpeed,
            ["Normal"] = NormalCalibrationSpeed,
            ["Hard"] = HardCalibrationSpeed,
            ["Expert"] = ExpertCalibrationSpeed,
            ["Special"] = SpecialCalibrationSpeed
        }
    };

    private JObject CaptureProfileSettings() => new()
    {
        ["target_fps"] = TargetFps,
        ["timing_offset_ms"] = TimingOffsetMs,
        ["frame_timeout_ms"] = FrameTimeoutMs,
        ["playfield_timeout_ms"] = PlayfieldTimeoutMs
    };

    private void ScheduleRuntimeAutoSave()
    {
        if (_suspendAutoSave > 0) return;
        _pendingRuntimeOptions = CaptureRuntimeOptions();
        StatusText = "演出运行设置已修改，正在自动保存…";
        _runtimeAutoSave.Schedule();
    }

    private void ScheduleProfileAutoSave()
    {
        if (_suspendAutoSave > 0 || SelectedProfile == null) return;
        _pendingProfileSave = new ProfileSaveRequest(
            SelectedProfile.Filename,
            SelectedProfile.Accepted,
            CaptureProfileSettings());
        StatusText = "Profile 参数已修改，正在自动保存…";
        _profileAutoSave.Schedule();
    }

    private async Task SavePendingRuntimeOptionsAsync()
    {
        var options = (JObject?)_pendingRuntimeOptions?.DeepClone();
        if (options == null) return;
        await _saveGate.WaitAsync();
        try
        {
            await ProfileManagerClient.InvokeAsync(new JObject
            {
                ["operation"] = "update-runtime-options",
                ["runtime_options"] = options
            });
        }
        finally
        {
            _saveGate.Release();
        }
        StatusText = "演出运行设置已自动保存";
    }

    private async Task SavePendingProfileAsync()
    {
        var request = _pendingProfileSave;
        if (request == null) return;
        if (
            request.Accepted
            && !_acceptedProfileEditConfirmed.Contains(request.Filename)
        )
        {
            var answer = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
            {
                Content = "修改后此 Profile 将撤销验收，正式演奏与挑战演出会被阻止，直到重新校准。是否继续自动保存？",
                ActionButtonsPreset = SukiMessageBoxButtons.YesNo,
                IconPreset = SukiMessageBoxIcons.Warning
            }, new SukiMessageBoxOptions { Title = "确认修改已验收 Profile" });
            if (answer is not SukiMessageBoxResult.Yes)
            {
                _profileAutoSave.Cancel();
                _pendingProfileSave = null;
                if (SelectedProfile?.Filename == request.Filename)
                {
                    _suspendAutoSave++;
                    try
                    {
                        LoadProfileSettings(SelectedProfile);
                    }
                    finally
                    {
                        _suspendAutoSave--;
                    }
                }
                StatusText = "已取消修改，并恢复已验收 Profile 的原值";
                return;
            }
            _acceptedProfileEditConfirmed.Add(request.Filename);
        }

        await _saveGate.WaitAsync();
        try
        {
            await ProfileManagerClient.InvokeAsync(new JObject
            {
                ["operation"] = "update-settings",
                ["profile"] = request.Filename,
                ["settings"] = request.Settings
            });
        }
        finally
        {
            _saveGate.Release();
        }
        StatusText = request.Accepted
            ? "Profile 参数已自动保存；验收已撤销，需重新校准"
            : "Profile 参数已自动保存";
        if (request.Accepted)
            await RefreshAsync();
    }

    private Task ReportAutoSaveFailureAsync(string scope, Exception exception)
    {
        StatusText = $"{scope}自动保存失败（已重试一次）：{exception.Message}";
        Instances.ToastManager.CreateToast()
            .WithTitle("演出设置")
            .WithContent(StatusText)
            .OfType(NotificationType.Error)
            .Dismiss()
            .After(TimeSpan.FromSeconds(8))
            .Queue();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private async Task SyncBestdoriChartsAsync()
    {
        var succeeded = await RunAsync(async () =>
        {
            StatusText = "正在同步 Bestdori 谱面；已有文件会校验后复用…";
            await ProfileManagerClient.SyncBestdoriChartsAsync(message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                    StatusText = $"谱面同步：{message}";
            });
            ChartCatalogText = await ProfileManagerClient.LoadChartCatalogStatusAsync();
            StatusText = $"Bestdori 谱面同步完成；{ChartCatalogText}";
        });
        if (!succeeded)
            ChartCatalogText = await ProfileManagerClient.LoadChartCatalogStatusAsync();
    }

    [RelayCommand]
    private async Task CopyPathAsync(ArtifactLocationItem? item)
    {
        if (item == null) return;
        await RunAsync(async () =>
        {
            var clipboard = Instances.Clipboard ?? throw new InvalidOperationException("剪贴板当前不可用");
            await clipboard.SetTextAsync(item.Path);
            StatusText = $"已复制路径：{item.Path}";
        });
    }

    [RelayCommand]
    private async Task OpenDirectoryAsync(ArtifactLocationItem? item)
    {
        if (item == null) return;
        await RunAsync(() =>
        {
            if (!Directory.Exists(item.Path))
                throw new DirectoryNotFoundException($"目录不存在：{item.Path}");
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true });
            StatusText = $"已打开目录：{item.Path}";
            return Task.CompletedTask;
        });
    }

    [RelayCommand]
    private async Task PinAsync()
    {
        if (SelectedProfile == null) return;
        var succeeded = await RunAsync(async () =>
        {
            await ProfileManagerClient.InvokeAsync(new JObject
            {
                ["operation"] = "pin", ["difficulty"] = Difficulty,
                ["profile"] = SelectedProfile.Filename
            });
        });
        if (succeeded) await RefreshAsync();
    }

    [RelayCommand]
    private async Task UseAutomaticAsync()
    {
        var succeeded = await RunAsync(async () =>
        {
            await ProfileManagerClient.InvokeAsync(new JObject
            {
                ["operation"] = "unpin", ["difficulty"] = Difficulty
            });
        });
        if (succeeded) await RefreshAsync();
    }

    private async Task<bool> RunAsync(Func<Task> action)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try { await action(); return true; }
        catch (Exception ex)
        {
            StatusText = $"Profile 管理器调用失败：{ex.Message}";
            Instances.ToastManager.CreateToast().WithTitle("演出设置").WithContent(StatusText).OfType(NotificationType.Error).Dismiss().After(TimeSpan.FromSeconds(8)).Queue();
            return false;
        }
        finally { IsBusy = false; }
    }
}

internal sealed record ProfileSaveRequest(
    string Filename,
    bool Accepted,
    JObject Settings);

public sealed class DebouncedAsyncAction : IDisposable
{
    private readonly object _sync = new();
    private readonly Func<Task> _action;
    private readonly Func<Exception, Task> _onFailure;
    private readonly TimeSpan _delay;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private CancellationTokenSource? _pending;
    private bool _disposed;

    public DebouncedAsyncAction(
        Func<Task> action,
        TimeSpan delay,
        Func<Exception, Task>? onFailure = null)
    {
        _action = action;
        _delay = delay;
        _onFailure = onFailure ?? (_ => Task.CompletedTask);
    }

    public void Schedule()
    {
        CancellationTokenSource pending;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _pending?.Cancel();
            _pending?.Dispose();
            pending = new CancellationTokenSource();
            _pending = pending;
        }
        _ = RunAsync(pending);
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
    }

    private async Task RunAsync(CancellationTokenSource pending)
    {
        try
        {
            await Task.Delay(_delay, pending.Token);
            await _serial.WaitAsync(pending.Token);
            try
            {
                if (pending.IsCancellationRequested) return;
                Exception? failure = null;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        await _action();
                        return;
                    }
                    catch (Exception ex)
                    {
                        failure = ex;
                        if (attempt == 0)
                            await Task.Delay(TimeSpan.FromMilliseconds(150));
                    }
                }
                await _onFailure(failure!);
            }
            finally
            {
                _serial.Release();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_pending, pending))
                    _pending = null;
            }
            pending.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _pending?.Cancel();
            _pending?.Dispose();
            _pending = null;
        }
        _serial.Dispose();
    }
}

public sealed class ArtifactLocationItem
{
    public ArtifactLocationItem(string key, string path)
    {
        Key = key;
        Path = path;
        Exists = Directory.Exists(path);
        LastUpdated = Exists ? Directory.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm:ss") : "—";
    }

    public string Key { get; }
    public string Path { get; }
    public bool Exists { get; }
    public string ExistsText => Exists ? "存在" : "不存在";
    public string LastUpdated { get; }
    public string Label => Key switch
    {
        "profiles" => "Profile 目录",
        "realtime_recordings" => "实时录像与轨迹",
        "result_captures" => "结算截图与 JSON",
        "maafw_debug" => "MaaFW 日志与错误截图",
        "mfa_logs" => "MFA 界面日志",
        _ => Key
    };
}

public sealed class PerformanceProfileItem
{
    private readonly JObject _value;
    public PerformanceProfileItem(JObject value) => _value = value;
    public string Filename => _value.Value<string>("filename") ?? string.Empty;
    public string Difficulty => _value.Value<string>("difficulty") ?? "?";
    public bool Accepted => _value.Value<bool?>("accepted") == true;
    public string AcceptedText => Accepted ? "已验收" : "未验收";
    public string EnvironmentMatchText => _value.Value<bool?>("environment_match") switch { true => "匹配", false => "不匹配", _ => "未知" };
    public string CreatedAt => _value.Value<string>("created_at") ?? "—";
    public string AcceptedAt => _value.Value<string>("accepted_at") ?? "—";
    public string ModifiedAt => _value.Value<string>("modified_at") ?? "—";
    public string NoteSpeedText =>
        ((JObject?)_value["environment"])?.Value<decimal?>("note_speed")?.ToString("F2")
        ?? "—";
    public JObject Settings => (JObject?)_value["settings"] ?? new JObject();
    public string EnvironmentText
    {
        get
        {
            var env = (JObject?)_value["environment"];
            if (env == null) return "旧版环境记录不可用";
            var resolution = env["resolution"] is JArray r ? string.Join("×", r.Values<int>()) : "?";
            return $"{resolution} · DPI {env["dpi"]} · {env["game_fps"]} FPS · 画质 {env["render_quality"]} · 音符速度 {env["note_speed"]}";
        }
    }
    public string CalibrationRecordsText
    {
        get
        {
            var rehearsals = _value["rehearsals"] as JArray;
            var formal = _value["formal_validation"] ?? _value["formal"];
            if (rehearsals == null || formal == null) return "旧版记录不可用";
            var lines = rehearsals.Select((item, index) => $"排练 {index + 1}：{FormatRecord(item)}").ToList();
            lines.Add($"正式验证：{FormatRecord(formal)}");
            return string.Join(Environment.NewLine, lines);
        }
    }
    private static string FormatRecord(JToken token)
    {
        // Calibration sessions keep recognition fields under ``result`` so
        // the attempt can also carry stage, artifact paths and stop reasons.
        // Older profiles stored the fields directly on the attempt.  Read
        // both layouts instead of rendering every judgement count as "—".
        var result = token["result"] ?? token;
        string V(string key) => result[key]?.ToString() ?? "—";
        var suggestion = token["suggested_timing_offset_ms"]?.ToString()
                         ?? token["timing_suggestion_ms"]?.ToString()
                         ?? result["suggested_timing_offset_ms"]?.ToString()
                         ?? result["timing_suggestion_ms"]?.ToString() ?? "—";
        return $"歌曲 {V("song_id")} · P/G/Gd/B/M {V("perfect")}/{V("great")}/{V("good")}/{V("bad")}/{V("miss")} · Fast/Slow {V("fast")}/{V("slow")} · 命中率 {V("hit_rate")} · 置信度 {V("confidence")} · 时序建议 {suggestion} ms · 通过 {V("passed")}";
    }
}

internal static class ProfileManagerClient
{
    private static async Task<JObject> LoadConfigAsync()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "profile-manager.json");
        if (!File.Exists(configPath)) throw new FileNotFoundException("未找到部署生成的 profile-manager.json", configPath);
        return JObject.Parse(await File.ReadAllTextAsync(configPath));
    }

    public static async Task<ArtifactLocationItem[]> LoadArtifactLocationsAsync()
    {
        var config = await LoadConfigAsync();
        var paths = (JObject?)config["artifact_paths"]
                    ?? throw new InvalidDataException("profile-manager.json 缺少 artifact_paths，请重新运行部署脚本");
        return paths.Properties()
            .Select(property => new ArtifactLocationItem(property.Name, property.Value.ToString()))
            .ToArray();
    }

    public static async Task<string> LoadChartCatalogStatusAsync()
    {
        try
        {
            var config = await LoadConfigAsync();
            var sync = (JObject?)config["chart_sync"]
                       ?? throw new InvalidDataException("profile-manager.json 缺少 chart_sync，请重新运行部署脚本");
            var manifestPath = sync.Value<string>("manifest_path")
                               ?? throw new InvalidDataException("chart_sync 缺少 manifest_path");
            if (!File.Exists(manifestPath))
                return "本地谱面清单不存在，请先同步";
            var manifest = JObject.Parse(await File.ReadAllTextAsync(manifestPath));
            return FormatChartCatalogStatus(manifest);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            return $"本地谱面状态不可用：{ex.Message}";
        }
    }

    internal static string FormatChartCatalogStatus(JObject manifest)
    {
        var summary = (JObject?)manifest["summary"]
                      ?? throw new InvalidDataException("谱面清单缺少 summary");
        var songs = summary.Value<int?>("songs_with_charts") ?? 0;
        var charts = summary.Value<int?>("charts") ?? 0;
        var jackets = summary.Value<int?>("jackets") ?? 0;
        var errors = (summary.Value<int?>("recoverable_errors") ?? 0)
                     + (summary.Value<int?>("fatal_errors") ?? 0);
        var generatedAt = manifest.Value<string>("generated_at") ?? "未知时间";
        return $"{songs} 首 / {charts} 张谱面 / {jackets} 个封面 / {errors} 个错误 · 更新于 {generatedAt}";
    }

    public static async Task SyncBestdoriChartsAsync(Action<string> progress)
    {
        var config = await LoadConfigAsync();
        var sync = (JObject?)config["chart_sync"]
                   ?? throw new InvalidDataException("profile-manager.json 缺少 chart_sync，请重新运行部署脚本");
        var startInfo = new ProcessStartInfo(sync.Value<string>("child_exec")
                                             ?? throw new InvalidDataException("chart_sync 缺少 child_exec"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = sync.Value<string>("working_directory") ?? AppContext.BaseDirectory
        };
        foreach (var arg in sync["child_args"]?.Values<string>() ?? [])
            startInfo.ArgumentList.Add(arg);
        startInfo.Environment["PYTHONIOENCODING"] = "utf-8";

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException("无法启动 Bestdori 谱面同步器");
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        try
        {
            while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                progress(line);
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            throw new TimeoutException("Bestdori 谱面同步超过 45 分钟，已终止同步器");
        }
        var stderr = (await stderrTask).Trim();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr)
                    ? $"谱面同步器退出码 {process.ExitCode}"
                    : stderr);
    }

    public static async Task<JObject> InvokeAsync(JObject request)
    {
        var config = await LoadConfigAsync();
        var startInfo = new ProcessStartInfo(config.Value<string>("child_exec") ?? throw new InvalidDataException("缺少 child_exec"))
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var arg in config["child_args"]?.Values<string>() ?? []) startInfo.ArgumentList.Add(arg);
        var environment = (JObject?)config["environment"];
        if (request.Value<string>("operation") == "list" && environment != null)
        {
            var effectiveEnvironment = (JObject)environment.DeepClone();
            var difficulty = request.Value<string>("difficulty");
            if (difficulty is "Expert" or "Special")
                effectiveEnvironment["note_speed"] = 5.0;
            request["environment"] = effectiveEnvironment;
        }
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动 Profile 管理器");
        await process.StandardInput.WriteAsync(request.ToString(Formatting.None));
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(timeout.Token);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var response = JObject.Parse(stdout);
        if (process.ExitCode != 0 || response.Value<bool>("ok") != true)
            throw new InvalidOperationException(response.Value<string>("error") ?? stderr.Trim());
        return (JObject?)response["result"] ?? throw new InvalidDataException("Profile 管理器响应缺少 result");
    }
}
