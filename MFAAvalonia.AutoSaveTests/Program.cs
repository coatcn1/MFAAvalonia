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
        options["native_realtime_enabled"]?.Type == JTokenType.Boolean
        && options.Value<bool>("native_realtime_enabled") == false,
        "native realtime option must be captured and default to false");
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

await DebouncesToLatestChangeAsync();
await SerializesChangesArrivingDuringSaveAsync();
await RetriesOnceAsync();
await ReportsTerminalFailureAfterRetryAsync();
await CancelPreventsPendingSaveAsync();
RuntimeOptionsIncludeProcessCleanupSwitch();
CalibrationRecordsReadNestedSessionResults();
ChartCatalogSummaryIsReadable();
ApplicationBrandingUsesProjectName();
NativeGitHubUpdaterSelectsPortablePackageSafely();
NativeGitHubUpdaterRejectsAmbiguousArchives();
GitHubOnlyResourcesCoerceLegacyMirrorSelection();
await NativeDownloaderResumesAndRestartsSafelyAsync();
await NativeDownloaderRejectsShaMismatchAsync();
Console.WriteLine("MFA auto-save tests passed: 16 (including native GitHub updater download semantics)");
