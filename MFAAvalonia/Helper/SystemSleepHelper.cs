using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Avalonia.Threading;
using MFAAvalonia.Configuration;

namespace MFAAvalonia.Helper;

public static class SystemSleepHelper
{
    [Flags]
    private enum ExecutionState : uint
    {
        ES_CONTINUOUS = 0x80000000,
        ES_DISPLAY_REQUIRED = 0x00000002,
        ES_SYSTEM_REQUIRED = 0x00000001
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    public static bool IsPreventingSleep { get; private set; }

    public static bool GetPreventSleepSetting()
    {
        // 防息屏属于整个应用；旧版配置只作首次迁移的回退来源。
        var global = GlobalConfiguration.GetValue(ConfigurationKeys.PreventSleep);
        return bool.TryParse(global, out var value)
            ? value
            : ConfigurationManager.Current.GetValue(ConfigurationKeys.PreventSleep, false);
    }

    public static void SavePreventSleepSetting(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.PreventSleep, value ? "true" : "false");
        ApplyPreventSleep(value);
    }

    public static void ApplyPreventSleep()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            ApplyPreventSleep(GetPreventSleepSetting());
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"应用防休眠设置失败：原因={ex.Message}", ex);
        }
    }

    public static void ApplyPreventSleep(bool prevent)
    {
        if (!OperatingSystem.IsWindows())
            return;

        // 执行状态绑定调用线程，启用、关闭和退出必须始终落在 UI 线程。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyPreventSleep(prevent));
            return;
        }

        try
        {
            var flags = ExecutionState.ES_CONTINUOUS;
            if (prevent)
                flags |= ExecutionState.ES_SYSTEM_REQUIRED | ExecutionState.ES_DISPLAY_REQUIRED;
            if (SetThreadExecutionState(flags) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            IsPreventingSleep = prevent;
            LoggerHelper.Info(prevent ? "已启用运行时防息屏和防休眠。" : "已释放防息屏和防休眠请求。");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"设置系统执行状态失败：原因={ex.Message}", ex);
        }
    }
}
