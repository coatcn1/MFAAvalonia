using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Semver;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MFAAvalonia.Helper;

/// <summary>从 GitHub Releases 增量更新整个 MaaBanGDream 便携包。</summary>
/// <remarks>
/// 只下载“内容发生变化”的条目：发布包根目录带 `update-manifest.json`
/// （相对路径 → SHA256），更新器先按 HTTP Range 读取发布 zip 的中央目录和
/// 清单条目，与本地清单 diff 后仅拉取差异条目的字节区间并逐条校验，
/// 避免每次下载几百 MB 的本地谱面文件。
/// </remarks>
public sealed class GitHubReleaseUpdater
{
    public const string Repository = "coatcn1/MaaBanGDream";

    private static readonly HttpClient Http = CreateClient();
    private readonly string _root;

    public GitHubReleaseUpdater(string packageRoot)
    {
        _root = packageRoot;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(60),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MaaBanGDream-Updater");
        return client;
    }

    public string? LocalVersion { get; private set; }

    public sealed record LatestRelease(string Tag, string Version);

    public sealed record ZipEntry(
        string Path,
        long LocalHeaderOffset,
        long CompressedSize,
        long UncompressedSize,
        ushort Method,
        uint Crc32);

    /// <summary>本地版本来自包根目录 interface.json；找不到时回退 BUILD-INFO.json。</summary>
    public string? ReadLocalVersion()
    {
        var interfacePath = Path.Combine(_root, "interface.json");
        if (File.Exists(interfacePath))
        {
            try
            {
                var payload = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(interfacePath, Encoding.UTF8));
                if (payload?["version"]?.ToString() is { Length: > 0 } version)
                {
                    return version;
                }
            }
            catch
            {
                // 忽略并回退 BUILD-INFO.json。
            }
        }

        var buildInfoPath = Path.Combine(_root, "BUILD-INFO.json");
        if (!File.Exists(buildInfoPath))
        {
            return null;
        }

        try
        {
            var payload = JsonConvert.DeserializeObject<JObject>(File.ReadAllText(buildInfoPath, Encoding.UTF8));
            return payload?["version"]?.ToString();
        }
        catch
        {
            return null;
        }
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
        var match = System.Text.RegularExpressions.Regex.Match(
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

    /// <summary>比较 semver；latest 高于本地时返回 true。</summary>
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
        var normalized = new List<string>(parts);
        while (normalized.Count < 3)
        {
            normalized.Add("0");
        }

        return string.Join(".", normalized.Take(3));
    }

    public sealed class UpdatePlan
    {
        public required string Tag { get; init; }

        public required string Version { get; init; }

        public required string ZipUrl { get; init; }

        public required List<ZipEntry> Entries { get; init; }

        public required Dictionary<string, string> RemoteManifest { get; init; }

        public required string PackagePrefix { get; init; }
    }

