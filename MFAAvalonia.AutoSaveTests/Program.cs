using Avalonia;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using System.Reflection;
using System.Net;
using System.Net.Sockets;
using System.Text;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void AdbEndpointGuardKeepsLdInputOnSelectedProcess()
{
    const string config = "{\"extras\":{\"ld\":{\"enable\":true,\"pid\":41}}}";
    Assert(AdbEndpointIdentityGuard.TryGetLdTarget("emulator-9998", config, out var port, out var pid)
        && port == 9999 && pid == 41, "LD endpoint was not parsed");
    Assert(!AdbEndpointIdentityGuard.TryGetLdTarget("127.0.0.1:23456", config, out _, out _),
        "non-emulator serial must not be treated as LD");
    Assert(AdbEndpointIdentityGuard.TryGetLdTarget("emulator-9998",
        "{\"extras\":{\"ld\":{\"enable\":true}}}", out _, out var missingPid)
        && missingPid == 0, "LD target with missing process identity must fail closed");

    var ldListener = new AdbTcpListener(IPAddress.Any, 9999, 41);
    var otherListener = new AdbTcpListener(IPAddress.Loopback, 9999, 42);
    Assert(AdbEndpointIdentityGuard.ResolveLdSerial(port, pid, [ldListener]) == "emulator-9998",
        "LD wildcard listener should retain the emulator serial without collision");
    Assert(AdbEndpointIdentityGuard.ResolveLdSerial(port, pid, [ldListener, otherListener]) == "127.0.0.2:9999",
        "MuMu loopback listener should route LD through the verified alias");
    Assert(AdbEndpointIdentityGuard.ResolveLdSerial(port, pid, [otherListener]) == null,
        "wrong process alone must block LD ADB input");
    Assert(AdbEndpointIdentityGuard.ResolveLdSerial(port, pid, []) == null,
        "missing LD listener must block connection");
    Assert(AdbEndpointIdentityGuard.ResolveLdSerial(port, pid,
        [ldListener, otherListener, new AdbTcpListener(IPAddress.Parse("127.0.0.2"), 9999, 43)]) == null,
        "wrong process on the alias must block connection");
}

static void AdbEndpointGuardReadsWindowsPortOwner()
{
    if (!OperatingSystem.IsWindows()) return;

    for (var attempt = 0; attempt < 20; attempt++)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port % 2 == 0) continue;

            var config = System.Text.Json.JsonSerializer.Serialize(
                new { extras = new { ld = new { enable = true, pid = Environment.ProcessId } } });
            Assert(AdbEndpointIdentityGuard.EnsureSelectedTarget($"emulator-{port - 1}", config, true)
                == $"emulator-{port - 1}", "correct live port owner should retain the serial");
            try
            {
                AdbEndpointIdentityGuard.EnsureSelectedTarget($"emulator-{port - 1}",
                    "{\"extras\":{\"ld\":{\"enable\":true,\"pid\":1}}}", true);
                throw new InvalidOperationException("wrong live port owner was accepted");
            }
            catch (AdbTargetMismatchException)
            {
            }
            Assert(AdbEndpointIdentityGuard.EnsureSelectedTarget($"emulator-{port - 1}",
                "{\"extras\":{\"ld\":{\"enable\":true,\"pid\":1}}}", false)
                == $"emulator-{port - 1}", "disabled guard should keep existing behavior");
            return;
        }
        finally
        {
            listener.Stop();
        }
    }
    throw new InvalidOperationException("could not allocate an odd local TCP port for ADB guard test");
}

