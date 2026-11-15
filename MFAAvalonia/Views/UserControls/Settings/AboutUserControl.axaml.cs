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
    private bool _clearingCache;

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
        if (_clearingCache) return;
        _clearingCache = true;
        var button = sender as Button;
        var window = TopLevel.GetTopLevel(this) as Window;
        var windowClosed = false;
        if (window != null) window.Closed += OnWindowClosed;
        try
        {
            if (button != null) button.IsEnabled = false;
            LoggerHelper.Info("开始清理缓存");
            // 不终止其他实例，也不强制释放外部文件锁；任务与维护共用同一门禁。
            var result = await CacheMaintenance.CleanAsync(AppPaths.DataRoot,
                ViewModels.UsersControls.Settings.ProfileManagerClient.LoadCacheArtifactPathsAsync,
                TaskMaintenanceCoordinator.Shared,
                () => TaskMaintenanceCoordinator.CheckQueuedTasks(() => MaaProcessor.Processors.Any(p => p.TaskQueue.Count > 0)));
            foreach (var failure in result.Failures)
                LoggerHelper.Warning($"缓存未清理：{failure.Path}，{failure.Reason}");
            var description = result.Describe();
            LoggerHelper.Info($"清理缓存：{description}");
            if (windowClosed) return;
            if (result.Failures.Count > 0 || result.DeletedFiles == 0)
                ToastHelper.Warn("缓存清理结果", description);
            else ToastHelper.Success(description);
        }
        catch (Exception ex)
        {
            LoggerHelper.Error("清理缓存失败", ex);
            if (!windowClosed) ToastHelper.Error(LangKeys.ClearCacheFailed.ToLocalization(), ex.Message);
        }
        finally
        {
            if (window != null) window.Closed -= OnWindowClosed;
            if (button != null && !windowClosed) button.IsEnabled = true;
            _clearingCache = false;
        }

        void OnWindowClosed(object? sender, EventArgs args) => windowClosed = true;
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