    /// <summary>拉取 zip 中央目录 + 远端清单，与本地清单 diff 出要下载的条目。</summary>
    public async Task<UpdatePlan?> PlanAsync(
        LatestRelease release,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var zipUrl =
            $"https://github.com/{Repository}/releases/download/{release.Tag}/" +
            $"MaaBanGDream-v{release.Version}-win-x64.zip";
        var packagePrefix = $"MaaBanGDream-v{release.Version}-win-x64/";

        progress?.Report("正在读取发布包目录…");
        var entries = await ListZipEntriesAsync(zipUrl, ct);
        var manifestEntry = entries.FirstOrDefault(
            entry => NormalizeEntryPath(entry.Path, packagePrefix) == "update-manifest.json");
        if (manifestEntry is null)
        {
            progress?.Report("发布包缺少 update-manifest.json，无法增量更新。");
            return null;
        }

        var manifestBytes = await FetchEntryAsync(
            zipUrl,
            manifestEntry,
            progress,
            ct);
        var manifestText = Encoding.UTF8.GetString(manifestBytes);
        var remoteManifest =
            (JsonConvert.DeserializeObject<JObject>(manifestText)?["files"] ??
             new JObject()).ToObject<Dictionary<string, string>>() ??
            new Dictionary<string, string>();

        var localManifestPath = Path.Combine(_root, "update-manifest.json");
        var localManifest = new Dictionary<string, string>();
        if (File.Exists(localManifestPath))
        {
            try
            {
                localManifest =
                    (JsonConvert.DeserializeObject<JObject>(
                        File.ReadAllText(localManifestPath, Encoding.UTF8))?["files"] ??
                     new JObject()).ToObject<Dictionary<string, string>>() ??
                    new Dictionary<string, string>();
            }
            catch
            {
                // 本地清单损坏时按全量处理。
            }
        }

        var needed = new List<ZipEntry>();
        foreach (var entry in entries)
        {
            var relative = NormalizeEntryPath(entry.Path, packagePrefix);
            if (string.IsNullOrEmpty(relative) || relative.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            if (remoteManifest.TryGetValue(relative, out var remoteHash) &&
                localManifest.TryGetValue(relative, out var localHash) &&
                string.Equals(remoteHash, localHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            needed.Add(entry);
        }

        return new UpdatePlan
        {
            Tag = release.Tag,
            Version = release.Version,
            ZipUrl = zipUrl,
            Entries = needed,
            RemoteManifest = remoteManifest,
            PackagePrefix = packagePrefix,
        };
    }

    /// <summary>逐条下载并应用差异文件；锁定中的程序文件走 .new + 重启脚本。</summary>
    public async Task<bool> ApplyAsync(
        UpdatePlan plan,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var locked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "MFAAvalonia.exe",
            "MFAAvalonia.dll",
            "MFAAvalonia.Core.dll",
        };
        var applied = 0;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var relative = NormalizeEntryPath(entry.Path, plan.PackagePrefix);
            progress?.Report($"更新 {relative}（{applied + 1}/{plan.Entries.Count}）");
            var bytes = await FetchEntryAsync(plan.ZipUrl, entry, progress, ct);
            if (!plan.RemoteManifest.TryGetValue(relative, out var expectedHash))
            {
                continue;
            }

            var actualHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Report($"校验失败：{relative}");
                return false;
            }

            var destination = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
            var directory = Path.GetDirectoryName(destination);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var fileName = Path.GetFileName(destination);
            if (locked.Contains(fileName))
            {
                File.WriteAllBytes(destination + ".new", bytes);
            }
            else
            {
                var tempPath = destination + ".updating";
                File.WriteAllBytes(tempPath, bytes);
                File.Move(tempPath, destination, overwrite: true);
            }

            applied++;
        }

        var manifestPath = Path.Combine(_root, "update-manifest.json");
        var manifestPayload = JsonConvert.SerializeObject(
            new JObject
            {
                ["version"] = plan.Version,
                ["files"] = JObject.FromObject(
                    plan.RemoteManifest.OrderBy(pair => pair.Key)
                        .ToDictionary(pair => pair.Key, pair => pair.Value)),
            },
            Formatting.Indented);
        var manifestTemp = manifestPath + ".updating";
        File.WriteAllText(manifestTemp, manifestPayload, new UTF8Encoding(false));
        File.Move(manifestTemp, manifestPath, overwrite: true);
        return true;
    }

