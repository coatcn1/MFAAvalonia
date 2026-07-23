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
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public sealed partial class PerformanceProfileSettingsUserControlModel : ViewModelBase
{
    public string[] Difficulties { get; } = ["Easy", "Normal", "Hard", "Expert", "Special"];
    public ObservableCollection<PerformanceProfileItem> Profiles { get; } = [];
    public ObservableCollection<ArtifactLocationItem> ArtifactLocations { get; } = [];

    [ObservableProperty] private string _difficulty = "Easy";
    [ObservableProperty] private PerformanceProfileItem? _selectedProfile;
    [ObservableProperty] private string _selectionText = "尚未加载";
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _targetFps = 60;
    [ObservableProperty] private int _timingOffsetMs;
    [ObservableProperty] private int _frameTimeoutMs = 150;
    [ObservableProperty] private int _playfieldTimeoutMs = 1500;
    [ObservableProperty] private bool _lifeSafetyEnabled = true;
    [ObservableProperty] private int _lifeExitThresholdPercent = 20;
    [ObservableProperty] private bool _rehearsalIgnoreLifeSafety = true;

    partial void OnDifficultyChanged(string value) => _ = RefreshAsync();

    partial void OnSelectedProfileChanged(PerformanceProfileItem? value)
    {
        if (value == null) return;
        TargetFps = value.Settings.Value<int?>("target_fps") ?? 60;
        TimingOffsetMs = value.Settings.Value<int?>("timing_offset_ms") ?? 0;
        FrameTimeoutMs = value.Settings.Value<int?>("frame_timeout_ms") ?? 150;
        PlayfieldTimeoutMs = value.Settings.Value<int?>("playfield_timeout_ms") ?? 1500;
    }

    [RelayCommand]
    public async Task RefreshAsync()
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
            ArtifactLocations.Clear();
            foreach (var item in await ProfileManagerClient.LoadArtifactLocationsAsync())
                ArtifactLocations.Add(item);

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
            StatusText = $"共 {Profiles.Count} 个本机 Profile";
        });
    }

    [RelayCommand]
    private async Task SaveRuntimeOptionsAsync()
    {
        var succeeded = await RunAsync(async () =>
        {
            await ProfileManagerClient.InvokeAsync(new JObject
            {
                ["operation"] = "update-runtime-options",
                ["runtime_options"] = new JObject
                {
                    ["life_safety_enabled"] = LifeSafetyEnabled,
                    ["life_exit_threshold"] = LifeExitThresholdPercent * 10,
                    ["rehearsal_ignore_life_safety"] = RehearsalIgnoreLifeSafety
                }
            });
        });
        if (succeeded)
        {
            StatusText =
                $"生命保护已保存：{(LifeSafetyEnabled ? "开启" : "关闭")}，阈值 {LifeExitThresholdPercent}%；" +
                $"排练忽略生命：{(RehearsalIgnoreLifeSafety ? "开启" : "关闭")}";
            await RefreshAsync();
        }
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

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (SelectedProfile == null) return;
        if (SelectedProfile.Accepted)
        {
            var answer = await SukiMessageBox.ShowDialog(new SukiMessageBoxHost
            {
                Content = "保存后此 Profile 将撤销验收，正式演奏与挑战演出会被阻止，直到重新校准。是否继续？",
                ActionButtonsPreset = SukiMessageBoxButtons.YesNo,
                IconPreset = SukiMessageBoxIcons.Warning
            }, new SukiMessageBoxOptions { Title = "确认修改已验收 Profile" });
            if (answer is not SukiMessageBoxResult.Yes) return;
        }
        var succeeded = await RunAsync(async () =>
        {
            await ProfileManagerClient.InvokeAsync(new JObject
            {
                ["operation"] = "update-settings", ["profile"] = SelectedProfile.Filename,
                ["settings"] = new JObject
                {
                    ["target_fps"] = TargetFps, ["timing_offset_ms"] = TimingOffsetMs,
                    ["frame_timeout_ms"] = FrameTimeoutMs,
                    ["playfield_timeout_ms"] = PlayfieldTimeoutMs
                }
            });
        });
        if (succeeded)
        {
            await RefreshAsync();
            StatusText = "已保存；Profile 验收已撤销，需重新校准。";
        }
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
        string V(string key) => token[key]?.ToString() ?? "—";
        var suggestion = token["suggested_timing_offset_ms"]?.ToString()
                         ?? token["timing_suggestion_ms"]?.ToString() ?? "—";
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
