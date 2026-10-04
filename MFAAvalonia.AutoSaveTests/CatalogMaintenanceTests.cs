using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;

static class CatalogMaintenanceTests
{
    private static void Assert(bool value, string reason)
    {
        if (!value) throw new InvalidOperationException(reason);
    }

    public static async Task RunAsync()
    {
        await CoordinatorAsync();
        await SchedulingAsync();
        await RealChildShutdownAsync();
        AutoOptions();
        CacheFixtures();
        Console.WriteLine("Catalog scheduling, maintenance leases and isolated cache cleanup tests passed");
    }

    private static async Task CoordinatorAsync()
    {
        Assert(!TaskMaintenanceCoordinator.CheckQueuedTasks(() => false), "idle registry was treated as busy");
        Assert(TaskMaintenanceCoordinator.CheckQueuedTasks(() => throw new InvalidOperationException("collection changed")),
            "concurrent instance mutation did not fail closed as busy");
        var gate = new TaskMaintenanceCoordinator();
        using var first = await gate.BeginTaskAsync(default);
        var second = await gate.BeginTaskAsync(default);
        Assert(gate.TryBeginMaintenance(() => false) == null, "active multi-instance tasks allowed maintenance");
        first.Dispose();
        Assert(gate.TryBeginMaintenance(() => false) == null, "last active task did not protect catalog");
        second.Dispose();
        Assert(gate.TryBeginMaintenance(() => true) == null, "queued task did not block maintenance");
        var maintenance = gate.TryBeginMaintenance(() => false)!;
        Assert(gate.TryBeginMaintenance(() => false) == null, "two maintenance operations overlapped");
        using var cancel = new CancellationTokenSource();
        var waiting = gate.BeginTaskAsync(cancel.Token);
        Assert(!waiting.IsCompleted, "task entered during maintenance");
        cancel.Cancel();
        try { await waiting; throw new InvalidOperationException("task wait did not cancel"); }
        catch (OperationCanceledException) { }
        var resumed = gate.BeginTaskAsync(default);
        maintenance.Dispose();
        using var resumedLease = await resumed;
        Assert(gate.IsBusy, "released maintenance did not wake queued task");
        resumedLease.Dispose();
        Assert(!gate.IsBusy, "cancel/disposal leaked lease");
    }