static void PreventSleepTracksTasksAndReleasesNativeState()
{
    if (!OperatingSystem.IsWindows()) return;
    Assert(Avalonia.Threading.Dispatcher.UIThread.CheckAccess(), "power test must use the UI thread");
    static void AssertPowerState(bool expected, string stage)
    {
        Assert(SystemSleepHelper.IsPreventingSleep == expected, $"incorrect power state: {stage}");
        var previous = NativePowerTest.SetThreadExecutionState(expected ? 0x80000003 : 0x80000000);
        Assert((previous & 3) == (expected ? 3u : 0u), $"incorrect native power flags: {stage}, {previous:x}");
    }

    static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("power lifecycle did not finish on the UI thread");
            Thread.Sleep(1);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }

    var configPath = MFAAvalonia.Configuration.GlobalConfiguration.ConfigPath;
    var original = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
    var config = MFAAvalonia.Configuration.ConfigurationManager.Current.Config;
    var hadLegacy = config.TryGetValue("PreventSleep", out var legacy);
    var scopes = new List<IDisposable>();
    try
    {
        config["PreventSleep"] = true;
        MFAAvalonia.Configuration.GlobalConfiguration.SetValue("PreventSleep", "");
        Assert(SystemSleepHelper.GetPreventSleepSetting(), "legacy setting must remain readable");
        SystemSleepHelper.SavePreventSleepSetting(false);
        Assert(!SystemSleepHelper.GetPreventSleepSetting(), "global false must override legacy true");
        SystemSleepHelper.SavePreventSleepSetting(true);
        Assert(SystemSleepHelper.GetPreventSleepSetting(), "enabled setting must persist");
        Assert(!SystemSleepHelper.IsPreventingSleep, "an enabled preference must not keep an idle application awake");
        SystemSleepHelper.ApplyPreventSleep();
        AssertPowerState(false, "idle application startup");

        var first = SystemSleepHelper.BeginTaskExecutionAsync().GetAwaiter().GetResult();
        scopes.Add(first);
        AssertPowerState(true, "first task started");
        var queue = new MFAAvalonia.Helper.ValueType.ObservableQueue<int>();
        queue.Enqueue(1);
        queue.Dequeue();
        AssertPowerState(true, "last task is executing after dequeue");

        var second = SystemSleepHelper.BeginTaskExecutionAsync().GetAwaiter().GetResult();
        scopes.Add(second);
        first.Dispose();
        first.Dispose();
        AssertPowerState(true, "another task remains after repeated disposal");
        SystemSleepHelper.SavePreventSleepSetting(false);
        AssertPowerState(false, "disabled during execution");
        SystemSleepHelper.SavePreventSleepSetting(true);
        AssertPowerState(true, "enabled during execution");
        second.Dispose();
        AssertPowerState(false, "all tasks completed");
        Assert(SystemSleepHelper.GetPreventSleepSetting(), "task completion must preserve the saved preference");

        SystemSleepHelper.SavePreventSleepSetting(false);
        using (var disabledTask = SystemSleepHelper.BeginTaskExecutionAsync().GetAwaiter().GetResult())
            AssertPowerState(false, "task started with preference disabled");
        SystemSleepHelper.SavePreventSleepSetting(true);
        foreach (var outcome in new[] { "completed", "stopped", "failed" })
        {
            try
            {
                using var task = SystemSleepHelper.BeginTaskExecutionAsync().GetAwaiter().GetResult();
                AssertPowerState(true, $"task running before {outcome}");
                if (outcome == "stopped") throw new OperationCanceledException();
                if (outcome == "failed") throw new ApplicationException("simulated task failure");
            }
            catch (OperationCanceledException) when (outcome == "stopped") { }
            catch (ApplicationException) when (outcome == "failed") { }
            AssertPowerState(false, $"released after {outcome}");
        }

        var executeTasks = typeof(MFAAvalonia.Extensions.MaaFW.MaaProcessor)
            .GetMethod("ExecuteTasks", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var outcome in new[] { "completed", "stopped", "failed" })
        {
            var processor = new MFAAvalonia.Extensions.MaaFW.MaaProcessor($"power-test-{Guid.NewGuid():N}");
            try
            {
                // 测试只执行真实队列循环，隔离需要完整主窗口的通知订阅。
                processor.TaskQueue.CountChanged = null;
                var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                processor.TaskQueue.Enqueue(new MFAAvalonia.Helper.ValueType.MFATask
                {
                    Name = "Power lifecycle test",
                    Action = () => completion.Task,
                });
                var execution = (Task)executeTasks.Invoke(processor, [CancellationToken.None])!;
                Assert(processor.TaskQueue.Count == 0 && !execution.IsCompleted,
                    "last dequeued task must still be executing");
                Assert(TaskMaintenanceCoordinator.Shared.TryBeginMaintenance(() => false) == null,
                    "last dequeued production task allowed catalog maintenance");
                AssertPowerState(true, $"real execution loop waiting before {outcome}");
                if (outcome == "completed") completion.SetResult();
                else if (outcome == "stopped") completion.SetCanceled();
                else completion.SetException(new ApplicationException("simulated execution failure"));
                PumpUntil(() => execution.IsCompleted && !SystemSleepHelper.IsPreventingSleep);
                execution.GetAwaiter().GetResult();
                AssertPowerState(false, $"real execution loop ended with {outcome}");
                Assert(!TaskMaintenanceCoordinator.Shared.IsBusy, "production task leaked catalog lease");
            }
            finally
            {
                processor.Dispose();
            }
        }

        using (var maintenance = TaskMaintenanceCoordinator.Shared.TryBeginMaintenance(() => false)!)
        using (var cancel = new CancellationTokenSource())
        {
            var processor = new MFAAvalonia.Extensions.MaaFW.MaaProcessor($"maintenance-wait-{Guid.NewGuid():N}");
            processor.TaskQueue.CountChanged = null;
            var called = false;
            processor.TaskQueue.Enqueue(new MFAAvalonia.Helper.ValueType.MFATask
            { Name = "Canceled maintenance wait", Action = () => { called = true; return Task.CompletedTask; } });
            var execution = (Task)executeTasks.Invoke(processor, [cancel.Token])!;
            Assert(!execution.IsCompleted && processor.TaskQueue.Count == 1, "production task did not await maintenance");
            cancel.Cancel();
            PumpUntil(() => execution.IsCompleted);
            execution.GetAwaiter().GetResult();
            Assert(!called && (MFAAvalonia.Helper.ValueType.MFATask.MFATaskStatus)typeof(MFAAvalonia.Extensions.MaaFW.MaaProcessor).GetField("Status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(processor)! == MFAAvalonia.Helper.ValueType.MFATask.MFATaskStatus.STOPPED,
                "canceling maintenance wait ran input or marked business failure");
            processor.Dispose();
        }
        Assert(!TaskMaintenanceCoordinator.Shared.IsBusy, "canceled production wait leaked lease");
        var workerScope = Task.Run(() => SystemSleepHelper.BeginTaskExecutionAsync());
        PumpUntil(() => workerScope.IsCompleted);
        var acquiredOnUi = workerScope.GetAwaiter().GetResult();
        scopes.Add(acquiredOnUi);
        AssertPowerState(true, "worker task acquisition applied on UI thread");
        Task.Run(acquiredOnUi.Dispose).GetAwaiter().GetResult();
        PumpUntil(() => !SystemSleepHelper.IsPreventingSleep);
        AssertPowerState(false, "worker task completion released on UI thread");

        var pending = SystemSleepHelper.BeginTaskExecutionAsync().GetAwaiter().GetResult();
        scopes.Add(pending);
        Task.Run(() => SystemSleepHelper.SavePreventSleepSetting(true)).GetAwaiter().GetResult();
        SystemSleepHelper.SavePreventSleepSetting(false);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        AssertPowerState(false, "queued old preference cannot override the latest disabled state");
        SystemSleepHelper.SavePreventSleepSetting(true);
        AssertPowerState(true, "task still active before shutdown");
        var delayedTask = Task.Run(() => SystemSleepHelper.BeginTaskExecutionAsync());
        SystemSleepHelper.Shutdown();
        AssertPowerState(false, "application shutdown");
        PumpUntil(() => delayedTask.IsCompleted);
        using (var lateWorkerTask = delayedTask.GetAwaiter().GetResult())
            AssertPowerState(false, "queued worker cannot reacquire after shutdown");
        using (var lateTask = SystemSleepHelper.BeginTaskExecutionAsync().GetAwaiter().GetResult())
            AssertPowerState(false, "late task cannot reacquire after shutdown");
        pending.Dispose();
        AssertPowerState(false, "late completion cannot reacquire after shutdown");
        Assert(SystemSleepHelper.GetPreventSleepSetting(), "shutdown must preserve the saved preference");
    }
    finally
    {
        foreach (var scope in scopes) scope.Dispose();
        SystemSleepHelper.SavePreventSleepSetting(false);
        if (hadLegacy) config["PreventSleep"] = legacy!;
        else config.Remove("PreventSleep");
        if (original != null) File.WriteAllBytes(configPath, original);
        else if (File.Exists(configPath)) File.Delete(configPath);
    }
}

static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow >= deadline)
            throw new TimeoutException("condition was not reached");
        await Task.Delay(10);
    }
}

static async Task DebouncesToLatestChangeAsync()
{
    var calls = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(50));

    action.Schedule();
    await Task.Delay(10);
    action.Schedule();
    await Task.Delay(10);
    action.Schedule();
    await WaitUntilAsync(() => Volatile.Read(ref calls) == 1, TimeSpan.FromSeconds(1));
    await Task.Delay(80);
    Assert(calls == 1, $"debounce expected one save, got {calls}");
}

static async Task SerializesChangesArrivingDuringSaveAsync()
{
    var calls = 0;
    var active = 0;
    var maximumActive = 0;
    var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var action = new DebouncedAsyncAction(
        async () =>
        {
            var currentCall = Interlocked.Increment(ref calls);
            var nowActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, nowActive);
            if (currentCall == 1)
            {
                firstStarted.SetResult();
                await releaseFirst.Task;
            }
            Interlocked.Decrement(ref active);
        },
        TimeSpan.FromMilliseconds(20));

    action.Schedule();
    await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
    action.Schedule();
    await Task.Delay(50);
    releaseFirst.SetResult();
    await WaitUntilAsync(() => Volatile.Read(ref calls) == 2, TimeSpan.FromSeconds(1));
    Assert(maximumActive == 1, $"saves overlapped: max active {maximumActive}");
}

static async Task RetriesOnceAsync()
{
    var attempts = 0;
    var failures = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
                throw new IOException("transient");
            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(10),
        _ =>
        {
            Interlocked.Increment(ref failures);
            return Task.CompletedTask;
        });

    action.Schedule();
    await WaitUntilAsync(() => Volatile.Read(ref attempts) == 2, TimeSpan.FromSeconds(1));
    Assert(failures == 0, "successful retry reported a terminal failure");
}

static async Task ReportsTerminalFailureAfterRetryAsync()
{
    var attempts = 0;
    var failures = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("persistent");
        },
        TimeSpan.FromMilliseconds(10),
        _ =>
        {
            Interlocked.Increment(ref failures);
            return Task.CompletedTask;
        });

    action.Schedule();
    await WaitUntilAsync(() => Volatile.Read(ref failures) == 1, TimeSpan.FromSeconds(1));
    Assert(attempts == 2, $"expected two attempts, got {attempts}");
}

