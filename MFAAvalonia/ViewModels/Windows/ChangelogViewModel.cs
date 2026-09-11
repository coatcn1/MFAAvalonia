using CommunityToolkit.Mvvm.ComponentModel;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.Helper.ValueType;
using MFAAvalonia.Views.Windows;
using System;
using System.IO;

namespace MFAAvalonia.ViewModels.Windows;

public partial class ChangelogViewModel : ViewModelBase
{
    public static readonly string ChangelogFileName = "Changelog.md";
    public static readonly string ReleaseFileName = "Release.md";
    [ObservableProperty] private string _announcementInfo = string.Empty;

    [ObservableProperty] private bool _doNotRemindThisChangelogAgain = Convert.ToBoolean(GlobalConfiguration.GetValue(ConfigurationKeys.DoNotShowChangelogAgain, bool.FalseString));
    partial void OnDoNotRemindThisChangelogAgainChanged(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.DoNotShowChangelogAgain, value.ToString());
    }


    [ObservableProperty] private AnnouncementType _type = AnnouncementType.Changelog;

    public static bool CheckReleaseNote()
    {
        var content = string.Empty;
        try
        {
            var resourcePath = AppPaths.ResourceDirectory;
            var mdPath = Path.Combine(resourcePath, ReleaseFileName);

            
            if (File.Exists(mdPath))
            {
                content = File.ReadAllText(mdPath);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"读取Release Note文件失败: {ex.Message}");
            content = string.Empty;
        }
        return ShowReleaseContent(content);
    }

    /// <summary>
    /// 统一复用发布说明窗口，供更新检查与 About 手动查看使用。
    /// </summary>
    public static bool ShowReleaseContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Trim().Equals("placeholder", StringComparison.OrdinalIgnoreCase))
            return false;

        var announcementView = new ChangelogView
        {
            DataContext = new ChangelogViewModel
            {
                Type = AnnouncementType.Release,
                AnnouncementInfo = content,
            }
        };
        announcementView.Show();
        return true;
    }

    public static bool CheckChangelog()
    {
        var content = string.Empty;
        try
        {
            var resourcePath = AppPaths.ResourceDirectory;
            var mdPath = Path.Combine(resourcePath, ChangelogFileName);

            if (File.Exists(mdPath))
            {
                content = File.ReadAllText(mdPath);
            }
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"读取公告文件失败: {ex.Message}");
            content = string.Empty;
        }
        return ShowChangelogContent(content);
    }

    /// <summary>
    /// 复用更新完成公告窗口，并保留用户“不再提醒”的选择。
    /// </summary>
    public static bool ShowChangelogContent(string? content)
    {
        var viewModel = new ChangelogViewModel
        {
            Type = AnnouncementType.Changelog,
        };
        if (viewModel.DoNotRemindThisChangelogAgain
            || string.IsNullOrWhiteSpace(content)
            || content.Trim().Equals("placeholder", StringComparison.OrdinalIgnoreCase))
            return false;

        viewModel.AnnouncementInfo = content;
        new ChangelogView { DataContext = viewModel }.Show();
        return true;
    }
}
