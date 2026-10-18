using MFAAvalonia.Helper;

static class AdbRouteReconnectTests
{
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task RunAsync()
    {
        foreach (var (oldRoute, selectedRoute) in new[] { ("emulator-7554", "127.0.0.2:7555"), ("127.0.0.2:7555", "emulator-7554"), ((string?)null, "emulator-7554") })
        {
            var coordinator = new AdbRouteReconnectCoordinator();
            var exists = true;
            var active = oldRoute;
            var screenshot = "old";
            var rebuilt = 0;
            var result = await coordinator.AcquireAsync(true, () => selectedRoute,
                () => new AdbRouteState(exists, false, active),
                () => { exists = false; active = null; screenshot = "detached"; },
                () => { Check(!exists && screenshot == "detached", "stale main/screenshot reused"); exists = true; active = selectedRoute; rebuilt++; return Task.FromResult(42); },
                () => { }, CancellationToken.None);
            Check(result == 42 && rebuilt == 1 && active == selectedRoute, "route not rebuilt exactly once");
        }

        var same = new AdbRouteReconnectCoordinator();
        var discarded = false;
        await same.AcquireAsync(true, () => "alias", () => new AdbRouteState(true, false, "alias"),
            () => discarded = true, () => Task.FromResult(1), () => { }, CancellationToken.None);
        Check(!discarded, "same route discarded");

        foreach (var busy in new[] { true, false })
        {
            var blocked = false;
            discarded = false;
            try
            {
                await new AdbRouteReconnectCoordinator().AcquireAsync(true,
                    () => busy ? "alias" : throw new AdbTargetMismatchException("unknown owner"),
                    () => new AdbRouteState(true, busy, "original"),
                    () => discarded = true, () => Task.FromResult(1), () => { }, CancellationToken.None);
            }
            catch (AdbTargetMismatchException) { blocked = true; }
            Check(blocked && !discarded, "busy/unknown-owner controller was disturbed");
        }

        var refreshConnected = true;
        try
        {
            await new AdbRouteReconnectCoordinator().AcquireAsync(true, () => "alias",
                () => new AdbRouteState(false, false, null), () => { },
                () => Task.FromResult(1), () => refreshConnected = false, CancellationToken.None);
            throw new InvalidOperationException("unknown actual rebuilt route accepted");
        }
        catch (AdbTargetMismatchException) { Check(!refreshConnected, "failed rebuild kept connected UI"); }

        var verifies = 0;
        await new AdbRouteReconnectCoordinator().AcquireAsync(false, () => { verifies++; return "unused"; },
            () => new AdbRouteState(true, false, null), () => throw new Exception("guard-off discard"),
            () => Task.FromResult(1), () => { }, CancellationToken.None);
        Check(verifies == 0, "guard-off/non-LD path changed");
        Check(!AdbRouteReconnectCoordinator.RoutineBlocksConnection(false, true, false),
            "internal initial connection blocked by routine queue");
        Check(AdbRouteReconnectCoordinator.RoutineBlocksConnection(true, true, false)
            && AdbRouteReconnectCoordinator.RoutineBlocksConnection(false, true, true)
            && !AdbRouteReconnectCoordinator.RoutineBlocksConnection(true, false, true),
            "manual/routine input-gap boundary changed");
        var routineBusy = AdbRouteReconnectCoordinator.RoutineBlocksConnection(false, true, true);
        var touchedBusyMain = false;
        try
        {
            await new AdbRouteReconnectCoordinator().AcquireAsync(true, () => "alias",
                () => new AdbRouteState(true, routineBusy, "original"),
                () => touchedBusyMain = true, () => Task.FromResult(1), () => { }, CancellationToken.None);
            throw new InvalidOperationException("routine native-job gap permitted route rebuild");
        }
        catch (AdbTargetMismatchException) { Check(!touchedBusyMain, "routine main was discarded"); }

        var refreshLinks = 0;
        try
        {
            new AdbRouteReconnectCoordinator().Refresh(true, () => "alias",
                () => new AdbRouteState(true, true, "alias"),
                () => { refreshLinks++; return true; }, _ => { }, CancellationToken.None);
            throw new InvalidOperationException("busy refresh accepted");
        }
        catch (AdbTargetMismatchException) { Check(refreshLinks == 0, "busy refresh reached LinkStart"); }

        var route = "alias";
        var activeRoute = "original";
        var uiConnected = false;
        var lifecycle = new AdbRouteReconnectCoordinator();
        var mainExists = true;
        var rebuildCount = 0;
        await lifecycle.AcquireAsync(true, () => route,
            () => new AdbRouteState(mainExists, false, activeRoute),
            () => { mainExists = false; activeRoute = null!; },
            () => { mainExists = true; activeRoute = route; rebuildCount++; return Task.FromResult(1); },
            () => uiConnected = false, CancellationToken.None);
        Check(lifecycle.Refresh(true, () => route, () => new AdbRouteState(mainExists, false, activeRoute),
            () => true, connected => uiConnected = connected, CancellationToken.None) && uiConnected,
            "refresh must publish only verified new route");
        await lifecycle.AcquireAsync(true, () => route, () => new AdbRouteState(mainExists, false, activeRoute),
            () => throw new Exception("start discarded refreshed route"), () => Task.FromResult(1),
            () => uiConnected = false, CancellationToken.None);
        Check(rebuildCount == 1, "refresh to start did not reuse rebuilt route");
        try
        {
            lifecycle.Refresh(true, () => route, () => new AdbRouteState(true, false, activeRoute),
                () => { route = "original"; return true; }, connected => uiConnected = connected, CancellationToken.None);
            throw new InvalidOperationException("refresh accepted route change during LinkStart");
        }
        catch (AdbTargetMismatchException) { Check(!uiConnected, "refresh published stale connected state"); }

        var initial = true;
        try
        {
            await lifecycle.AcquireAsync(true, () => initial ? "alias" : "original",
                () => new AdbRouteState(true, false, "alias"), () => { },
                () => { initial = false; return Task.FromResult(1); }, () => { }, CancellationToken.None);
            throw new InvalidOperationException("second route change was accepted");
        }
        catch (AdbTargetMismatchException) { }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = lifecycle.AcquireAsync(false, () => "unused", () => default,
            () => { }, async () => { entered.SetResult(); await release.Task; return 1; }, () => { }, CancellationToken.None);
        await entered.Task;
        try
        {
            lifecycle.ReadSnapshot<int>(() => throw new InvalidOperationException("snapshot bypassed pending rebuild"), CancellationToken.None);
            throw new InvalidOperationException("snapshot was not blocked");
        }
        catch (AdbTargetMismatchException) { }
        finally { release.SetResult(); }
        await pending;

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        try
        {
            await lifecycle.AcquireAsync(true, () => throw new Exception("verification after stop"), () => default,
                () => throw new Exception("discard after stop"), () => Task.FromResult(1), () => { }, canceled.Token);
            throw new InvalidOperationException("stop ignored");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("ADB route reconnect coordinator tests passed");
    }
}
