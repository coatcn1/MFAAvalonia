using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Platform.Storage;
using MaaFramework.Binding.Interop.Native;
using MFAAvalonia.Configuration;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.Converters;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.ViewModels.Other;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.Windows;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.ViewModels.UsersControls.Settings;

public partial class VersionUpdateSettingsUserControlModel : ViewModelBase
{
    [ObservableProperty] private bool _showLocalPackageUpdate;

    public enum UpdateProxyType
    {
        Http,
        Socks5
    }

    protected override void Initialize()
    {
        try
        {
            MaaFwVersion = MaaUtility.MaaVersion();
        }
        catch (Exception e)
        {
            MaaFwVersion = "v5.0.0";
            LoggerHelper.Error($"读取 MaaFramework 版本失败，已回退默认值：原因={e.Message}", e);
        }
        LanguageHelper.LanguageChanged += (_, _) => UpdateCdkExpireDisplay();
        ConfigurationManager.ConfigurationSwitched += OnConfigurationSwitched;
        RefreshDebugActionsVisibility();
        StopCountdownTimer();
        base.Initialize();
    }

    private void OnConfigurationSwitched(string _)
    {
        RefreshDebugActionsVisibility();
    }

    public void RefreshDebugActionsVisibility()
    {
        if (ConfigurationManager.Current.TryGetValue<bool>("Debug", out var debugValue))
        {
            ShowLocalPackageUpdate = debugValue;
            LoggerHelper.Info($"本地更新包按钮可见性已刷新：来源=config.Debug(bool)，值={debugValue}");
            return;
        }

        var rawValue = ConfigurationManager.Current.GetValue("Debug", string.Empty);
        ShowLocalPackageUpdate = bool.TryParse(rawValue, out var parsed) && parsed;
        LoggerHelper.Info($"本地更新包按钮可见性已刷新：来源=config.Debug(string)，原始值={rawValue}，解析结果={ShowLocalPackageUpdate}");
    }

    [ObservableProperty] private string _maaFwVersion = "";
    [ObservableProperty] private string _mfaVersion = RootViewModel.Version;
    [ObservableProperty] private string _resourceVersion = string.Empty;
    [ObservableProperty] private bool _showResourceVersion;
    [ObservableProperty] private long _cdkExpiredTime = 0;
    [ObservableProperty] private bool _cdkTextVisible = false;
    [ObservableProperty] private string _cdkExpireText = string.Empty;
    [ObservableProperty] private IBrush _cdkExpireColor = Brushes.MediumSeaGreen;
    private Timer? _countdownTimer;

    partial void OnCdkExpiredTimeChanged(long value)
    {
        UpdateCdkExpireDisplay();
    }

