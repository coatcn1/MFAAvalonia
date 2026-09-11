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
    Assert(
        options["life_safety_enabled"] == null
        && options["life_exit_threshold"] == null
        && options["rehearsal_ignore_life_safety"] == null,
        "removed life protection options must not be persisted");
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

await DebouncesToLatestChangeAsync();
await SerializesChangesArrivingDuringSaveAsync();
await RetriesOnceAsync();
await ReportsTerminalFailureAfterRetryAsync();
await CancelPreventsPendingSaveAsync();
RuntimeOptionsIncludeProcessCleanupSwitch();
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