static async Task CancelPreventsPendingSaveAsync()
{
    var calls = 0;
    using var action = new DebouncedAsyncAction(
        () =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        },
        TimeSpan.FromMilliseconds(100));
    action.Schedule();
    action.Cancel();
    await Task.Delay(180);
    Assert(calls == 0, "cancelled save still ran");
}

static void RuntimeOptionsIncludeProcessCleanupSwitch()
{
    var model = new PerformanceProfileSettingsUserControlModel();
    var nativeSwitch = typeof(PerformanceProfileSettingsUserControlModel).GetProperty(
        "NativeRealtimeEnabled");
    Assert(nativeSwitch != null, "native realtime switch property missing");
    Assert(
        nativeSwitch!.GetValue(model) is false,
        "native realtime switch must default to disabled");
    var capture = typeof(PerformanceProfileSettingsUserControlModel).GetMethod(
        "CaptureRuntimeOptions",
        BindingFlags.Instance | BindingFlags.NonPublic);
    Assert(capture != null, "CaptureRuntimeOptions method missing");
    var options = (JObject?)capture!.Invoke(model, null);
    Assert(options != null, "runtime options capture returned null");
    Assert(
        options!.Value<bool>("skip_process_conflict_cleanup") == false,
        "process cleanup switch must default to false");
    Assert(
        options.Value<bool>("skip_result_check") == false,
        "result check switch must default to false");
    var resultSwitch = typeof(PerformanceProfileSettingsUserControlModel).GetProperty(
        "SkipResultCheck");
    Assert(resultSwitch != null, "result check switch property missing");
    resultSwitch!.SetValue(model, true);
    var enabledOptions = (JObject?)capture.Invoke(model, null);
    Assert(
        enabledOptions?.Value<bool>("skip_result_check") == true,
        "result check switch was not captured after it changed");
    Assert(model.PlayFailureRetryCount == 1, "retry count default must remain one");
    model.PlayFailureRetryCount = 99;
    var retryOptions = (JObject?)capture.Invoke(model, null);
    Assert(retryOptions?.Value<int>("play_failure_retry_count") == 99,
        "maximum retry count was not captured");
    Assert(
        options["native_realtime_enabled"]?.Type == JTokenType.Boolean
        && options.Value<bool>("native_realtime_enabled") == false,
        "native realtime option must be captured and default to false");
    Assert(
        options.Value<bool>("note_speed_settings_enabled"),
        "note speed settings must default to enabled");
    Assert(
        options["game_effect_settings_enabled"] == null
        && options["note_skin_type"] == null
        && options["tap_effect"] == null
        && options["judgement_assist_effect"] == null,
        "removed game effect options must not be persisted");
    Assert(
        options["life_safety_enabled"] == null
        && options["life_exit_threshold"] == null
        && options["rehearsal_ignore_life_safety"] == null,
        "removed life protection options must not be persisted");
}

