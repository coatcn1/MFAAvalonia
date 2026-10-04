using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace MFAAvalonia.Helper;

public sealed record CacheCleanupFailure(string Path, string Reason);
public sealed record CacheCleanupResult(int DeletedFiles, long DeletedBytes, IReadOnlyList<CacheCleanupFailure> Failures)
{
    public string Describe()
    {
        var text = new StringBuilder($"已删除 {DeletedFiles} 个文件，释放 {DeletedBytes} 字节（{DeletedBytes / 1024.0 / 1024.0:F2} MiB）");
        if (Failures.Count > 0)
        {
            text.Append($"；{Failures.Count} 项未清理：");
            text.Append(string.Join("；", Failures.Take(5).Select(item => $"{item.Path}：{item.Reason}")));
            if (Failures.Count > 5) text.Append("；其余详情见日志");
        }
        else if (DeletedFiles == 0) text.Append("；没有可清理文件");
        return text.ToString();
    }
}

public static class CacheMaintenance
{
    private static readonly Dictionary<string, string[]> AllowedArtifacts = new()
    {
        ["realtime_recordings"] = ["debug", "recordings"],
        ["result_captures"] = ["screencap"],
        ["maafw_debug"] = ["debug"],
        ["mfa_logs"] = ["logs"]
    };

    public static IReadOnlyList<string> Plan(string dataRoot, JObject? artifactPaths)
    {
        var targets = new List<string>();
        foreach (var name in new[] { "debug", "logs", "screencap" })
            targets.Add(ValidateTarget(Path.Combine(dataRoot, name), [name]));
        foreach (var item in AllowedArtifacts)
        {
            if (artifactPaths?[item.Key] is not JToken value) continue;
            if (value.Type != JTokenType.String) throw new InvalidDataException($"缓存路径 {item.Key} 必须为字符串");
            targets.Add(ValidateTarget(value.Value<string>()!, item.Value));
        }
        // 父目录包含子目录时只走一次，确保删除数与释放大小不会重复。
        return targets.Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => !targets.Any(other => !path.Equals(other, StringComparison.OrdinalIgnoreCase)
                                                && IsChildOf(path, other))).ToArray();
    }

    private static string ValidateTarget(string path, string[] suffix)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("缓存目录必须是规范绝对路径");
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!full.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("缓存目录不能包含相对跳转");
        var cursor = new DirectoryInfo(full);
        for (var i = suffix.Length - 1; i >= 0; i--)
        {
            if (!cursor.Name.Equals(suffix[i], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("缓存目录末级布局不匹配白名单");
            cursor = cursor.Parent ?? throw new InvalidDataException("不能清理磁盘根目录");
        }
        foreach (var part in full.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
            if (part.Equals("config", StringComparison.OrdinalIgnoreCase)
                || part.Equals("profiles", StringComparison.OrdinalIgnoreCase)
                || part.Equals("charts", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("缓存目录不得位于 config、profiles 或 charts 中");
        RejectLinkedAncestors(full);
        return full;
    }

    private static bool IsChildOf(string path, string root) => path.StartsWith(
        root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RejectLinkedAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current != null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("拒绝清理重解析点或其子目录");
    }

    public static CacheCleanupResult Clean(string dataRoot, JObject? artifactPaths,
        TaskMaintenanceCoordinator coordinator, Func<bool> hasQueuedTasks)
    {
        using var lease = coordinator.TryBeginMaintenance(hasQueuedTasks)
                          ?? throw new InvalidOperationException("有实例任务正在运行、排队或正在维护，请稍后清理缓存");
        var targets = Plan(dataRoot, artifactPaths);
        var failures = new List<CacheCleanupFailure>();
        var files = 0;
        long bytes = 0;
        foreach (var target in targets)
        {
            if (!Directory.Exists(target)) continue;
            Walk(target, target, false);
        }
        return new CacheCleanupResult(files, bytes, failures);

        void Walk(string path, string root, bool removeDirectory)
        {
            try
            {
                if (!path.Equals(root, StringComparison.OrdinalIgnoreCase) && !IsChildOf(path, root))
                    throw new IOException("缓存项越过白名单边界");
                RejectLinkedAncestors(path);
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝清理重解析点");
                if (Directory.Exists(path))
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(path)) Walk(entry, root, true);
                    if (removeDirectory && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                }
                else
                {
                    var size = new FileInfo(path).Length;
                    File.Delete(path);
                    files++;
                    bytes += size;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add(new CacheCleanupFailure(path, ex.Message));
            }
        }
    }
}
