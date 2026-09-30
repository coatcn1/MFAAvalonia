using Avalonia.Controls;
using System;

namespace MFAAvalonia.Helper;

internal static class DocumentWindowHost
{
    private static Window? ActiveWindow;

    internal static bool Show(Window window)
    {
        // 公告和版本说明共用一个阅读窗口入口，避免手动查看与启动检测叠出多个窗口。
        if (ActiveWindow is { IsVisible: true })
        {
            ActiveWindow.Activate();
            window.Close();
            return false;
        }
        ActiveWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(ActiveWindow, window)) ActiveWindow = null; };
        if (Instances.RootView is { IsVisible: true } owner)
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            window.ShowInTaskbar = false;
            _ = window.ShowDialog(owner);
        }
        else
            window.Show();
        return true;
    }
}