static void NativeExperimentalOptionsKeepBooleanAndSaveBoundaries()
{
    var type = typeof(PerformanceProfileSettingsUserControlModel);
    var model = new PerformanceProfileSettingsUserControlModel();
    var load = type.GetMethod("LoadNativeExperimentalOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var capture = type.GetMethod("CaptureRuntimeOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var pending = type.GetField("_pendingRuntimeOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var loaded = type.GetField("_runtimeOptionsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
    load.Invoke(model, [new JObject()]);
    Assert(!model.NativeLifeFeedbackEnabled && !model.NativeWaitJitterFilterEnabled,
        "missing experiment options must default false");
    foreach (var life in new[] { false, true })
    foreach (var wait in new[] { false, true })
    {
        load.Invoke(model, [new JObject { ["native_life_feedback_enabled"] = life,
            ["native_wait_jitter_filter_enabled"] = wait }]);
        model.NativeRealtimeEnabled = false;
        var snapshot = (JObject)capture.Invoke(model, null)!;
        Assert(snapshot.Value<bool>("native_life_feedback_enabled") == life
            && snapshot.Value<bool>("native_wait_jitter_filter_enabled") == wait,
            "capture or disabling Native lost an experimental choice");
    }
    Assert(pending.GetValue(model) == null, "unloaded experiments must not auto-save defaults");
    try
    {
        load.Invoke(model, [new JObject { ["native_life_feedback_enabled"] = false,
            ["native_wait_jitter_filter_enabled"] = "false" }]);
        throw new InvalidOperationException("string experiment option was accepted");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
    Assert(model.NativeLifeFeedbackEnabled && model.NativeWaitJitterFilterEnabled,
        "invalid load partially replaced the saved experiment choices");
    loaded.SetValue(model, true);
    model.SkipResultCheck = true;
    var saved = (JObject)pending.GetValue(model)!;
    Assert(saved.Value<bool>("native_life_feedback_enabled") && saved.Value<bool>("native_wait_jitter_filter_enabled"),
        "full replacement save lost experiment fields");
    ((DebouncedAsyncAction)type.GetField("_runtimeAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!
        .GetValue(model)!).Cancel();
}

static void CooperativeLoadingGuardIsVisibleAndPreservesAutoSaveBoundary()
{
    var type = typeof(PerformanceProfileSettingsUserControlModel);
    var property = type.GetProperty("CooperativeMemberLoadingGuardEnabled");
    Assert(property != null, "cooperative loading guard switch property missing");
    var model = new PerformanceProfileSettingsUserControlModel();
    Assert(property!.GetValue(model) is true, "cooperative loading guard must default to enabled");
    var capture = type.GetMethod("CaptureRuntimeOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var pending = type.GetField("_pendingRuntimeOptions", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var loaded = type.GetField("_runtimeOptionsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var load = type.GetMethod("LoadCooperativeMemberLoadingGuard", BindingFlags.Instance | BindingFlags.NonPublic)!;
    load.Invoke(model, [new JObject()]);
    Assert(property.GetValue(model) is true, "old runtime settings must enable guard when field is absent");
    load.Invoke(model, [new JObject { ["cooperative_member_loading_guard_enabled"] = false }]);
    Assert(property.GetValue(model) is false, "saved false must survive loading");
    try
    {
        load.Invoke(model, [new JObject { ["cooperative_member_loading_guard_enabled"] = "false" }]);
        throw new InvalidOperationException("non-boolean loading guard was accepted");
    }
    catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
    property.SetValue(model, false);
    Assert(pending.GetValue(model) == null, "unloaded or failed settings must not auto-save defaults");
    Assert(((JObject)capture.Invoke(model, null)!).Value<bool>("cooperative_member_loading_guard_enabled") == false,
        "disabled loading guard missing from captured settings");
    loaded.SetValue(model, true);
    property.SetValue(model, true);
    property.SetValue(model, false);
    model.SkipResultCheck = true;
    Assert(((JObject)pending.GetValue(model)!).Value<bool>("cooperative_member_loading_guard_enabled") == false,
        "changing another switch must preserve disabled loading guard");
    // 只验证队列捕获，避免单元测试触发部署目录的真实持久化。
    var autoSave = type.GetField("_runtimeAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!;
    ((DebouncedAsyncAction)autoSave.GetValue(model)!).Cancel();
}

static async Task CooperativeLoadingGuardPersistsThroughProfileManagerAsync()
{
    var python = Environment.GetEnvironmentVariable("MAABANGDREAM_TEST_PYTHON");
    var projectRoot = Environment.GetEnvironmentVariable("MAABANGDREAM_TEST_PROJECT_ROOT");
    if (string.IsNullOrEmpty(python) || string.IsNullOrEmpty(projectRoot)) return;
    // 使用真实管理器和隔离的 selection 文件，不读取或覆盖用户 Profile。
    var root = Path.Combine(projectRoot, ".local", "mfa-guard-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(root);
    var selectionPath = Path.Combine(root, "selection.json");
    var configPath = Path.Combine(AppContext.BaseDirectory, "profile-manager.json");
    var originalConfig = File.Exists(configPath) ? await File.ReadAllBytesAsync(configPath) : null;
    var type = typeof(PerformanceProfileSettingsUserControlModel);
    var autoSaveField = type.GetField("_runtimeAutoSave", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var loadedField = type.GetField("_runtimeOptionsLoaded", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var models = new List<PerformanceProfileSettingsUserControlModel>();
    try
    {
        var code = "import sys; sys.path.insert(0,sys.argv[1]); import agent.profile_manager as m; "
                   + "original=m.handle_request; m.handle_request=lambda request: original(request,root=sys.argv[2]); "
                   + "raise SystemExit(m.main())";
        await File.WriteAllTextAsync(configPath, new JObject
        {
            ["child_exec"] = python,
            ["child_args"] = new JArray("-c", code, projectRoot, root),
            ["artifact_paths"] = new JObject { ["profiles"] = root },
            ["chart_sync"] = new JObject { ["manifest_path"] = Path.Combine(root, "manifest.json") }
        }.ToString());
        await File.WriteAllTextAsync(selectionPath,
            "{\"version\":1,\"pinned\":{},\"runtime_options\":{\"native_realtime_enabled\":false}}");
        var oldSelection = await File.ReadAllTextAsync(selectionPath);
        var model = new PerformanceProfileSettingsUserControlModel();
        models.Add(model);
        await model.RefreshAsync();
        Assert(model.CooperativeMemberLoadingGuardEnabled, "old selection did not load guard as enabled");
        Assert(model.BestdoriAutoUpdateEnabled && model.BestdoriAutoUpdateIntervalHours == 24,
            "old selection did not migrate auto update defaults");
        Assert(await File.ReadAllTextAsync(selectionPath) == oldSelection, "refresh auto-saved defaults");
        model.CooperativeMemberLoadingGuardEnabled = false;
        model.NativeLifeFeedbackEnabled = true;
        model.NativeWaitJitterFilterEnabled = true;
        model.BestdoriAutoUpdateEnabled = false;
        model.BestdoriAutoUpdateIntervalHours = 72;
        await WaitUntilAsync(() => JObject.Parse(File.ReadAllText(selectionPath))["runtime_options"]?
            .Value<bool?>("cooperative_member_loading_guard_enabled") == false, TimeSpan.FromSeconds(5));
        var reloaded = new PerformanceProfileSettingsUserControlModel();
        models.Add(reloaded);
        await reloaded.RefreshAsync();
        Assert(!reloaded.CooperativeMemberLoadingGuardEnabled, "saved false did not survive refresh");
        Assert(!reloaded.NativeRealtimeEnabled, "guard must be independent of native input switch");
        Assert(reloaded.NativeLifeFeedbackEnabled && reloaded.NativeWaitJitterFilterEnabled,
            "experimental Native settings did not persist through real manager");
        Assert(!reloaded.BestdoriAutoUpdateEnabled && reloaded.BestdoriAutoUpdateIntervalHours == 72,
            "disabled auto update/custom interval did not persist through real manager");
        reloaded.SkipResultCheck = true;
        await WaitUntilAsync(() => JObject.Parse(File.ReadAllText(selectionPath))["runtime_options"]?
            .Value<bool?>("skip_result_check") == true, TimeSpan.FromSeconds(5));
        Assert(JObject.Parse(File.ReadAllText(selectionPath))["runtime_options"]?
            .Value<bool?>("cooperative_member_loading_guard_enabled") == false, "other option save lost false");
        Assert(JObject.Parse(File.ReadAllText(selectionPath))["runtime_options"]?
            .Value<bool?>("native_life_feedback_enabled") == true, "other option save lost experiment");
        ((DebouncedAsyncAction)autoSaveField.GetValue(reloaded)!).Cancel();
        await File.WriteAllTextAsync(selectionPath, "broken selection");
        try { await reloaded.RefreshAsync(); }
        catch (InvalidOperationException) when (reloaded.StatusText.StartsWith("Profile 管理器调用失败："))
        {
            // 无 UI 的测试进程可能没有 Toast 服务，读取失败状态仍须关闭保存门禁。
        }
        Assert(loadedField.GetValue(reloaded) is false, "failed refresh must close auto-save gate");
        reloaded.CooperativeMemberLoadingGuardEnabled = true;
        reloaded.BestdoriAutoUpdateEnabled = true;
        reloaded.BestdoriAutoUpdateIntervalHours = 1;
        reloaded.SkipResultCheck = false;
        reloaded.NativeLifeFeedbackEnabled = false;
        reloaded.NativeWaitJitterFilterEnabled = false;
        await Task.Delay(750);
        Assert(await File.ReadAllTextAsync(selectionPath) == "broken selection", "failed read overwrote file with defaults");
        Console.WriteLine("Cooperative guard profile-manager persistence tests passed: " + root);
    }
    finally
    {
        foreach (var model in models)
            ((DebouncedAsyncAction)autoSaveField.GetValue(model)!).Cancel();
        if (originalConfig != null) await File.WriteAllBytesAsync(configPath, originalConfig);
        else File.Delete(configPath);
    }
}

static void ProfileCurrentSelectionMarkerIsIndependentFromGridSelection()
{
    var current = new PerformanceProfileItem(new JObject
    {
        ["filename"] = "expert-current.json",
        ["difficulty"] = "Expert",
    }, isCurrentSelection: true);
    var inspected = new PerformanceProfileItem(new JObject
    {
        ["filename"] = "special-inspected.json",
        ["difficulty"] = "Special",
    });

    Assert(current.IsCurrentSelection, "current profile marker was not retained");
    Assert(!inspected.IsCurrentSelection, "ordinary grid selection was marked current");
}

static void ProfileSelectionRequestUsesTaskDifficulty()
{
    var request = PerformanceProfileSettingsUserControlModel.CreateSetCurrentProfileRequest(
        "Easy",
        "expert-current.json");

    Assert(request.Value<string>("operation") == "pin", "profile selection operation changed");
    Assert(request.Value<string>("difficulty") == "Easy", "profile selection used source difficulty");
    Assert(request.Value<string>("profile") == "expert-current.json", "profile selection filename changed");
}

static void CalibrationRecordsReadNestedSessionResults()
{
    static JObject Attempt(int perfect, int great, int good, int bad, int miss) =>
        new()
        {
            ["suggested_timing_offset_ms"] = -12,
            ["result"] = new JObject
            {
                ["song_id"] = "song-306",
                ["perfect"] = perfect,
                ["great"] = great,
                ["good"] = good,
                ["bad"] = bad,
                ["miss"] = miss,
                ["fast"] = 3,
                ["slow"] = 2,
                ["hit_rate"] = 0.9875,
                ["confidence"] = 0.9,
            },
        };

    var item = new PerformanceProfileItem(new JObject
    {
        ["rehearsals"] = new JArray(Attempt(355, 41, 0, 0, 5)),
        ["formal"] = Attempt(370, 25, 0, 0, 6),
    });

    Assert(
        item.CalibrationRecordsText.Contains("355/41/0/0/5"),
        $"nested rehearsal judgements missing: {item.CalibrationRecordsText}");
    Assert(
        item.CalibrationRecordsText.Contains("370/25/0/0/6"),
        $"nested formal judgements missing: {item.CalibrationRecordsText}");
    Assert(
        item.CalibrationRecordsText.Contains("时序建议 -12 ms"),
        $"attempt-level timing suggestion missing: {item.CalibrationRecordsText}");
}

static void ChartCatalogSummaryIsReadable()
{
    var clientType = typeof(PerformanceProfileSettingsUserControlModel).Assembly.GetType(
        "MFAAvalonia.ViewModels.UsersControls.Settings.ProfileManagerClient")
        ?? throw new InvalidOperationException("profile manager client missing");
    var formatter = clientType.GetMethod(
        "FormatChartCatalogStatus",
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
        ?? throw new InvalidOperationException("chart catalog formatter missing");
    var text = (string?)formatter.Invoke(null, [new JObject
    {
        ["generated_at"] = "2026-08-30T00:00:00Z",
        ["summary"] = new JObject
        {
            ["songs_with_charts"] = 809,
            ["charts"] = 1777,
            ["jackets"] = 867,
            ["recoverable_errors"] = 2,
            ["fatal_errors"] = 1,
        },
    }]);
    Assert(text != null && text.Contains("809 首 / 1777 张谱面 / 867 个封面 / 3 个错误"),
        $"unexpected chart catalog status: {text}");
}

static void ApplicationBrandingUsesProjectName()
{
    Assert(
        MFAAvalonia.Assets.Localization.Strings.AppTitle == "MaaBanGDream",
        $"unexpected application title: {MFAAvalonia.Assets.Localization.Strings.AppTitle}");
    Assert(
        MFAAvalonia.Helper.IconHelper.DefaultBrandIconUri.EndsWith(
            "/Assets/maabangdream-icon.png",
            StringComparison.Ordinal),
        $"unexpected application icon: {MFAAvalonia.Helper.IconHelper.DefaultBrandIconUri}");
}

static void NativeGitHubUpdaterSelectsPortablePackageSafely()
{
    var assets = new[]
    {
        new VersionChecker.GitHubReleaseAsset("MaaBanGDream-v1.3.6-win-x64-update.zip.sha256", "update-sha", ""),
        new VersionChecker.GitHubReleaseAsset("MaaBanGDream-v1.3.6-win-x64.zip", "full", "full-hash"),
        new VersionChecker.GitHubReleaseAsset("MaaBanGDream-v1.3.6-win-x64.zip.sha256", "full-sha", ""),
        new VersionChecker.GitHubReleaseAsset("MaaBanGDream-v1.3.6-win-x64-update.zip", "update", "update-hash"),
    };
    var runtimeFree = VersionChecker.SelectGitHubReleaseAsset(assets, "win", "win", "x64", true);
    Assert(runtimeFree.Url == "update", $"expected runtime-free update archive, got {runtimeFree.Name}");
    var full = VersionChecker.SelectGitHubReleaseAsset(assets, "win", "win", "x64", false);
    Assert(full.Url == "full", $"expected full archive when runtime is missing, got {full.Name}");
}

static void NativeGitHubUpdaterRejectsAmbiguousArchives()
{
    var assets = new[]
    {
        new VersionChecker.GitHubReleaseAsset("one-win-x64-update.zip", "one", ""),
        new VersionChecker.GitHubReleaseAsset("two-win-x64-update.zip", "two", ""),
    };
    try
    {
        VersionChecker.SelectGitHubReleaseAsset(assets, "win", "win", "x64", true);
        throw new InvalidOperationException("ambiguous archives were accepted");
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("不唯一", StringComparison.Ordinal))
    {
    }
}

static void GitHubOnlyResourcesCoerceLegacyMirrorSelection()
{
    Assert(VersionChecker.NormalizeResourceDownloadSourceIndex(1, null) == 0,
        "resource without RID must use GitHub");
    Assert(VersionChecker.NormalizeResourceDownloadSourceIndex(1, "resource-rid") == 1,
        "resource with RID should retain Mirror selection");
}

static async Task<(bool Success, string Path)> InvokeNativeDownloadAsync(string url, string path)
{
    using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    return await VersionChecker.DownloadFileWithClientAsync(httpClient, url, path);
}

static async Task<string> ServeDownloadOnceAsync(
    TcpListener listener,
    byte[] payload,
    bool honorRange,
    bool alreadyComplete = false)
{
    using var client = await listener.AcceptTcpClientAsync();
    await using var stream = client.GetStream();
    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
    var headers = new StringBuilder();
    string? line;
    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync()))
        headers.AppendLine(line);
    var headerText = headers.ToString();
    var rangeMatch = System.Text.RegularExpressions.Regex.Match(
        headerText,
        @"Range:\s*bytes=(\d+)-",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    var start = rangeMatch.Success ? int.Parse(rangeMatch.Groups[1].Value) : 0;
    if (alreadyComplete)
    {
        var rangeResponse = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{payload.Length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(rangeResponse);
        return headerText;
    }
    var responseStart = honorRange ? start : 0;
    var status = honorRange && start > 0 ? "206 Partial Content" : "200 OK";
    var responseHeaders = new StringBuilder()
        .Append($"HTTP/1.1 {status}\r\n")
        .Append($"Content-Length: {payload.Length - responseStart}\r\n");
    if (status.StartsWith("206", StringComparison.Ordinal))
        responseHeaders.Append($"Content-Range: bytes {responseStart}-{payload.Length - 1}/{payload.Length}\r\n");
    responseHeaders.Append("Connection: close\r\n\r\n");
    var headerBytes = Encoding.ASCII.GetBytes(responseHeaders.ToString());
    await stream.WriteAsync(headerBytes);
    await stream.WriteAsync(payload.AsMemory(responseStart));
    return headerText;
}

static async Task NativeDownloaderResumesAndRestartsSafelyAsync()
{
    var payload = Encoding.UTF8.GetBytes("native updater resumable payload");
    foreach (var honorRange in new[] { true, false })
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var tempRoot = Path.Combine(Path.GetTempPath(), $"mfa-download-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempRoot);
            var target = Path.Combine(tempRoot, "payload.zip");
            await File.WriteAllBytesAsync(target + ".part", payload[..7]);
            var serverTask = ServeDownloadOnceAsync(listener, payload, honorRange);
            var result = await InvokeNativeDownloadAsync(
                $"http://127.0.0.1:{port}/payload.zip",
                target).WaitAsync(TimeSpan.FromSeconds(10));
            var requestHeaders = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert(requestHeaders.Contains("Range: bytes=7-", StringComparison.OrdinalIgnoreCase),
                "resume request did not contain the expected Range header");
            Assert(result.Success && File.Exists(target), "native downloader did not finalize the archive");
            Assert((await File.ReadAllBytesAsync(target)).SequenceEqual(payload),
                honorRange ? "206 resume produced incorrect bytes" : "200 restart produced incorrect bytes");
            Directory.Delete(tempRoot, true);
        }
        finally
        {
            listener.Stop();
        }
    }

    var completedListener = new TcpListener(IPAddress.Loopback, 0);
    completedListener.Start();
    try
    {
        var port = ((IPEndPoint)completedListener.LocalEndpoint).Port;
        var tempRoot = Path.Combine(Path.GetTempPath(), $"mfa-download-complete-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        var target = Path.Combine(tempRoot, "payload.zip");
        await File.WriteAllBytesAsync(target + ".part", payload);
        var serverTask = ServeDownloadOnceAsync(completedListener, payload, true, alreadyComplete: true);
        var result = await InvokeNativeDownloadAsync(
            $"http://127.0.0.1:{port}/payload.zip",
            target).WaitAsync(TimeSpan.FromSeconds(10));
        var requestHeaders = await serverTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(requestHeaders.Contains($"Range: bytes={payload.Length}-", StringComparison.OrdinalIgnoreCase),
            "completed partial file did not send the expected Range header");
        Assert(result.Success && (await File.ReadAllBytesAsync(target)).SequenceEqual(payload),
            "416 completion did not finalize the existing partial file");
        Directory.Delete(tempRoot, true);
    }
    finally
    {
        completedListener.Stop();
    }
}

static async Task NativeDownloaderRejectsShaMismatchAsync()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"mfa-sha-test-{Guid.NewGuid():N}.zip");
    await File.WriteAllBytesAsync(tempPath, Encoding.UTF8.GetBytes("payload"));
    try
    {
        Assert(!await VersionChecker.VerifyFileSha256Async(tempPath, new string('0', 64)),
            "SHA256 mismatch was accepted");
    }
    finally
    {
        File.Delete(tempPath);
    }
}

static async Task AboutMetadataMergeAndLateLoadAsync()
{
    var metadata = new JObject
    {
        ["name"] = "MaaBanGDream",
        ["label"] = "BanG Dream! 自动化（MaaFramework）",
        ["version"] = "1.3.6",
        ["icon"] = "docs/assets/maabangdream-logo-v1.png",
        ["about_icon"] = "docs/assets/maabangdream-about-v1.png",
    }.ToObject<MFAAvalonia.Extensions.MaaFW.MaaInterface>()
      ?? throw new InvalidOperationException("failed to create About metadata");
    Assert(metadata.Icon == "docs/assets/maabangdream-logo-v1.png",
        "top-level interface icon was not retained for the About page");
    Assert(metadata.AboutIcon == "docs/assets/maabangdream-about-v1.png",
        "dedicated About icon was not retained");

    var baseMetadata = new MFAAvalonia.Extensions.MaaFW.MaaInterface
    {
        Icon = "base-icon.png",
        AboutIcon = "base-about.png",
    };
    baseMetadata.Merge(metadata);
    Assert(baseMetadata.Icon == "docs/assets/maabangdream-logo-v1.png",
        "merged interface lost the top-level icon");
    Assert(baseMetadata.AboutIcon == "docs/assets/maabangdream-about-v1.png",
        "merged interface lost the dedicated About icon");

    var tempDirectory = Path.Combine(Path.GetTempPath(), $"mfa-about-{Guid.NewGuid():N}");
    try
    {
        Directory.CreateDirectory(Path.Combine(tempDirectory, "docs"));
        await File.WriteAllTextAsync(Path.Combine(tempDirectory, "docs", "about.md"), "项目简介");
        await File.WriteAllTextAsync(Path.Combine(tempDirectory, "docs", "contact.md"), "联系方式");
        await File.WriteAllTextAsync(Path.Combine(tempDirectory, "docs", "license.md"), "许可证");
        metadata.Description = "docs/about.md";
        metadata.Contact = "docs/contact.md";
        metadata.License = "docs/license.md";
        var content = await MFAAvalonia.Extensions.MaaFW.MaaProcessor.ResolveSettingsMetadataContentAsync(
            metadata, tempDirectory);
        Assert(content.description == "项目简介" && content.contact == "联系方式" && content.license == "许可证",
            "late SettingsViewModel metadata refresh did not resolve all interface content");
    }
    finally
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }
}

static async Task GitHubReleaseNotesUseExactTagAndWebFallbackAsync()
{
    var tempDirectory = Path.Combine(Path.GetTempPath(), $"mfa-release-{Guid.NewGuid():N}");
    try
    {
        var mismatchHandler = new GitHubRouteHandler(request =>
            GitHubResponse(request, HttpStatusCode.OK, "{\"tag_name\":\"v1.3.6\",\"body\":\"旧版本\"}"));
        using (var mismatchClient = new HttpClient(mismatchHandler))
        {
            try
            {
                await VersionChecker.GetGitHubReleaseNotesAsync(
                    "owner", "repo", "v1.3.7", mismatchClient, mismatchClient, tempDirectory);
                throw new InvalidOperationException("mismatched release tag was accepted");
            }
            catch (InvalidDataException)
            {
            }
        }

        var handler = new GitHubRouteHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases/tags/v1.3.6", StringComparison.Ordinal))
                return GitHubResponse(request, HttpStatusCode.Forbidden, "", reasonPhrase: "rate limit exceeded");
            if (path.EndsWith("/releases/tag/v1.3.6", StringComparison.Ordinal))
                return GitHubResponse(request, HttpStatusCode.OK,
                    "<div data-test-selector=\"body-content\" class=\"markdown-body\"><p>最新说明</p><div><strong>嵌套正文</strong></div></div>");
            throw new InvalidOperationException($"unexpected release-note request: {request.RequestUri}");
        });
        using var client = new HttpClient(handler);
        var result = await VersionChecker.GetGitHubReleaseNotesAsync(
            "owner", "repo", "v1.3.6", client, client, tempDirectory);
        Assert(!result.FromCache && result.Content.Contains("最新说明") && result.Content.Contains("嵌套正文"),
            "rate-limited release body did not use the same-tag GitHub web page");
        Assert(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/tag/v1.3.6", StringComparison.Ordinal)),
            "release body fallback queried a page other than the requested tag");

        var cacheHandler = new GitHubRouteHandler(request =>
            GitHubResponse(request, HttpStatusCode.Forbidden, "bad credentials"));
        using var cacheClient = new HttpClient(cacheHandler);
        var cached = await VersionChecker.GetGitHubReleaseNotesAsync(
            "owner", "repo", "v1.3.6", cacheClient, cacheClient, tempDirectory);
        Assert(cached.FromCache && cached.Content.Contains("本地缓存") && cached.Content.Contains("最新说明"),
            "same-version release cache was not clearly identified after a network failure");
        Assert(cacheHandler.Requests.All(uri => !uri.AbsolutePath.EndsWith("/releases/tag/v1.3.6", StringComparison.Ordinal)),
            "ordinary forbidden response must not use the release web fallback");
    }
    finally
    {
        Directory.Delete(tempDirectory, true);
    }
}

static async Task GitHubLatestReleaseNotesIncludeLatestTagAsync()
{
    var tempDirectory = Path.Combine(Path.GetTempPath(), $"mfa-latest-release-{Guid.NewGuid():N}");
    try
    {
        var handler = new GitHubRouteHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.RequestUri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return GitHubResponse(request, HttpStatusCode.Forbidden, "", new Uri("https://github.com/owner/repo/releases/tag/v1.3.8"), reasonPhrase: "rate limit exceeded");
            if (request.RequestUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                && path.EndsWith("/releases/latest", StringComparison.Ordinal))
                return GitHubResponse(request, HttpStatusCode.OK, "", new Uri("https://github.com/owner/repo/releases/tag/v1.3.8"));
            if (path.EndsWith("/releases/tag/v1.3.8", StringComparison.Ordinal))
                return GitHubResponse(request, HttpStatusCode.OK,
                    "<div data-test-selector=\"body-content\" class=\"markdown-body\"><p>最新版本说明</p></div>");
            throw new InvalidOperationException($"unexpected latest release request: {request.RequestUri}");
        });
        using var client = new HttpClient(handler);
        var latest = await VersionChecker.GetLatestGitHubReleaseNotesAsync(
            "owner", "repo", client, client, tempDirectory);
        Assert(latest.Version == "v1.3.8" && latest.Content.Contains("最新版本说明"),
            "latest release body did not report the redirected latest tag");
        Assert(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)),
            "latest release lookup did not use the latest endpoint");
        Assert(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/tag/v1.3.8", StringComparison.Ordinal)),
            "rate-limited latest release did not read the redirected tag body");

        Assert(VersionChecker.CanShowPendingResourceChangelog("v1.3.8", "1.3.8"),
            "GitHub tag and installed interface version were not treated as the same release");
        Assert(!VersionChecker.CanShowPendingResourceChangelog("v1.3.8", "v1.3.7"),
            "failed update would have displayed a mismatched changelog");

        var manifestRoot = Path.Combine(tempDirectory, "install");
        Directory.CreateDirectory(manifestRoot);
        await File.WriteAllTextAsync(Path.Combine(manifestRoot, "update-manifest.json"), "{\"version\":\"1.3.8\"}");
        Assert(VersionChecker.HasInstalledUpdateManifestVersion(manifestRoot, "v1.3.8"),
            "matching update manifest did not prove the portable update completed");
        Assert(!VersionChecker.HasInstalledUpdateManifestVersion(manifestRoot, "v1.3.7"),
            "mismatched update manifest was accepted");
    }
    finally
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }
}