    /// <summary>写出重启脚本并让应用退出；脚本等进程结束后替换 .new 并重启。</summary>
    public void ScheduleRestart()
    {
        var scriptPath = Path.Combine(_root, "update-restart.cmd");
        var launcher = Path.Combine(_root, "启动 MaaBanGDream.cmd");
        var script = new StringBuilder();
        script.AppendLine("@echo off");
        script.AppendLine(":waitloop");
        script.AppendLine("tasklist /FI \"IMAGENAME eq MFAAvalonia.exe\" 2>NUL | find /I \"MFAAvalonia.exe\" >NUL");
        script.AppendLine("if %ERRORLEVEL%==0 (");
        script.AppendLine("  timeout /T 1 /NOBREAK >NUL");
        script.AppendLine("  goto waitloop");
        script.AppendLine(")");
        foreach (var name in new[] { "MFAAvalonia.exe", "MFAAvalonia.dll", "MFAAvalonia.Core.dll" })
        {
            script.AppendLine($"if exist \"{name}.new\" move /Y \"{name}.new\" \"{name}\"");
        }

        if (File.Exists(launcher))
        {
            script.AppendLine("start \"\" \"启动 MaaBanGDream.cmd\"");
        }
        else
        {
            script.AppendLine("start \"\" \"MFAAvalonia.exe\"");
        }

        script.AppendLine("del \"%~f0\"");
        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(false));
    }

    private static string NormalizeEntryPath(string entryPath, string prefix)
    {
        var normalized = entryPath.Replace('\\', '/');
        if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[prefix.Length..];
        }

        return normalized.TrimStart('/');
    }

    private async Task<List<ZipEntry>> ListZipEntriesAsync(
        string zipUrl,
        CancellationToken ct)
    {
        // 1) 先拿到总大小，再用显式区间拉取末尾 64KB 找 EOCD。
        // GitHub 的资产 CDN 不支持 `bytes=-N` 后缀区间，但支持显式起止区间。
        var totalSize = await GetContentLengthAsync(zipUrl, ct);
        var eocdStart = Math.Max(0, totalSize - 65557);
        var eocdBytes = await RangeReadAsync(
            zipUrl,
            eocdStart,
            totalSize - eocdStart,
            ct);
        var eocdIndex = FindSignature(eocdBytes, 0x06054b50);
        if (eocdIndex < 0)
        {
            throw new InvalidDataException("无法定位 ZIP 中央目录（EOCD）。");
        }

        var cdOffset = (long)BitConverter.ToUInt32(eocdBytes, eocdIndex + 16);
        var cdSize = (long)BitConverter.ToUInt32(eocdBytes, eocdIndex + 12);
        var cdCount = BitConverter.ToUInt16(eocdBytes, eocdIndex + 10);
        var cdBytes = await RangeReadAsync(zipUrl, cdOffset, cdSize, ct);

        var entries = new List<ZipEntry>(cdCount);
        var cursor = 0;
        for (var i = 0; i < cdCount; i++)
        {
            var signature = BitConverter.ToUInt32(cdBytes, cursor);
            if (signature != 0x02014b50)
            {
                throw new InvalidDataException("ZIP 中央目录条目签名异常。");
            }

            var method = BitConverter.ToUInt16(cdBytes, cursor + 10);
            var crc = BitConverter.ToUInt32(cdBytes, cursor + 16);
            var compressedSize = BitConverter.ToUInt32(cdBytes, cursor + 20);
            var uncompressedSize = BitConverter.ToUInt32(cdBytes, cursor + 24);
            var nameLength = BitConverter.ToUInt16(cdBytes, cursor + 28);
            var extraLength = BitConverter.ToUInt16(cdBytes, cursor + 30);
            var commentLength = BitConverter.ToUInt16(cdBytes, cursor + 32);
            var localHeaderOffset = BitConverter.ToUInt32(cdBytes, cursor + 42);
            var name = Encoding.UTF8.GetString(cdBytes, cursor + 46, nameLength);
            entries.Add(new ZipEntry(
                name,
                localHeaderOffset,
                compressedSize,
                uncompressedSize,
                method,
                crc));
            cursor += 46 + nameLength + extraLength + commentLength;
        }

        return entries;
    }

    private async Task<long> GetContentLengthAsync(string zipUrl, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, zipUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
        using var response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentRange is { HasLength: true } contentRange &&
            contentRange.Length.HasValue)
        {
            return contentRange.Length.Value;
        }

        if (response.Content.Headers.ContentLength is { } contentLength && contentLength > 0)
        {
            return contentLength;
        }

        throw new InvalidDataException("无法确定发布包大小。");
    }

    private async Task<byte[]> FetchEntryAsync(
        string zipUrl,
        ZipEntry entry,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        // 先取本地头（最多 512 字节）得到文件名长度，再按中央目录的压缩大小
        // 精确拉取条目数据，避免多下载相邻条目。
        var headerBytes = await RangeReadAsync(
            zipUrl,
            entry.LocalHeaderOffset,
            Math.Min(512, 64 * 1024 * 1024),
            ct);
        if (BitConverter.ToUInt32(headerBytes, 0) != 0x04034b50)
        {
            throw new InvalidDataException($"条目 {entry.Path} 本地头签名异常。");
        }

        var nameLength = BitConverter.ToUInt16(headerBytes, 26);
        var extraLength = BitConverter.ToUInt16(headerBytes, 28);
        var dataOffset = 30L + nameLength + extraLength;
        var compressed = await RangeReadAsync(
            zipUrl,
            entry.LocalHeaderOffset + dataOffset,
            entry.CompressedSize,
            ct);
        return entry.Method switch
        {
            0 => compressed,
            8 => Inflate(compressed, entry.UncompressedSize),
            _ => throw new InvalidDataException($"不支持的压缩方式：{entry.Method}"),
        };
    }

    private static byte[] Inflate(byte[] compressed, long expectedSize)
    {
        using var input = new MemoryStream(compressed, writable: false);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        deflate.CopyTo(output);
        var result = output.ToArray();
        if (expectedSize > 0 && result.LongLength != expectedSize)
        {
            throw new InvalidDataException("解压大小与 ZIP 目录记录不一致。");
        }

        return result;
    }

    private static async Task<byte[]> RangeReadAsync(
        string url,
        long? offset,
        long length,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (offset.HasValue)
        {
            var end = offset.Value + length - 1;
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                offset.Value,
                end);
        }
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output, ct);
        return output.ToArray();
    }

    private static int FindSignature(byte[] buffer, uint signature)
    {
        for (var i = buffer.Length - 4; i >= 0; i--)
        {
            if (BitConverter.ToUInt32(buffer, i) == signature)
            {
                return i;
            }
        }

        return -1;
    }
}
