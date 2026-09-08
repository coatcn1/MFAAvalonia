using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>从 GitHub Releases 整包更新 MaaBanGDream 便携包。</summary>
/// <remarks>
/// 不再对发布 zip 做逐文件 Range 增量：整包下载到 temp/update 下的
/// <c>.part</c>（支持断点续传），下载完成后与发布附带的
/// <c>.zip.sha256</c> 校验一致，再写重启脚本让进程退出后覆盖解压。
/// 本地版本以 update-manifest.json 为准——它只在一次完整应用成功后才被
/// 替换，中断的半更新状态不会把版本误报成新版本。
/// </remarks>
public sealed class GitHubReleaseUpdater
{
    public const string Repository = "coatcn1/MaaBanGDream";

    private static readonly HttpClient Http = CreateClient(TimeSpan.FromSeconds(30));
    // 大文件下载不能套 60 秒总超时：慢速网络下整包可能要几十分钟。
    // 只给元数据请求短超时，下载体请求无总超时、由取消令牌和操作系统
    // 套接字超时兜底。
    private static readonly HttpClient DownloadHttp =
        CreateClient(Timeout.InfiniteTimeSpan);
    private readonly string _root;

    public GitHubReleaseUpdater(string packageRoot)
    {
        _root = packageRoot;
    }