static async Task AnnouncementsCacheAndContentChangesAsync()
{
    var root = Path.Combine(AppContext.BaseDirectory, "announcement-test-" + Guid.NewGuid().ToString("N"));
    var url = "https://example.invalid/announcement.md";
    try
    {
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        File.WriteAllText(Path.Combine(root, "docs", "announcement.md"), "# Bundled\n\nOffline notice");
        var offline = await MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ResolveAnnouncementAsync(url, root, () => Task.FromResult(string.Empty));
        Assert(offline.Contains("Offline notice"), "first offline launch lost the bundled announcement");
        var live = await MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ResolveAnnouncementAsync(url, root, () => Task.FromResult("# Online\n\nNew notice"));
        var cached = await MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ResolveAnnouncementAsync(url, root, () => Task.FromException<string>(new IOException("offline")));
        Assert(live == cached, "network failure did not retain the cached notice");
        var other = await MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ResolveAnnouncementAsync("https://other.invalid/notice.md", root, () => Task.FromResult(string.Empty));
        Assert(other == string.Empty, "announcement cache leaked to a different source");
        var one = MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ComputeFingerprint([
            new() { Title = "Notice", Content = "First\r\nSecond" }]);
        var equivalent = MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ComputeFingerprint([
            new() { Title = "Notice", Content = " First\nSecond\n" }]);
        var changed = MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ComputeFingerprint([
            new() { Title = "Notice", Content = "Changed" }]);
        Assert(one == equivalent && one != changed, "announcement fingerprint missed or invented a content change");
        Assert(!MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ShouldShowAnnouncement(false, false, one, one), "unchanged notice repeated");
        Assert(MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ShouldShowAnnouncement(false, true, one, changed), "new notice inherited the previous suppression");
        Assert(MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ShouldShowAnnouncement(true, true, one, one), "manual announcement viewing was suppressed");
        Assert(!MFAAvalonia.ViewModels.Windows.AnnouncementViewModel.ShouldShowAnnouncement(false, true, "", one), "legacy suppression was discarded");
    }
    finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
}

