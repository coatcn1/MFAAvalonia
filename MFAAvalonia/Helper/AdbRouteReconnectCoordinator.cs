using System;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

public readonly record struct AdbRouteState(bool HasMainTasker, bool Busy, string? ActiveRoute);

public sealed class AdbRouteReconnectCoordinator
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);

    public static bool RoutineBlocksConnection(bool manual, bool routineActive, bool inputStarted) =>
        routineActive && (manual || inputStarted);

    public async Task<T> AcquireAsync<T>(bool guarded, Func<string> verifyRoute,
        Func<AdbRouteState> readState, Action discardIdleMain, Func<Task<T>> acquire,
        Action markDisconnected, CancellationToken token)
    {
        await _connectionGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            string? verifiedBefore = null;
            if (guarded)
            {
                // 先核验 owner，再决定是否丢弃；未知 owner 不能破坏现存控制器。
                var verified = verifyRoute();
                verifiedBefore = verified;
                var before = readState();
                if (before.Busy)
                    throw new AdbTargetMismatchException("主任务仍在运行或停止中，已阻止连接操作。请先停止任务。");
                if (before.HasMainTasker && !string.Equals(before.ActiveRoute, verified, StringComparison.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (readState().Busy)
                        throw new AdbTargetMismatchException("主控制器路由已变化，但任务仍在运行或停止中，已阻止重连。请先停止任务。");
                    discardIdleMain();
                }
            }

            token.ThrowIfCancellationRequested();
            var result = await acquire();
            token.ThrowIfCancellationRequested();
            if (guarded)
            {
                var verified = verifyRoute();
                var after = readState();
                // 最多重建一次。连接期间再变路由时直接失败，不能循环换目标。
                if (!after.HasMainTasker
                    || !string.Equals(verifiedBefore, verified, StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(after.ActiveRoute, verified, StringComparison.OrdinalIgnoreCase))
                    throw new AdbTargetMismatchException("重连后的实际主控制器路由未知或再次变化，已阻止继续使用。请重新连接设备。");
            }
            return result;
        }
        catch
        {
            markDisconnected();
            throw;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public T ReadSnapshot<T>(Func<T> read, CancellationToken token)
    {
        // 截图初始化和任务入队与重建共用入口，防止旧初始化任务回填到新主连接。
        if (!_connectionGate.Wait(0, token))
            throw new AdbTargetMismatchException("主连接正在初始化或重建，暂不允许截图或输入。请等待连接完成。");
        try
        {
            token.ThrowIfCancellationRequested();
            return read();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public bool Refresh(bool guarded, Func<string> verifyRoute, Func<AdbRouteState> readState,
        Func<bool> link, Action<bool> publishConnected, CancellationToken token)
    {
        try
        {
            return ReadSnapshot(() =>
            {
                string? verified = null;
                if (guarded)
                {
                    verified = verifyRoute();
                    var before = readState();
                    if (!before.HasMainTasker || before.Busy
                        || !string.Equals(before.ActiveRoute, verified, StringComparison.OrdinalIgnoreCase))
                        throw new AdbTargetMismatchException("刷新前实际主路由未知、变化或正在运行任务，已阻止刷新连接。");
                }
                token.ThrowIfCancellationRequested();
                var connected = link();
                token.ThrowIfCancellationRequested();
                if (guarded)
                {
                    var after = readState();
                    if (!after.HasMainTasker || !string.Equals(verified, verifyRoute(), StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(after.ActiveRoute, verified, StringComparison.OrdinalIgnoreCase))
                        throw new AdbTargetMismatchException("刷新期间主路由再次变化，已阻止将界面标记为已连接。");
                }
                publishConnected(connected);
                return connected;
            }, token);
        }
        catch
        {
            publishConnected(false);
            throw;
        }
    }
}