    private static HttpClient CreateClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MaaBanGDream-Updater");
        return client;
    }

    public string? LocalVersion { get; private set; }

    public sealed record LatestRelease(string Tag, string Version);

    /// <summary>最新 Release 的公告内容（tag、标题与简介 Markdown）。</summary>
    public sealed record ReleaseAnnouncement(string Tag, string Name, string Body);

    /// <summary>
    /// 本地版本以 update-manifest.json 为准（最后一次完整应用成功才会写入），
    /// 找不到时依次回退 BUILD-INFO.json、interface.json，兼容旧版便携包。
    /// </summary>
    public string? ReadLocalVersion()
    {
        foreach (var relative in new[]
        {
            "update-manifest.json",
            "BUILD-INFO.json",
            "interface.json",
        })
        {
            var path = Path.Combine(_root, relative);
            if (!File.Exists(path))
            {
                continue;
            }
            try
            {
                var payload = JObject.Parse(File.ReadAllText(path, Encoding.UTF8));
                var version = payload["version"]?.ToString();
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return version;
                }
            }
            catch
            {
                // 文件损坏时继续尝试下一个来源。
            }
        }
        return null;
    }

    /// <summary>通过 releases/latest 的 302 重定向拿最新 tag，绕开 GitHub API 限流。</summary>
    public async Task<LatestRelease?> CheckLatestAsync(CancellationToken ct)
    {
        LocalVersion = ReadLocalVersion();
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://github.com/{Repository}/releases/latest");
        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        var finalUri = response.RequestMessage?.RequestUri;
        var match = Regex.Match(
            finalUri?.AbsoluteUri ?? string.Empty,
            @"/releases/tag/(?<tag>[^/?#]+)");
        if (!match.Success)
        {
            return null;
        }

        var tag = match.Groups["tag"].Value;
        var version = tag.TrimStart('v', 'V');
        return new LatestRelease(tag, version);
    }

    /// <summary>读取最新 Release 的 tag 与简介正文，用于主页更新公告。</summary>
    /// <remarks>
    /// 走未认证的 GitHub REST API（每 IP 每小时 60 次）；启动时只调用一次，
    /// 失败时静默跳过公告，不影响更新检查与下载。
    /// </remarks>
    public async Task<ReleaseAnnouncement?> FetchLatestAnnouncementAsync(
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        var payload = JObject.Parse(text);
        var tag = payload["tag_name"]?.ToString();
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }
        return new ReleaseAnnouncement(
            tag,
            payload["name"]?.ToString() ?? tag,
            payload["body"]?.ToString() ?? string.Empty);
    }

    public static bool IsNewer(string? latest, string? local)
    {
        if (string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(local))
        {
            return true;
        }
        if (!SemVersion.TryParse(Normalize(latest), SemVersionStyles.Any, out var newer) ||
            !SemVersion.TryParse(Normalize(local), SemVersionStyles.Any, out var older))
        {
            return false;
        }
        return newer.ComparePrecedenceTo(older) > 0;
    }

    private static string Normalize(string value)
    {
        var text = value.Trim().TrimStart('v', 'V');
        var core = text.Split('-')[0];
        var parts = core.Split('.');
        var normalized = new System.Collections.Generic.List<string>(parts);
        while (normalized.Count < 3)
        {
            normalized.Add("0");
        }
        return string.Join(".", normalized.Take(3));
    }

    private static string AssetUrl(string tag, string assetName) =>
        $"https://github.com/{Repository}/releases/download/{tag}/{assetName}";

    /// <summary>下载完整发布包；目标文件写入 &lt;root&gt;/temp/update/&lt;name&gt;.zip。</summary>
    /// <returns>已通过 SHA256 校验的本地 zip 路径。</returns>
    public async Task<string> DownloadAsync(
        LatestRelease release,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var assetName = $"MaaBanGDream-v{release.Version}-win-x64.zip";
        var zipUrl = AssetUrl(release.Tag, assetName);
        var shaUrl = AssetUrl(release.Tag, assetName + ".sha256");

        progress?.Report("读取校验信息…");
        var expectedSha256 = await FetchSha256Async(shaUrl, ct);

        var updateDirectory = Path.Combine(_root, "temp", "update");
        Directory.CreateDirectory(updateDirectory);
        var partPath = Path.Combine(updateDirectory, assetName + ".part");
        var zipPath = Path.Combine(updateDirectory, assetName);

        // 清理其它版本的残留下载，只保留本次目标。
        foreach (var stale in Directory.GetFiles(updateDirectory, "*.zip*"))
        {
            var name = Path.GetFileName(stale);
            if (!name.StartsWith(assetName, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(stale); } catch { }
            }
        }

        await DownloadWithResumeAsync(
            zipUrl,
            partPath,
            progress,
            ct);

        progress?.Report("校验下载文件…");
        string actualSha256;
        using (var hashStream = File.OpenRead(partPath))
        {
            actualSha256 = Convert.ToHexString(
                SHA256.HashData(hashStream)).ToLowerInvariant();
        }
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partPath);
            throw new InvalidDataException(
                "下载文件校验失败，已删除不完整文件；请重试，将重新下载。");
        }

        File.Move(partPath, zipPath, overwrite: true);
        return zipPath;
    }

    /// <summary>
    /// 带断点续传的下载。已存在的 .part 从断点继续；服务器不支持 Range、
    /// 或断点与服务器长度不一致时回退成重新下载。写入 .part 保证中断后
    /// 下次重试可以接着下，而不是从头再来。
    /// </summary>
    private async Task DownloadWithResumeAsync(
        string url,
        string partPath,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        long existing = File.Exists(partPath)
            ? new FileInfo(partPath).Length
            : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
        }

        using var response = await DownloadHttp.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        FileMode mode;
        long startOffset;
        long? totalLength;
        switch (response.StatusCode)
        {
            case HttpStatusCode.PartialContent:
                {
                    var range = response.Content.Headers.ContentRange;
                    if (range?.From != existing || range.Length < existing)
                    {
                        // 断点与服务器实际区间不一致（例如 .part 已损坏或被
                        // 截断），丢弃旧文件重新完整下载。
                        response.Dispose();
                        File.Delete(partPath);
                        existing = 0;
                        request.Dispose();
                        await DownloadWithResumeAsync(url, partPath, progress, ct);
                        return;
                    }
                    mode = FileMode.Append;
                    startOffset = existing;
                    totalLength = range.Length;
                    break;
                }
            case HttpStatusCode.RequestedRangeNotSatisfiable:
                // .part 已经等于服务器长度（上次其实已下完）：直接复用，
                // 由调用方做 SHA256 校验。
                return;
            case HttpStatusCode.OK:
                mode = FileMode.Create;
                startOffset = 0;
                totalLength = response.Content.Headers.ContentLength;
                break;
            default:
                response.EnsureSuccessStatusCode();
                throw new HttpRequestException("无法连接 GitHub 下载更新包。");
        }

        if (totalLength is { } alreadyComplete && alreadyComplete == startOffset)
        {
            // 已完整：由调用方校验后决定是否复用。
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        // 杀毒软件等可能短暂占用刚写过的 .part；共享冲突时短暂等待重试。
        FileStream file;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                file = new FileStream(
                    partPath,
                    mode,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 1024 * 1024,
                    useAsync: true);
                break;
            }
            catch (IOException) when (attempt < 15)
            {
                await Task.Delay(600, ct);
            }
        }
        await using (file)
        {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer, ct);
            if (read == 0)
            {
                break;
            }
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            copied += read;
            if (totalLength is { } totalBytes)
            {
                var received = startOffset + copied;
                var percent = totalBytes == 0 ? 0 : received * 100.0 / totalBytes;
                progress?.Report(
                    $"下载中 {percent:0.0}%（{received / 1048576.0:0.0}/" +
                    $"{totalBytes / 1048576.0:0.0} MB，支持断点续传）");
            }
            else
            {
                progress?.Report($"下载中 {(startOffset + copied) / 1048576.0:0.0} MB…");
            }
        }

        if (totalLength is { } expectedBytes && startOffset + copied != expectedBytes)
        {
            throw new IOException(
                $"下载不完整：收到 {startOffset + copied} 字节，期望 {expectedBytes} 字节。");
        }
        }
    }

    private async Task<string> FetchSha256Async(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(ct);
        var match = Regex.Match(text, @"\b([0-9a-fA-F]{64})\b");
        if (!match.Success)
        {
            throw new InvalidDataException("发布包缺少可解析的 SHA256 校验值。");
        }
        return match.Groups[1].Value.ToLowerInvariant();
    }

    /// <summary>写重启脚本并以独立进程启动：进程退出后覆盖解压并重启应用。</summary>
    public void ScheduleRestart(string zipPath)
    {
        var scriptPath = Path.Combine(_root, "update-restart.ps1");
        var launcher = Path.Combine(_root, "启动 MaaBanGDream.cmd");
        var rootLiteral = _root.Replace("'", "''");
        var zipLiteral = zipPath.Replace("'", "''");
        var launcherLiteral = launcher.Replace("'", "''");

        var script = new StringBuilder();
        script.AppendLine("param([int]$TargetPid, [string]$ZipPath)");
        script.AppendLine("$log = Join-Path $env:TEMP 'maabangdream-update-restart.log'");
        script.AppendLine("\"$(Get-Date -Format o) start target=$TargetPid zip=$ZipPath\" | Out-File -Append $log -Encoding utf8");
        script.AppendLine("while (Get-Process -Id $TargetPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 800 }");
        script.AppendLine("\"$(Get-Date -Format o) process exited\" | Out-File -Append $log -Encoding utf8");
        script.AppendLine($"$root = '{rootLiteral}'");
        script.AppendLine($"$zipPath = '{zipLiteral}'");
        script.AppendLine("$staging = Join-Path $root 'temp\\update\\staging'");
        script.AppendLine("Add-Type -AssemblyName System.IO.Compression.FileSystem");
        script.AppendLine("if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }");
        script.AppendLine("New-Item -ItemType Directory -Force -Path $staging | Out-Null");
        script.AppendLine("[System.IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $staging)");
        script.AppendLine("\"$(Get-Date -Format o) extracted\" | Out-File -Append $log -Encoding utf8");
        script.AppendLine("$inner = Get-ChildItem -LiteralPath $staging -Directory | Select-Object -First 1");
        script.AppendLine("if ($inner) { $staging = $inner.FullName }");
        script.AppendLine("if (-not (Test-Path -LiteralPath (Join-Path $staging 'MFAAvalonia.exe'))) { throw '更新包结构异常：缺少 MFAAvalonia.exe' }");
        script.AppendLine("$preserve = @('config', 'profiles', 'debug', 'logs', 'screencap', '.maabangdream-backup')");
        script.AppendLine("foreach ($attempt in 1..5) {");
        script.AppendLine("  $failed = $false");
        script.AppendLine("  Get-ChildItem -LiteralPath $staging -Force | ForEach-Object {");
        script.AppendLine("    if ($_.Name -in $preserve) { return }");
        script.AppendLine("    try { Copy-Item -LiteralPath $_.FullName -Destination $root -Recurse -Force -ErrorAction Stop }");
        script.AppendLine("    catch { $failed = $true; Start-Sleep -Milliseconds 600 }");
        script.AppendLine("  }");
        script.AppendLine("  if (-not $failed) { break }");
        script.AppendLine("}");
        script.AppendLine("\"$(Get-Date -Format o) overlay done\" | Out-File -Append $log -Encoding utf8");
        script.AppendLine("Remove-Item -LiteralPath $zipPath -Force -ErrorAction SilentlyContinue");
        script.AppendLine("Remove-Item -LiteralPath (Join-Path $root 'temp\\update\\staging') -Recurse -Force -ErrorAction SilentlyContinue");
        script.AppendLine("\"$(Get-Date -Format o) launching $root\" | Out-File -Append $log -Encoding utf8");
        script.AppendLine(
            $"Start-Process -FilePath 'cmd.exe' " +
            $"-WorkingDirectory '{rootLiteral}' " +
            $"-ArgumentList '/c','\"{launcherLiteral}\"'");
        script.AppendLine("\"$(Get-Date -Format o) launch issued\" | Out-File -Append $log -Encoding utf8");
        // 自删必须放在最后：Windows PowerShell 按需读取脚本文件，先删
        // 自己会丢掉随后的启动器重启行。
        script.AppendLine("Remove-Item -LiteralPath (Join-Path $root 'update-restart.ps1') -Force -ErrorAction SilentlyContinue");
        // Windows PowerShell 5.1 对无 BOM 的 .ps1 按系统 ANSI(GBK) 解码，
        // 中文启动器文件名会被读成乱码导致 cmd /c 静默失败；必须写 BOM。
        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(true));
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments =
                $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden " +
                $"-File \"{scriptPath}\" -TargetPid {Environment.ProcessId} " +
                $"-ZipPath \"{zipPath}\"",
            WorkingDirectory = _root,
            UseShellExecute = false,
            CreateNoWindow = true,
        });
    }
}