static void PackagedReleaseNotesAreReadLocally()
{
    var tempDirectory = Path.Combine(Path.GetTempPath(), $"mfa-packaged-release-{Guid.NewGuid():N}");
    try
    {
        var resourceDirectory = Path.Combine(tempDirectory, "resource");
        Directory.CreateDirectory(resourceDirectory);
        File.WriteAllText(Path.Combine(resourceDirectory, "Release.md"), "# v1.3.7\n\n本地发布说明");

        Assert(VersionChecker.TryReadPackagedReleaseNotes(tempDirectory, out var content)
               && content.Contains("本地发布说明", StringComparison.Ordinal),
            "validated update package release notes were not read locally");

        File.WriteAllText(Path.Combine(resourceDirectory, "Release.md"), "placeholder");
        Assert(!VersionChecker.TryReadPackagedReleaseNotes(tempDirectory, out _),
            "placeholder package release notes were accepted");
    }
    finally
    {
        if (Directory.Exists(tempDirectory))
            Directory.Delete(tempDirectory, true);
    }
}

static HttpResponseMessage GitHubResponse(HttpRequestMessage request, HttpStatusCode status, string body,
    Uri? effectiveUri = null, int? remaining = null, string? reasonPhrase = null)
{
    var response = new HttpResponseMessage(status)
    {
        RequestMessage = new HttpRequestMessage(request.Method, effectiveUri ?? request.RequestUri),
        Content = new StringContent(body, Encoding.UTF8, "text/html"),
    };
    response.ReasonPhrase = reasonPhrase;
    if (remaining.HasValue)
        response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", remaining.Value.ToString());
    return response;
}

