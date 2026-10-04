using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace MFAAvalonia.Helper;

public sealed class CancelableChildProcess : IDisposable
{
    private static readonly HashSet<Process> Children = new();
    private readonly CancellationTokenSource _linked;
    private readonly CancellationTokenRegistration _registration;
    public Process Process { get; }
    public CancellationToken Token => _linked.Token;

    public CancelableChildProcess(ProcessStartInfo startInfo, CancellationToken token)
    {
        _linked = CancellationTokenSource.CreateLinkedTokenSource(token, BestdoriUpdateService.Shared.ShutdownToken);
        _linked.Token.ThrowIfCancellationRequested();
        Process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException("无法启动子进程");
        lock (Children) Children.Add(Process);
        // 退出时 UI 消息循环可能已停止，直接在取消回调中终止进程，不能依赖 await 的后续执行。
        _registration = _linked.Token.Register(() => Terminate(Process));
    }

    public static void Terminate(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            if (!process.WaitForExit(3000)) LoggerHelper.Warning("取消后子进程仍未在 3 秒内退出");
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception ex) { LoggerHelper.Warning($"终止子进程失败：{ex.Message}"); }
    }

    public void Dispose()
    {
        _registration.Dispose();
        Terminate(Process);
        lock (Children) Children.Remove(Process);
        Process.Dispose();
        _linked.Dispose();
    }
}