    private void UpdateCdkExpireDisplay()
    {
        if (CdkExpiredTime <= 0)
        {
            CdkExpireText = string.Empty;
            CdkTextVisible = false;
            StopCountdownTimer();
            return;
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var remaining = CdkExpiredTime - now;

        if (remaining <= 0)
        {
            CdkExpireText = LangKeys.MirrorCdkExpired.ToLocalization();
            CdkExpireColor = Brushes.Red;
            CdkTextVisible = true;
            StopCountdownTimer();
            return;
        }

        // 小于1天显示黄色
        if (remaining < 86400)
        {
            CdkExpireColor = Brushes.Orange;
            StartCountdownTimer();
        }
        else
        {
            CdkExpireColor = Brushes.MediumSeaGreen;
        }

        // 根据时间长短显示不同单位
        if (remaining >= 86400) // >= 1天
        {
            var days = remaining / 86400;
            CdkExpireText = string.Format(LangKeys.CdkExpireInDays.ToLocalization(), days);
        }
        else if (remaining >= 3600) // >= 1小时
        {
            var hours = remaining / 3600;
            CdkExpireText = string.Format(LangKeys.CdkExpireInHours.ToLocalization(), hours);
        }
        else if (remaining >= 120) // >= 2分钟
        {
            var minutes = remaining / 60;
            CdkExpireText = string.Format(LangKeys.CdkExpireInMinutes.ToLocalization(), minutes);
        }
        else // < 2分钟
        {
            CdkExpireText = string.Format(LangKeys.CdkExpireInSeconds.ToLocalization(), remaining);
        }
        CdkTextVisible = true;
    }
    
    private void StartCountdownTimer()
    {
        if (_countdownTimer != null)
            return; // 定时器已经在运行

        _countdownTimer = new Timer(_ =>
        {
            DispatcherHelper.RunOnMainThread(() =>
            {
                if (CdkExpiredTime <= 0)
                {
                    StopCountdownTimer();
                    return;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                var remaining = CdkExpiredTime - now;

                if (remaining <= 0)
                {
                    CdkExpireText = LangKeys.MirrorCdkExpired.ToLocalization();
                    CdkExpireColor = Brushes.Red;
                    CdkTextVisible = true;
                    StopCountdownTimer();
                }
                else if (remaining < 86400) // 仍然小于2分钟，继续倒计时
                {
                    CdkExpireText = string.Format(LangKeys.CdkExpireInSeconds.ToLocalization(), remaining);
                }
                else // 超过2分钟了，停止倒计时并更新显示
                {
                    UpdateCdkExpireDisplay();
                }
            });
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private void StopCountdownTimer()
    {
        if (_countdownTimer != null)
        {
            _countdownTimer.Dispose();
            _countdownTimer = null;
        }
    }

    partial void OnResourceVersionChanged(string value)
    {
        ShowResourceVersion = !string.IsNullOrWhiteSpace(value);
    }

    public ObservableCollection<LocalizationViewModel> DownloadSourceList =>
    [
        new()
        {
            Name = "GitHub"
        },
        new(LangKeys.MirrorChyan),
    ];

    [ObservableProperty] private int _downloadSourceIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.DownloadSourceIndex, 1);

    partial void OnDownloadSourceIndexChanged(int value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.DownloadSourceIndex, value);
    }

    public ObservableCollection<LocalizationViewModel> UIUpdateChannelList =>
    [
        new(LangKeys.AlphaVersion),
        new(LangKeys.BetaVersion),
        new(LangKeys.StableVersion),
    ];

    [ObservableProperty] private int _uIUpdateChannelIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.UIUpdateChannelIndex, 2);

    partial void OnUIUpdateChannelIndexChanged(int value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.UIUpdateChannelIndex, value);
    }

    public ObservableCollection<LocalizationViewModel> ResourceUpdateChannelList =>
    [
        new(LangKeys.AlphaVersion),
        new(LangKeys.BetaVersion),
        new(LangKeys.StableVersion),
    ];

    [ObservableProperty] private int _resourceUpdateChannelIndex = ConfigurationManager.Current.GetValue(ConfigurationKeys.ResourceUpdateChannelIndex, 2);

    partial void OnResourceUpdateChannelIndexChanged(int value) => HandlePropertyChanged(ConfigurationKeys.ResourceUpdateChannelIndex, value);

    [ObservableProperty] private string _gitHubToken = SimpleEncryptionHelper.Decrypt(ConfigurationManager.Current.GetValue(ConfigurationKeys.GitHubToken, string.Empty));

    partial void OnGitHubTokenChanged(string value) => HandlePropertyChanged(ConfigurationKeys.GitHubToken, SimpleEncryptionHelper.Encrypt(value));

    [ObservableProperty] private string _cdkPassword = SimpleEncryptionHelper.Decrypt(ConfigurationManager.Current.GetValue(ConfigurationKeys.DownloadCDK, string.Empty));

    partial void OnCdkPasswordChanged(string value) => HandlePropertyChanged(ConfigurationKeys.DownloadCDK, SimpleEncryptionHelper.Encrypt(value),(() =>
    {
        CdkExpiredTime = 0;
        CdkTextVisible = false;
    }));

    [ObservableProperty] private bool _enableCheckVersion = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableCheckVersion, true);