static async Task GitHubRateLimitFallsBackToStableWebReleaseAsync()
{
    var handler = new GitHubRouteHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.Forbidden, "", reasonPhrase: "rate limit exceeded");
        if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK, "", new Uri("https://github.com/owner/repo/releases/tag/v1.3.7"));
        if (path.EndsWith("/releases/expanded_assets/v1.3.7", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK,
                "<a href=\"/other/repo/releases/download/v1.3.7/foreign-win-x64-update.zip\">foreign</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.6/stale-win-x64-update.zip\">stale</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64-update.zip\">archive</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64-update.zip.sha256\">update sha</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64.zip\">full</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64.zip.sha256\">full sha</a>");
        if (path.EndsWith(".sha256", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK, new string('a', 64));
        throw new InvalidOperationException($"unexpected GitHub fallback request: {request.RequestUri}");
    });
    using var client = new HttpClient(handler);
    var result = await VersionChecker.GetLatestVersionAndDownloadUrlFromGithubAsync(
        "owner", "repo", true, currentVersion: "v1.3.6", httpClient: client,
        versionTypeOverride: VersionChecker.VersionType.Stable, webFallbackHttpClient: client);

    Assert(result.latestVersion == "v1.3.7", $"unexpected fallback version: {result.latestVersion}");
    Assert(result.url.EndsWith("MaaBanGDream-v1.3.7-win-x64.zip", StringComparison.Ordinal),
        $"unexpected fallback archive: {result.url}");
    Assert(result.sha256 == new string('a', 64), "fallback did not use the exact archive sidecar");
    Assert(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)),
        "rate-limited GitHub API did not use the stable web-release fallback");
}

