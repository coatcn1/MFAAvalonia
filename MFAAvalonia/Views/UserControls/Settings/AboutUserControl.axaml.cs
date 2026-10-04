using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaEdit.Highlighting;
using MFAAvalonia.Extensions;
using MFAAvalonia.Extensions.MaaFW;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.Windows;
using MFAAvalonia.Views.Windows;
using Avalonia.VisualTree;
using MFAAvalonia.Views.Mobile;
using System;
using System.IO;
using System.Linq;

namespace MFAAvalonia.Views.UserControls.Settings;

public partial class AboutUserControl : UserControl
{
    public AboutUserControl()
    {
        InitializeComponent();

    }
    private async void Button_OnClick(object? sender, RoutedEventArgs e)
    {
        var storageProvider = Instances.StorageProvider;
        if (storageProvider == null)
        {
            ToastHelper.Warn(LangKeys.Warning.ToLocalization(), LangKeys.PlatformNotSupportedOperation.ToLocalization());
            return;
        }

        await FileLogExporter.CompressRecentLogs(storageProvider);
    }
    
    private async void DisplayAnnouncement(object? sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(MaaProcessor.Interface?.Welcome))
            await AnnouncementViewModel.AddAnnouncementAsync(MaaProcessor.Interface.Welcome, projectDir: AppPaths.DataRoot);
        await AnnouncementViewModel.CheckAnnouncement(forceShow: true);
    }

    private void DisplayReleaseNotes(object? sender, RoutedEventArgs e)
    {
        if (!ChangelogViewModel.CheckReleaseNote())
        {
            LoggerHelper.Warning("当前安装版本没有可用的本地发布说明。");
            ChangelogViewModel.ShowReleaseContent("# 暂无本地发布说明\n\n当前安装版本没有携带发布说明文件。");
        }
    }
    
    private async void ClearCache_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var artifacts = await ViewModels.UsersControls.Settings.ProfileManagerClient.LoadCacheArtifactPathsAsync();
            // 不终止其他实例，也不强制释放外部文件锁；任务与维护共用同一门禁。
            var result = CacheMaintenance.Clean(AppPaths.DataRoot, artifacts,
                TaskMaintenanceCoordinator.Shared, () => TaskMaintenanceCoordinator.CheckQueuedTasks(() => MaaProcessor.Processors.Any(p => p.TaskQueue.Count > 0)));
            foreach (var failure in result.Failures)
                LoggerHelper.Warning($"缓存未清理：{failure.Path}，{failure.Reason}");
            LoggerHelper.Info($"清理缓存：{result.Describe()}");
            if (result.Failures.Count > 0 || result.DeletedFiles == 0)
                ToastHelper.Warn("缓存清理结果", result.Describe());
            else ToastHelper.Success(result.Describe());
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"清理缓存失败: {ex.Message}");
            ToastHelper.Error(LangKeys.ClearCacheFailed.ToLocalization(), ex.Message);
        }
    }
    
    private void ShowLicense_Click(object? sender, RoutedEventArgs e)
    {
        var viewModel = DataContext as ViewModels.Pages.SettingsViewModel;
        if (viewModel != null && !string.IsNullOrEmpty(viewModel.ResourceLicense))
        {
            LicenseView.ShowLicense(viewModel.ResourceLicense);
        }
    }

    private void StartTutorial_Click(object? sender, RoutedEventArgs e)
    {
        var rootContent = this.GetVisualAncestors().OfType<RootViewContent>().FirstOrDefault();
        rootContent?.TryStartTutorial();
    }
}