    [ObservableProperty] private bool _enableAutoUpdateResource = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableAutoUpdateResource, true);

    [ObservableProperty] private bool _enableAutoUpdateMFA = ConfigurationManager.Current.GetValue(ConfigurationKeys.EnableAutoUpdateMFA, false);

    partial void OnEnableCheckVersionChanged(bool value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.EnableCheckVersion, value);
    }

    partial void OnEnableAutoUpdateResourceChanged(bool value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.EnableAutoUpdateResource, value);
    }

    partial void OnEnableAutoUpdateMFAChanged(bool value)
    {
        ConfigurationManager.Current.SetValue(ConfigurationKeys.EnableAutoUpdateMFA, value);
    }
    [ObservableProperty] private string _proxyAddress = ConfigurationManager.Current.GetValue(ConfigurationKeys.ProxyAddress, string.Empty);
    [ObservableProperty] private UpdateProxyType _proxyType = ConfigurationManager.Current.GetValue(ConfigurationKeys.ProxyType, UpdateProxyType.Http, UpdateProxyType.Http, new UniversalEnumConverter<UpdateProxyType>());
    public ObservableCollection<LocalizationViewModel> ProxyTypeList =>
    [
        new("HTTP Proxy")
        {
            Other = UpdateProxyType.Http
        },
        new("SOCKS5 Proxy")
        {
            Other = UpdateProxyType.Socks5
        },
    ];

    partial void OnProxyAddressChanged(string value) => HandlePropertyChanged(ConfigurationKeys.ProxyAddress, value);

    partial void OnProxyTypeChanged(UpdateProxyType value) => HandlePropertyChanged(ConfigurationKeys.ProxyType, value.ToString());

    [RelayCommand]
    private void UpdateResource() => _ = ApplyGitHubUpdateAsync(force: false);

    [RelayCommand]
    private void RedownloadResource() => _ = ApplyGitHubUpdateAsync(force: true);

    [RelayCommand]
    private async Task UpdateResourceFromLocalPackage()
    {
        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        var result = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择本地更新包",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Archive")
                {
                    Patterns = ["*.zip", "*.7z", "*.tar", "*.gz", "*.tgz", "*.tar.gz"]
                }
            ]
        });

        if (result is { Count: > 0 } && result[0].TryGetLocalPath() is { } path)
        {
            VersionChecker.UpdateResourceFromLocalPackageAsync(path);
        }
    }

    [RelayCommand]
    private void CheckResourceUpdate() => _ = CheckGitHubUpdateAsync();

    [RelayCommand]
    private void UpdateMFA()
    {
        VersionChecker.UpdateMFAAsync();
    }
    [RelayCommand]
    private void CheckMFAUpdate()
    {
        VersionChecker.CheckMFAVersionAsync();
    }
    [RelayCommand]
    private void UpdateMaaFW()
    {
        VersionChecker.UpdateMaaFwAsync();
    }
    
    [RelayCommand]
    private void QueryCdkTime()
    {
        CdkExpiredTime = 0;
        CdkTextVisible = false;
        VersionChecker.CheckCDKAsync();
    }

    // ---- GitHub Releases 增量自更新（MaaBanGDream 便携包） ----

    [ObservableProperty] private string _gitHubUpdateStatus = "尚未检查更新";

    [ObservableProperty] private bool _hasGitHubUpdate;

    [ObservableProperty] private bool _isGitHubUpdating;

    [ObservableProperty] private bool _isApplyingGitHubUpdate;

    private GitHubReleaseUpdater? _gitHubUpdater;

    private GitHubReleaseUpdater.LatestRelease? _gitHubLatest;

    /// <summary>
    /// 启动时后台静默检查一次；结果只显示在设置页，不打断用户。
    /// 开启“自动更新资源”且应用空闲时，检查到新版本就自动整包更新。
    /// </summary>
    public void StartGitHubUpdateCheck()
    {
        if (!EnableCheckVersion && !EnableAutoUpdateResource)
        {
            return;
        }
        _ = StartupGitHubCheckAsync();
    }

    [RelayCommand]
    private async Task CheckGitHubUpdate()
    {
        IsGitHubUpdating = true;
        try
        {
            await CheckGitHubUpdateAsync();
        }
        finally
        {
            IsGitHubUpdating = false;
        }
    }

    private async Task CheckGitHubUpdateAsync()
    {
        var updater = GetUpdater();
        if (updater is null)
        {
            GitHubUpdateStatus = "无法确定安装目录，无法检查更新。";
            return;
        }

        try
        {
            var latest = await updater.CheckLatestAsync(CancellationToken.None);
            if (latest is null)
            {
                GitHubUpdateStatus = "无法连接 GitHub，请稍后再试。";
                return;
            }

            _gitHubLatest = latest;
            ResourceVersion = updater.LocalVersion ?? string.Empty;
            HasGitHubUpdate = GitHubReleaseUpdater.IsNewer(
                latest.Version,
                updater.LocalVersion);
            if (HasGitHubUpdate)
            {
                // 标题栏显示“发现新版本”按钮，点击即走整包更新流程。
                Instances.RootViewModel.WindowUpdateInfo =
                    $"发现新版本 v{latest.Version}，点击更新";
                Instances.RootViewModel.TempResourceUpdateAction =
                    () => _ = ApplyGitHubUpdateAsync(force: false);
            }
            else
            {
                Instances.RootViewModel.WindowUpdateInfo = string.Empty;
                Instances.RootViewModel.TempResourceUpdateAction = null;
            }
            GitHubUpdateStatus = HasGitHubUpdate
                ? $"发现新版本 v{latest.Version}（当前 {updater.LocalVersion}）"
                : $"已是最新版本 v{latest.Version}";
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"GitHub 更新检查失败：{ex.Message}", ex);
            GitHubUpdateStatus = "更新检查失败，请查看日志。";
        }
    }

    [RelayCommand]
    private Task ApplyGitHubUpdate() => ApplyGitHubUpdateAsync(force: false);

    private async Task StartupGitHubCheckAsync()
    {
        await CheckGitHubUpdateAsync();
        await CheckGitHubAnnouncementAsync();
        if (!EnableAutoUpdateResource || !HasGitHubUpdate)
        {
            return;
        }
        // 自动应用只在空闲时进行，避免打断正在运行的任务；繁忙时下次
        // 启动再检查。
        if (Instances.RootViewModel?.Idle != true)
        {
            GitHubUpdateStatus = "发现新版本，但当前正在执行任务，已推迟自动更新。";
            return;
        }
        await ApplyGitHubUpdateAsync(force: false);
    }

    /// <summary>
    /// 主页更新公告：读取最新 Release 的简介 Markdown，每个新 tag 只弹一次。
    /// </summary>
    private async Task CheckGitHubAnnouncementAsync()
    {
        try
        {
            var updater = GetUpdater();
            if (updater is null)
            {
                return;
            }
            var announcement = await updater.FetchLatestAnnouncementAsync(
                CancellationToken.None);
            if (announcement is null || string.IsNullOrWhiteSpace(announcement.Body))
            {
                return;
            }
            var lastTag = ConfigurationManager.Current.GetValue(
                ConfigurationKeys.GitHubAnnouncementLastTag,
                string.Empty) ?? string.Empty;
            if (string.Equals(lastTag, announcement.Tag, StringComparison.Ordinal))
            {
                return;
            }
            ConfigurationManager.Current.SetValue(
                ConfigurationKeys.GitHubAnnouncementLastTag,
                announcement.Tag);
            DispatcherHelper.RunOnMainThread(() =>
            {
                var viewModel = new ChangelogViewModel
                {
                    Type = AnnouncementType.Release,
                    AnnouncementInfo = announcement.Body,
                };
                new ChangelogView { DataContext = viewModel }.Show();
            });
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"获取 GitHub 更新公告失败：{ex.Message}", ex);
        }
    }

    private async Task ApplyGitHubUpdateAsync(bool force)
    {
        var updater = GetUpdater();
        if (updater is null)
        {
            GitHubUpdateStatus = "无法确定安装目录，无法更新。";
            return;
        }
        if (!force && (_gitHubLatest is null || !HasGitHubUpdate))
        {
            GitHubUpdateStatus = "当前没有可用的新版本。";
            return;
        }

        IsApplyingGitHubUpdate = true;
        try
        {
            var latest = _gitHubLatest;
            if (latest is null || force)
            {
                latest = await updater.CheckLatestAsync(CancellationToken.None);
                if (latest is null)
                {
                    GitHubUpdateStatus = "无法连接 GitHub，请稍后再试。";
                    return;
                }
                _gitHubLatest = latest;
                HasGitHubUpdate = GitHubReleaseUpdater.IsNewer(
                    latest.Version,
                    updater.LocalVersion);
            }

            var progress = new Progress<string>(
                text => DispatcherHelper.RunOnMainThread(() => GitHubUpdateStatus = text));
            var zipPath = await updater.DownloadAsync(
                latest,
                progress,
                CancellationToken.None);
            updater.ScheduleRestart(zipPath);
            if (Instances.RootViewModel?.Idle == true)
            {
                ToastHelper.Info("更新完成", "程序将自动重启以应用更新。");
                DispatcherHelper.RunOnMainThread(
                    () => Environment.Exit(0));
            }
            else
            {
                // 下载期间用户可能已开始任务：不强制退出，重启辅助脚本会
                // 在应用正常退出后完成覆盖解压并重启。
                GitHubUpdateStatus =
                    "更新包已下载完成；将在程序退出后自动应用并重启。";
                ToastHelper.Info(
                    "更新包已就绪",
                    "为避免打断正在执行的任务，将在程序退出后自动应用更新。");
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"GitHub 更新应用失败：{ex.Message}", ex);
            GitHubUpdateStatus = $"更新失败：{ex.Message}";
        }
        finally
        {
            IsApplyingGitHubUpdate = false;
        }
    }

    private GitHubReleaseUpdater? GetUpdater()
    {
        if (_gitHubUpdater is not null)
        {
            return _gitHubUpdater;
        }

        var baseDirectory = AppContext.BaseDirectory;
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return null;
        }

        _gitHubUpdater = new GitHubReleaseUpdater(baseDirectory);
        return _gitHubUpdater;
    }
}
