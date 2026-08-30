using MFAAvalonia.ViewModels.UsersControls.Settings;
using Newtonsoft.Json.Linq;
using System.Reflection;

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
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
        options.Value<int>("note_skin_type") == 1,
        "note skin type must default to TYPE1");
    Assert(
        options.Value<int>("tap_effect") == 1,
        "tap effect must default to 1");
    Assert(
        options.Value<bool>("judgement_assist_effect"),
        "judgement assist must default to enabled");
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
}

await DebouncesToLatestChangeAsync();
await SerializesChangesArrivingDuringSaveAsync();
await RetriesOnceAsync();
await ReportsTerminalFailureAfterRetryAsync();
await CancelPreventsPendingSaveAsync();
RuntimeOptionsIncludeProcessCleanupSwitch();
CalibrationRecordsReadNestedSessionResults();
ChartCatalogSummaryIsReadable();
ApplicationBrandingUsesProjectName();
Console.WriteLine("MFA auto-save tests passed: 9 (including chart catalog status and branding)");
