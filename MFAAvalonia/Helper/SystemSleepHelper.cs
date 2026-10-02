using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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

    private static readonly HashSet<TaskExecutionScope> ActiveTaskExecutions = new();
    private static bool _isShuttingDown;

    public static bool IsPreventingSleep { get; private set; }

    public static bool GetPreventSleepSetting()
    {
        // 开关偏好在配置间共享；旧版配置只作首次迁移的回退来源。
        var global = GlobalConfiguration.GetValue(ConfigurationKeys.PreventSleep);
        return bool.TryParse(global, out var value)
            ? value
            : ConfigurationManager.Current.GetValue(ConfigurationKeys.PreventSleep, false);
    }

    public static void SavePreventSleepSetting(bool value)
    {
        GlobalConfiguration.SetValue(ConfigurationKeys.PreventSleep, value ? "true" : "false");
        ApplyPreventSleep();
    }

    public static async Task<IDisposable> BeginTaskExecutionAsync()
    {
        var scope = new TaskExecutionScope();
        if (!OperatingSystem.IsWindows())
            return scope;

        // 在任务开始前完成申请；多个配置各自结束时不能释放其他任务的保护。
        await DispatcherHelper.RunOnMainThreadAsync(() =>
        {
            if (_isShuttingDown)
                return;
            ActiveTaskExecutions.Add(scope);
            ApplyPreventSleep();
        });
        return scope;
    }

    private static void EndTaskExecution(TaskExecutionScope scope)
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => EndTaskExecution(scope));
            return;
        }

        if (ActiveTaskExecutions.Remove(scope))
            ApplyPreventSleep();
    }

    public static void Shutdown()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Shutdown);
            return;
        }

        // 退出后忽略迟到的任务回调，避免清理期间重新申请；不清除已保存的偏好。
        _isShuttingDown = true;
        ActiveTaskExecutions.Clear();
        SetPreventSleepState(false);
    }

    public static void ApplyPreventSleep()
    {
        if (!OperatingSystem.IsWindows())
            return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            // 在 UI 线程读取最新偏好和任务状态，不执行过时的开关快照。
            Dispatcher.UIThread.Post(ApplyPreventSleep);
            return;
        }

        try
        {
            SetPreventSleepState(!_isShuttingDown && ActiveTaskExecutions.Count > 0
                && GetPreventSleepSetting());
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"应用防休眠设置失败：原因={ex.Message}", ex);
        }
    }

    private static void SetPreventSleepState(bool prevent)
    {
        // 执行状态绑定调用线程；所有调用只从 UI 线程进入。
        if (prevent == IsPreventingSleep)
            return;

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
            LoggerHelper.Info(prevent ? "已启用任务运行时防息屏和防休眠。" : "已释放防息屏和防休眠请求。");
        }
        catch (Exception ex)
        {
            LoggerHelper.Error($"设置系统执行状态失败：原因={ex.Message}", ex);
        }
    }

    private sealed class TaskExecutionScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                EndTaskExecution(this);
        }
    }
}