static async Task GitHubTagRateLimitFallsBackToExpandedAssetsAsync()
{
    var handler = new GitHubRouteHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases", StringComparison.Ordinal))
        {
            if (request.RequestUri.Query.Contains("page=2", StringComparison.Ordinal))
                return GitHubResponse(request, HttpStatusCode.OK, "[]");
            return GitHubResponse(request, HttpStatusCode.OK,
                "[{\"tag_name\":\"v1.3.7\",\"prerelease\":false,\"body\":\"\"}]");
        }
        if (path.EndsWith("/releases/tags/v1.3.7", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.Forbidden, "API rate limit exceeded", remaining: 0);
        if (path.EndsWith("/releases/expanded_assets/v1.3.7", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK,
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64-update.zip\">archive</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64-update.zip.sha256\">update sha</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64.zip\">full</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64.zip.sha256\">full sha</a>");
        if (path.EndsWith(".sha256", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK, new string('b', 64));
        throw new InvalidOperationException($"unexpected GitHub tag fallback request: {request.RequestUri}");
    });
    using var client = new HttpClient(handler);
    var result = await VersionChecker.GetLatestVersionAndDownloadUrlFromGithubAsync(
        "owner", "repo", false, currentVersion: "v1.3.6", httpClient: client,
        versionTypeOverride: VersionChecker.VersionType.Stable, webFallbackHttpClient: client);

    Assert(result.latestVersion == "v1.3.7", "tag fallback changed the selected version");
    Assert(result.sha256 == new string('b', 64), "tag fallback did not validate the exact sidecar");
    Assert(handler.Requests.Any(uri => uri.AbsolutePath.EndsWith("/releases/expanded_assets/v1.3.7", StringComparison.Ordinal)),
        "rate-limited tag API did not use the expanded-assets fallback");
}

static async Task GitHubOrdinaryForbiddenDoesNotFallBackAsync()
{
    var handler = new GitHubRouteHandler(request =>
        GitHubResponse(request, HttpStatusCode.Forbidden, "bad credentials"));
    using var client = new HttpClient(handler);
    try
    {
        await VersionChecker.GetLatestVersionAndDownloadUrlFromGithubAsync(
            "owner", "repo", true, httpClient: client,
            versionTypeOverride: VersionChecker.VersionType.Stable, webFallbackHttpClient: client);
        throw new InvalidOperationException("ordinary forbidden response was accepted");
    }
    catch (Exception ex) when (ex.Message.Contains("403", StringComparison.Ordinal))
    {
    }
    Assert(handler.Requests.All(uri => !uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)),
        "ordinary forbidden response must not use the public web fallback");
}

static async Task GitHubNonStableChannelsDoNotFallBackAsync()
{
    foreach (var channel in new[] { VersionChecker.VersionType.Beta, VersionChecker.VersionType.Alpha })
    {
        var handler = new GitHubRouteHandler(request =>
            GitHubResponse(request, HttpStatusCode.Forbidden, "rate limit exceeded", remaining: 0));
        using var client = new HttpClient(handler);
        try
        {
            await VersionChecker.GetLatestVersionAndDownloadUrlFromGithubAsync(
                "owner", "repo", true, httpClient: client, versionTypeOverride: channel,
                webFallbackHttpClient: client);
            throw new InvalidOperationException($"{channel} channel unexpectedly used a stable fallback");
        }
        catch (Exception ex) when (ex.Message.Contains("403", StringComparison.Ordinal))
        {
        }
        Assert(handler.Requests.All(uri => !uri.AbsolutePath.EndsWith("/releases/latest", StringComparison.Ordinal)),
            $"{channel} channel must not use the stable web fallback");
    }
}

static async Task GitHubWebFallbackRequiresExactShaSidecarAsync()
{
    var handler = new GitHubRouteHandler(request =>
    {
        var path = request.RequestUri!.AbsolutePath;
        if (path.EndsWith("/releases", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.TooManyRequests, "rate limit exceeded");
        if (path.EndsWith("/releases/latest", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK, "", new Uri("https://github.com/owner/repo/releases/tag/v1.3.7"));
        if (path.EndsWith("/releases/expanded_assets/v1.3.7", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK,
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64.zip\">archive</a>" +
                "<a href=\"/owner/repo/releases/download/v1.3.7/MaaBanGDream-v1.3.7-win-x64.zip.sha256\">invalid sidecar</a>");
        if (path.EndsWith(".sha256", StringComparison.Ordinal))
            return GitHubResponse(request, HttpStatusCode.OK, "not a sha256 checksum");
        throw new InvalidOperationException($"unexpected sidecar request: {request.RequestUri}");
    });
    using var client = new HttpClient(handler);
    try
    {
        await VersionChecker.GetLatestVersionAndDownloadUrlFromGithubAsync(
            "owner", "repo", true, httpClient: client,
            versionTypeOverride: VersionChecker.VersionType.Stable, webFallbackHttpClient: client);
        throw new InvalidOperationException("429 fallback accepted a non-matching sidecar");
    }
    catch (Exception ex) when (ex.Message.Contains("SHA256", StringComparison.Ordinal))
    {
    }
}

if (args.Contains("--power-lifecycle"))
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("Power lifecycle tests skipped: Windows native power requests required");
        return;
    }
    // 使用真实 UI 线程约束；无平台的 Dispatcher 会掩盖跨线程释放错误。
    AppBuilder.Configure<Application>().UseWin32().UseSkia().SetupWithoutStarting();
    AppPaths.Initialize();
    var originalWorkingDirectory = Environment.CurrentDirectory;
    try
    {
        // MaaFramework 按工作目录生成调试配置，限定在被忽略的测试输出目录。
        Environment.CurrentDirectory = AppPaths.TempDirectory;
        PreventSleepTracksTasksAndReleasesNativeState();
    }
    finally
    {
        Environment.CurrentDirectory = originalWorkingDirectory;
    }
    Console.WriteLine("Windows task-scoped power lifecycle tests passed (including real task queue and native flags)");
    return;
}

AdbEndpointGuardKeepsLdInputOnSelectedProcess();
AdbEndpointGuardReadsWindowsPortOwner();
await CatalogMaintenanceTests.RunAsync();
await DebouncesToLatestChangeAsync();
await SerializesChangesArrivingDuringSaveAsync();
await RetriesOnceAsync();
await ReportsTerminalFailureAfterRetryAsync();
await CancelPreventsPendingSaveAsync();
RuntimeOptionsIncludeProcessCleanupSwitch();
NativeExperimentalOptionsKeepBooleanAndSaveBoundaries();
CooperativeLoadingGuardIsVisibleAndPreservesAutoSaveBoundary();
await CooperativeLoadingGuardPersistsThroughProfileManagerAsync();
ProfileCurrentSelectionMarkerIsIndependentFromGridSelection();
ProfileSelectionRequestUsesTaskDifficulty();
CalibrationRecordsReadNestedSessionResults();
ChartCatalogSummaryIsReadable();
ApplicationBrandingUsesProjectName();
await AboutMetadataMergeAndLateLoadAsync();
NativeGitHubUpdaterSelectsPortablePackageSafely();
NativeGitHubUpdaterRejectsAmbiguousArchives();
GitHubOnlyResourcesCoerceLegacyMirrorSelection();
await NativeDownloaderResumesAndRestartsSafelyAsync();
await NativeDownloaderRejectsShaMismatchAsync();
await GitHubRateLimitFallsBackToStableWebReleaseAsync();
await GitHubTagRateLimitFallsBackToExpandedAssetsAsync();
await GitHubOrdinaryForbiddenDoesNotFallBackAsync();
await GitHubNonStableChannelsDoNotFallBackAsync();
await GitHubWebFallbackRequiresExactShaSidecarAsync();
await GitHubReleaseNotesUseExactTagAndWebFallbackAsync();
await GitHubLatestReleaseNotesIncludeLatestTagAsync();
PackagedReleaseNotesAreReadLocally();
await AnnouncementsCacheAndContentChangesAsync();
await CatalogMaintenanceTests.GlobalShutdownAsync();
Console.WriteLine("MFA auto-save tests passed (including About metadata and native GitHub updater coverage)");

sealed class GitHubRouteHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri ?? new Uri("https://invalid.local/"));
        return Task.FromResult(responder(request));
    }
}

static class NativePowerTest
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    public static extern uint SetThreadExecutionState(uint flags);
}
