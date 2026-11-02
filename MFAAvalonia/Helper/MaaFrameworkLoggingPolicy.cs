using MaaFramework.Binding;
using MFAAvalonia.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MFAAvalonia.Helper;

public sealed record MaaFrameworkStartupOptions(bool LoggingEnabled, bool SaveDraw, bool SaveOnError,
    int StdoutLevel, int DrawQuality, bool DebugMode);

public static class MaaFrameworkLoggingPolicy
{
    public const string AgentEnvironmentVariable = "MAABANGDREAM_MAAFW_LOGGING";
    private static readonly object SaveLock = new();
    private static readonly MaaFrameworkStartupOptions Startup = Capture(ConfigurationManager.Maa.Config, Warn);

    // 显式类型初始化保证第一次读取设置时即冻结，界面保存不能改变本进程的日志策略。
    static MaaFrameworkLoggingPolicy() { }

    public static bool StartupLoggingEnabled => Startup.LoggingEnabled;

    private static void Warn(string message)
    {
        try { LoggerHelper.Warning(message); }
        catch { /* 附加日志失败不能阻止框架初始化。 */ }
    }

    public static MaaFrameworkStartupOptions Capture(IReadOnlyDictionary<string, object> config,
        Action<string>? warning = null)
    {
        void Invalid(string key)
        {
            try { warning?.Invoke($"MaaFramework 配置 {key} 无效，使用安全默认值"); }
            catch { /* 警告通道不参与配置和输入完整性判定。 */ }
        }
        bool Boolean(string key, bool fallback)
        {
            if (!config.TryGetValue(key, out var raw)) return fallback;
            if (raw is bool value) return value;
            if (raw is JValue { Type: JTokenType.Boolean } token) return token.Value<bool>();
            Invalid(key);
            return fallback;
        }
        int Integer(string key, int fallback, int maximum)
        {
            if (!config.TryGetValue(key, out var raw)) return fallback;
            long? number = null;
            try
            {
                number = raw switch
                {
                    int value => value,
                    long value => value,
                    JValue { Type: JTokenType.Integer } token => token.Value<long>(),
                    _ => null,
                };
            }
            catch { /* 超大整数或不可转换的配置仅回退，不中断框架启动。 */ }
            if (number is >= 0 && number <= maximum) return (int)number.Value;
            Invalid(key);
            return fallback;
        }
        return new MaaFrameworkStartupOptions(
            Boolean(ConfigurationKeys.FrameworkLogging, false),
            Boolean(ConfigurationKeys.SaveDraw, false),
            Boolean(ConfigurationKeys.SaveOnError, true),
            Integer("stdout_level", 2, 7), Integer("draw_quality", 85, 100),
            Boolean(ConfigurationKeys.ShowHitDraw, false));
    }

    public static MaaToolkit CreateToolkit(string dataRoot)
    {
        // InitOption 会先按旧 logging 值开启无法热关闭的日志流，改为仅应用已有全局选项。
        var toolkit = new MaaToolkit(false);
        ApplyStartupOptions(new MaaGlobal(), dataRoot, Startup);
        return toolkit;
    }

    public static void ApplyStartupOptions(MaaGlobal global, string dataRoot,
        MaaFrameworkStartupOptions options)
    {
        Apply(() => global.SetOption_LogDir(options.LoggingEnabled ? Path.Combine(dataRoot, "debug") : string.Empty), "日志目录");
        // 空日志目录仍会让 SDK 将调试图写到工作目录，关闭时只屏蔽运行态，不改保存值。
        Apply(() => global.SetOption_SaveDraw(options.LoggingEnabled && options.SaveDraw), "识别调试图");
        Apply(() => global.SetOption(GlobalOption.SaveOnError, options.LoggingEnabled && options.SaveOnError), "错误截图");
        Apply(() => global.SetOption_StdoutLevel((LoggingLevel)options.StdoutLevel), "控制台日志等级");
        Apply(() => global.SetOption(GlobalOption.DrawQuality, options.DrawQuality), "调试图质量");
        Apply(() => global.SetOption_DebugMode(options.DebugMode), "内存调试信息");
    }

    private static void Apply(Func<bool> apply, string name)
    {
        try
        {
            if (!apply()) Warn($"MaaFramework 未接受{name}设置，继续运行");
        }
        catch (Exception ex) { Warn($"MaaFramework {name}设置失败：{ex.GetType().Name}，继续运行"); }
    }

    public static bool ShouldSaveDebugFile(bool savedValue) => Startup.LoggingEnabled && savedValue;

    public static void ApplyAgentEnvironment(ProcessStartInfo startInfo) =>
        startInfo.Environment[AgentEnvironmentVariable] = Startup.LoggingEnabled ? "1" : "0";

    public static bool ReadSavedLogging(MFAConfiguration configuration) =>
        Capture(configuration.Config, Warn).LoggingEnabled;

    public static void SaveLoggingSetting(MFAConfiguration configuration, bool enabled, string? path = null)
    {
        lock (SaveLock)
        {
            path ??= Path.Combine(AppPaths.ConfigDirectory, "maa_option.json");
            // 读取失败时拒绝覆盖；只合并本次用户修改，保留未知字段和其他显式选项。
            JObject content;
            try
            {
                var bytes = File.ReadAllBytes(path);
                var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
                // 手动接受 UTF-8 BOM，避免自动编码检测改用宽容解码并损坏未知字段。
                content = JObject.Parse(new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset));
            }
            catch (FileNotFoundException) { content = JObject.FromObject(configuration.Config); }
            catch (DirectoryNotFoundException) { content = JObject.FromObject(configuration.Config); }
            content[ConfigurationKeys.FrameworkLogging] = enabled;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $".maa-option-{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporary, content.ToString(Formatting.Indented), new UTF8Encoding(false));
                File.Move(temporary, path, true);
                configuration.SetConfig(content.ToObject<Dictionary<string, object>>()!);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }
    }
}