    private static async Task SchedulingAsync()
    {
        var now = DateTimeOffset.Parse("2026-10-04T00:00:00Z");
        var plan = new BestdoriUpdatePlan(false, 24, now.AddDays(-2));
        var gate = new TaskMaintenanceCoordinator();
        var calls = 0;
        var fail = false;
        Func<Action<string>, CancellationToken, Task> sync = (_, token) =>
        {
            token.ThrowIfCancellationRequested();
            calls++;
            if (fail) throw new IOException("simulated network failure");
            plan = plan with { LastSuccess = now };
            return Task.CompletedTask;
        };
        var service = new BestdoriUpdateService(_ => Task.FromResult(plan), sync, gate, () => false, () => now);
        await service.CheckAsync();
        Assert(calls == 0 && service.Status.Contains("已关闭"), "disabled scheduler went online");
        plan = plan with { Enabled = true, LastSuccess = now };
        await service.CheckAsync();
        Assert(calls == 0 && service.Status.Contains("下次"), "not-due scheduler went online");
        now = now.AddHours(24);
        using (var task = await gate.BeginTaskAsync(default)) await service.CheckAsync();
        Assert(calls == 0 && service.Status.Contains("等待"), "busy scheduler went online");
        await service.CheckAsync();
        Assert(calls == 1 && !gate.IsBusy, "due scheduler failed to sync or release lease");
        plan = plan with { LastSuccess = now.AddDays(-2) };
        fail = true;
        await service.CheckAsync();
        await service.CheckAsync();
        now = now.AddMinutes(14);
        await service.CheckAsync();
        Assert(calls == 2 && service.Status.Contains("simulated network failure"), "failed sync retried before 15 minutes");
        now = now.AddMinutes(1);
        fail = false;
        await service.CheckAsync();
        Assert(calls == 3 && !gate.IsBusy, "backoff did not permit retry or leaked lease");
        var queued = new BestdoriUpdateService(_ => Task.FromResult(plan with { LastSuccess = null }), sync,
            gate, () => true, () => now);
        await queued.CheckAsync();
        Assert(calls == 3, "queued instance did not block automatic sync");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hanging = new BestdoriUpdateService(_ => Task.FromResult(plan with { LastSuccess = null }),
            async (_, token) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); },
            gate, () => false, () => now);
        var running = hanging.CheckAsync();
        await entered.Task;
        try { await hanging.RunManualAsync(_ => { }); throw new InvalidOperationException("manual overlapped auto"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("维护")) { }
        hanging.CancelAutomatic();
        await running;
        Assert(!gate.IsBusy && hanging.Status.Contains("取消"), "disable did not cancel its automatic sync");
        var manual = hanging.RunManualAsync(_ => { });
        hanging.CancelAutomatic();
        Assert(!manual.IsCompleted, "disabling auto canceled manual sync");
        hanging.Shutdown();
        try { await manual; throw new InvalidOperationException("shutdown did not cancel manual sync"); }
        catch (OperationCanceledException) { }
        Assert(!gate.IsBusy, "shutdown leaked manual lease");
        var shutdownAuto = new BestdoriUpdateService(_ => Task.FromResult(plan with { LastSuccess = null }),
            async (_, token) => await Task.Delay(Timeout.Infinite, token), gate, () => false);
        var auto = shutdownAuto.CheckAsync();
        shutdownAuto.Shutdown();
        await auto;
        Assert(!gate.IsBusy, "shutdown leaked automatic lease");
        var reading = new TaskCompletionSource<BestdoriUpdatePlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptedRead = new BestdoriUpdateService(_ => reading.Task, sync, gate, () => false, () => now);
        var delayedCheck = interruptedRead.CheckAsync();
        interruptedRead.CancelAutomatic();
        reading.SetResult(plan with { LastSuccess = null });
        await delayedCheck;
        Assert(calls == 3 && !gate.IsBusy, "disable during config read allowed automatic sync");
        await interruptedRead.CheckAsync();
        Assert(calls == 3, "stale saved true bypassed UI disable before auto-save finished");
        interruptedRead.SetAutomaticEnabled(true);
        await interruptedRead.CheckAsync();
        Assert(calls == 4, "re-enabling UI switch did not permit automatic sync again");
    }

    private static void AutoOptions()
    {
        var type = typeof(MFAAvalonia.ViewModels.UsersControls.Settings.PerformanceProfileSettingsUserControlModel);
        var model = new MFAAvalonia.ViewModels.UsersControls.Settings.PerformanceProfileSettingsUserControlModel();
        var load = type.GetMethod("LoadBestdoriAutoUpdateOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var capture = type.GetMethod("CaptureRuntimeOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        load.Invoke(model, [new JObject()]);
        Assert(model.BestdoriAutoUpdateEnabled && model.BestdoriAutoUpdateIntervalHours == 24, "missing auto settings did not use defaults");
        BestdoriUpdateService.Shared.SetAutomaticEnabled(false);
        load.Invoke(model, [new JObject()]);
        Assert(typeof(BestdoriUpdateService).GetField("_automaticAllowedByUi", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(BestdoriUpdateService.Shared) is true, "successful reload with unchanged property did not re-enable scheduler");
        load.Invoke(model, [new JObject { ["bestdori_auto_update_enabled"] = false, ["bestdori_auto_update_interval_hours"] = 720 }]);
        var saved = (JObject)capture.Invoke(model, null)!;
        Assert(!saved.Value<bool>("bestdori_auto_update_enabled") && saved.Value<int>("bestdori_auto_update_interval_hours") == 720, "auto update capture lost false/interval");
        foreach (var invalid in new JToken[] { 0, 721, true, 24.5, "24", JValue.CreateNull() })
        {
            try { load.Invoke(model, [new JObject { ["bestdori_auto_update_interval_hours"] = invalid }]); throw new Exception("invalid interval accepted"); }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
        }
        try { load.Invoke(model, [new JObject { ["bestdori_auto_update_enabled"] = "false" }]); throw new Exception("invalid flag accepted"); }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
        var pending = type.GetField("_pendingRuntimeOptions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        Assert(pending.GetValue(model) == null, "failed/unloaded settings queued default save");
    }

    private static async Task RealChildShutdownAsync()
    {
        var python = Environment.GetEnvironmentVariable("MAABANGDREAM_TEST_PYTHON");
        if (string.IsNullOrEmpty(python)) return;
        foreach (var manual in new[] { false, true })
        {
            var gate = new TaskMaintenanceCoordinator();
            var started = new TaskCompletionSource<System.Diagnostics.Process>(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new BestdoriUpdateService(_ => Task.FromResult(new BestdoriUpdatePlan(true, 24, null)),
                async (_, token) =>
                {
                    var info = new System.Diagnostics.ProcessStartInfo(python)
                    { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
                    info.ArgumentList.Add("-c");
                    info.ArgumentList.Add("import time; print('ready', flush=True); time.sleep(120)");
                    using var child = new CancelableChildProcess(info, token);
                    Assert(await child.Process.StandardOutput.ReadLineAsync(token).ConfigureAwait(false) == "ready", "slow child never started");
                    started.SetResult(child.Process);
                    await child.Process.WaitForExitAsync(child.Token).ConfigureAwait(false);
                    child.Token.ThrowIfCancellationRequested();
                }, gate, () => false);
            var running = manual ? service.RunManualAsync(_ => { }) : service.CheckAsync();
            var ownedProcess = await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var process = System.Diagnostics.Process.GetProcessById(ownedProcess.Id);
            Assert(!process.HasExited && gate.IsBusy, "slow child/maintenance lease not active");
            // 断言在 Shutdown 返回时成立，不给 UI continuation 机会替代同步取消回调。
            service.Shutdown();
            Assert(process.HasExited, "shutdown returned before actual Python child exited");
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (manual) { }
            Assert(!gate.IsBusy, "actual child shutdown leaked maintenance lease");
        }
        Console.WriteLine("Real slow Python auto/manual shutdown: child exited synchronously and maintenance lease released");
    }

    public static async Task GlobalShutdownAsync()
    {
        var python = Environment.GetEnvironmentVariable("MAABANGDREAM_TEST_PYTHON");
        var project = Environment.GetEnvironmentVariable("MAABANGDREAM_TEST_PROJECT_ROOT");
        if (string.IsNullOrEmpty(python) || string.IsNullOrEmpty(project)) return;
        var root = Path.Combine(project, ".local", "mfa-global-shutdown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var configPath = Path.Combine(AppContext.BaseDirectory, "profile-manager.json");
        var original = File.Exists(configPath) ? await File.ReadAllBytesAsync(configPath) : null;
        var originalWorkingDirectory = Environment.CurrentDirectory;
        var syncReady = Path.Combine(root, "sync.pid");
        var optionsReady = Path.Combine(root, "options.pid");
        var code = "import pathlib,os,time,sys; pathlib.Path(sys.argv[1]).write_text(str(os.getpid())); time.sleep(120)";
        try
        {
            // MaaFramework 的首次静态初始化按工作目录写配置和日志，限定到隔离夹。
            Environment.CurrentDirectory = root;
            await File.WriteAllTextAsync(configPath, new JObject
            {
                ["child_exec"] = python, ["child_args"] = new JArray("-c", code, optionsReady),
                ["chart_sync"] = new JObject { ["child_exec"] = python, ["child_args"] = new JArray("-c", code, syncReady) }
            }.ToString());
            var client = typeof(MFAAvalonia.ViewModels.UsersControls.Settings.PerformanceProfileSettingsUserControlModel)
                .Assembly.GetType("MFAAvalonia.ViewModels.UsersControls.Settings.ProfileManagerClient")!;
            var options = (Task<JObject>)client.GetMethod("InvokeAsync")!.Invoke(null,
                [new JObject { ["operation"] = "runtime-options" }, CancellationToken.None])!;
            var sync = BestdoriUpdateService.Shared.RunManualAsync(_ => { });
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(syncReady) || !File.Exists(optionsReady))
            {
                if (DateTime.UtcNow > deadline) throw new Exception("production children did not start");
                await Task.Delay(10);
            }
            using var syncProcess = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(syncReady)));
            using var optionsProcess = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(optionsReady)));
            Assert(TaskMaintenanceCoordinator.Shared.IsBusy, "production sync did not acquire lease");
            BestdoriUpdateService.Shared.Shutdown();
            Assert(syncProcess.HasExited && optionsProcess.HasExited, "global shutdown left sync/runtime-options Python child alive");
            foreach (var running in new Task[] { sync, options })
            {
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("shutdown was reported as success"); }
                catch (OperationCanceledException) { }
            }
            Assert(!TaskMaintenanceCoordinator.Shared.IsBusy, "global production shutdown leaked maintenance lease");
            Console.WriteLine("Production global shutdown: sync/runtime-options children exited and lease released");
        }
        finally
        {
            BestdoriUpdateService.Shared.Shutdown();
            Environment.CurrentDirectory = originalWorkingDirectory;
            if (original != null) await File.WriteAllBytesAsync(configPath, original);
            else File.Delete(configPath);
        }
    }

    private static void CacheFixtures()
    {
        var root = Environment.GetEnvironmentVariable("MAABANGDREAM_TEST_ROOT")
                   ?? Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        root = Path.GetFullPath(root);
        var data = Path.Combine(root, "data");
        var source = Path.Combine(root, "source");
        var debug = Path.Combine(data, "debug");
        Directory.CreateDirectory(Path.Combine(debug, "nested"));
        Directory.CreateDirectory(Path.Combine(data, "profiles"));
        Directory.CreateDirectory(Path.Combine(source, "debug", "recordings"));
        var profile = Path.Combine(data, "profiles", "keep.json");
        File.WriteAllText(profile, "preserve");
        File.WriteAllText(Path.Combine(debug, "ordinary.txt"), "123");
        File.WriteAllText(Path.Combine(debug, "nested", "ordinary.txt"), "12345");
        File.WriteAllText(Path.Combine(source, "debug", "recordings", "recording.json"), "1234");
        var locked = Path.Combine(debug, "locked.log");
        File.WriteAllText(locked, "locked");
        using var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var paths = new JObject { ["realtime_recordings"] = Path.Combine(source, "debug", "recordings"),
            ["maafw_debug"] = Path.Combine(source, "debug"), ["profiles"] = Path.Combine(data, "profiles") };
        var planned = CacheMaintenance.Plan(data, paths);
        Assert(!planned.Contains(Path.Combine(source, "debug", "recordings")), "parent/child was not deduplicated");
        var gate = new TaskMaintenanceCoordinator();
        var result = CacheMaintenance.Clean(data, paths, gate, () => false);
        Assert(result.DeletedFiles == 3 && result.DeletedBytes == 12, "deletion count/bytes are not truthful");
        Assert(result.Failures.Count == 1 && result.Failures[0].Path == locked, "locked file failure was hidden");
        Assert(File.Exists(locked) && File.ReadAllText(profile) == "preserve", "locked/profile file damaged");
        Assert(result.Describe().Contains("未清理"), "partial cleanup falsely reported success");
        var empty = CacheMaintenance.Clean(Path.Combine(root, "empty"), null, gate, () => false);
        Assert(empty.DeletedFiles == 0 && empty.Describe().Contains("没有可清理"), "empty cleanup falsely reported deletion");
        using var maintenance = gate.TryBeginMaintenance(() => false)!;
        try { CacheMaintenance.Clean(data, null, gate, () => false); throw new Exception("sync did not block cache"); }
        catch (InvalidOperationException) { }
        foreach (var target in new[] { Path.GetPathRoot(root)!, source, Path.Combine(data, "profiles", "debug"),
                     Path.Combine(data, "config", "debug"), Path.Combine(data, "charts", "debug"),
                     Path.Combine(data, "debug", "..", "debug") })
        {
            try { CacheMaintenance.Plan(data, new JObject { ["maafw_debug"] = target }); throw new Exception("unsafe target accepted"); }
            catch (InvalidDataException) { }
        }
        Assert(File.ReadAllText(profile) == "preserve", "unsafe planner touched files");
        if (OperatingSystem.IsWindows())
        {
            var outside = Path.Combine(root, "outside-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(outside, "debug"));
            var protectedFile = Path.Combine(outside, "debug", "keep.txt");
            File.WriteAllText(protectedFile, "must survive");
            var link = Path.Combine(root, "link-" + Guid.NewGuid().ToString("N"));
            var info = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "/c", "mklink", "/J", link, outside }) info.ArgumentList.Add(arg);
            using var process = System.Diagnostics.Process.Start(info)!;
            process.WaitForExit();
            Assert(process.ExitCode == 0, "isolated junction fixture creation failed");
            try { CacheMaintenance.Plan(data, new JObject { ["maafw_debug"] = Path.Combine(link, "debug") }); throw new Exception("linked ancestor was accepted"); }
            catch (IOException) { }
            Assert(File.ReadAllText(protectedFile) == "must survive", "junction planner deleted outside file");
        }
    }
}
