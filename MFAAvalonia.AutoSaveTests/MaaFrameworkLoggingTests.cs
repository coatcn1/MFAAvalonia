using MaaFramework.Binding;
using MFAAvalonia.Configuration;
using MFAAvalonia.Helper;
using MFAAvalonia.ViewModels.UsersControls.Settings;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text;

static class MaaFrameworkLoggingTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static Dictionary<string, object> Options(bool? logging = null) => logging is null
        ? new() { ["save_draw"] = true, ["save_on_error"] = true, ["stdout_level"] = 2, ["draw_quality"] = 72,
            ["show_hit_box"] = true, ["unknown_option"] = new JObject { ["kept"] = 4 } }
        : new() { ["logging"] = logging.Value, ["save_draw"] = true, ["save_on_error"] = true,
            ["stdout_level"] = 2, ["draw_quality"] = 72, ["show_hit_box"] = true,
            ["unknown_option"] = new JObject { ["kept"] = 4 } };

    public static void WriteChildReport(string[] args)
    {
        var mode = args[1];
        var root = args[2];
        var reportPath = args[3];
        AppPaths.Initialize();
        ConfigurationManager.Maa.SetConfig(Options(mode == "missing" ? null : mode.StartsWith("on")));
        var startup = MaaFrameworkLoggingPolicy.StartupLoggingEnabled;
        MaaFrameworkLoggingPolicy.CreateToolkit(root);
        if (mode == "off-save-on" || mode == "on-save-off")
            MaaFrameworkLoggingPolicy.SaveLoggingSetting(ConfigurationManager.Maa, !startup,
                Path.Combine(root, "test-options.json"));
        var child = new ProcessStartInfo();
        child.Environment[MaaFrameworkLoggingPolicy.AgentEnvironmentVariable] = startup ? "0" : "1";
        MaaFrameworkLoggingPolicy.ApplyAgentEnvironment(child);
        var before = LogBytes(root);
        for (var index = 0; index < 50; index++)
        {
            using var resource = new MaaResource();
            resource.Clear();
        }
        var report = new JObject
        {
            ["startup"] = startup,
            ["agent"] = child.Environment[MaaFrameworkLoggingPolicy.AgentEnvironmentVariable],
            ["saved"] = MaaFrameworkLoggingPolicy.ReadSavedLogging(ConfigurationManager.Maa),
            ["draw_effective"] = MaaFrameworkLoggingPolicy.ShouldSaveDebugFile(true),
            ["saved_draw"] = (bool)ConfigurationManager.Maa.Config["save_draw"],
            ["before"] = before, ["after"] = LogBytes(root),
            ["root_vision"] = Directory.Exists(Path.Combine(root, "vision")),
            ["root_error"] = Directory.Exists(Path.Combine(root, "on_error")),
        };
        File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
    }

    private static long LogBytes(string root) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, "maafw*.log", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
        : 0;

    private static async Task<JObject> RunChildAsync(string mode, string root)
    {
        Directory.CreateDirectory(root);
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("test host unavailable");
        var report = Path.Combine(root, "report.json");
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(MaaFrameworkLoggingTests).Assembly.Location);
        foreach (var argument in new[] { "--framework-logging-child", mode, root, report })
            info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("logging child failed to start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert(process.ExitCode == 0, $"logging child {mode}: {await error}; {await output}");
            return JObject.Parse(await File.ReadAllTextAsync(report, timeout.Token));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    public static async Task RunAsync()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "logging-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var value in new object?[] { null, false, true, "false", 1, JValue.CreateNull() })
        {
            var data = Options();
            if (value is not null) data["logging"] = value;
            var snapshot = MaaFrameworkLoggingPolicy.Capture(data);
            Assert(snapshot.LoggingEnabled == (value is true), "logging must be a strict boolean defaulting off");
            Assert(snapshot.SaveDraw && snapshot.SaveOnError && snapshot.DebugMode
                && snapshot.StdoutLevel == 2 && snapshot.DrawQuality == 72, "unrelated options changed");
        }
        var bad = Options();
        bad["stdout_level"] = "7";
        bad["draw_quality"] = 101;
        var invalid = MaaFrameworkLoggingPolicy.Capture(bad);
        Assert(invalid.StdoutLevel == 2 && invalid.DrawQuality == 85, "invalid options bypassed validation");
        bad["draw_quality"] = new JValue(System.Numerics.BigInteger.Pow(10, 80));
        Assert(MaaFrameworkLoggingPolicy.Capture(bad).DrawQuality == 85, "oversized config integer escaped validation");

        foreach (var mode in new[] { "missing", "off", "on", "off-save-on", "on-save-off" })
        {
            var report = await RunChildAsync(mode, Path.Combine(root, mode));
            var active = mode.StartsWith("on");
            Assert(report.Value<bool>("startup") == active && report.Value<string>("agent") == (active ? "1" : "0"),
                $"host and child startup snapshots disagree: {mode}");
            Assert(report.Value<bool>("draw_effective") == active && report.Value<bool>("saved_draw"),
                $"runtime mask changed saved debug options: {mode}");
            Assert(active ? report.Value<long>("after") > report.Value<long>("before") : report.Value<long>("after") == 0,
                $"real SDK file logging policy failed: {mode}");
            Assert(!report.Value<bool>("root_vision") && !report.Value<bool>("root_error"), "debug files escaped their root");
            if (mode.Contains("save")) Assert(report.Value<bool>("saved") != active, "saved value did not change");
        }
        PersistenceAndViewModel(root);
        Console.WriteLine("MaaFramework logging tests passed (real SDK cold startup, child snapshot, persistence and debug-file mask)");
    }

    private static void PersistenceAndViewModel(string root)
    {
        var config = new MFAConfiguration("test", "maa_option", Options(true));
        var path = Path.Combine(root, "options.json");
        var original = JObject.FromObject(config.Config);
        original["unknown_option"]!["note"] = "中文·♪";
        File.WriteAllText(path, original.ToString(), new UTF8Encoding(true));
        MaaFrameworkLoggingPolicy.SaveLoggingSetting(config, false, path);
        var saved = JObject.Parse(File.ReadAllText(path));
        Assert(saved.Value<bool>("logging") == false && saved["unknown_option"]!.ToString() == original["unknown_option"]!.ToString(),
            "saving false lost the value or unknown fields");
        Assert(config.Config["logging"] is false && (bool)config.Config["save_draw"], "memory did not preserve the saved value");
        var bytes = "{invalid-json";
        File.WriteAllText(path, bytes);
        try
        {
            MaaFrameworkLoggingPolicy.SaveLoggingSetting(config, true, path);
            throw new InvalidOperationException("invalid JSON accepted");
        }
        catch (Newtonsoft.Json.JsonException) { }
        Assert(File.ReadAllText(path) == bytes && config.Config["logging"] is false, "failed read overwrote valid prior memory or disk");
        var invalidUtf8 = Encoding.ASCII.GetBytes("{\"logging\":false,\"unknown\":\"")
            .Concat(new byte[] { 0xff }).Concat(Encoding.ASCII.GetBytes("\"}")).ToArray();
        foreach (var bom in new[] { Array.Empty<byte>(), new byte[] { 0xef, 0xbb, 0xbf } })
        {
            var protectedBytes = bom.Concat(invalidUtf8).ToArray();
            File.WriteAllBytes(path, protectedBytes);
            try
            {
                MaaFrameworkLoggingPolicy.SaveLoggingSetting(config, true, path);
                throw new InvalidOperationException("invalid UTF-8 was accepted");
            }
            catch (DecoderFallbackException) { }
            Assert(File.ReadAllBytes(path).SequenceEqual(protectedBytes) && config.Config["logging"] is false,
                "invalid UTF-8 replacement changed unknown fields or memory");
        }
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, saved.ToString());
            var protectedBytes = File.ReadAllBytes(path);
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                try
                {
                    MaaFrameworkLoggingPolicy.SaveLoggingSetting(config, true, path);
                    throw new InvalidOperationException("locked file was replaced");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            Assert(File.ReadAllBytes(path).SequenceEqual(protectedBytes) && config.Config["logging"] is false,
                "failed replacement changed prior disk or memory");
        }
        Assert(!Directory.EnumerateFiles(root, ".maa-option-*.tmp").Any(), "failed setting write left a temporary file");

        var vmPath = Path.Combine(AppPaths.ConfigDirectory, "maa_option.json");
        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        var before = File.Exists(vmPath) ? File.ReadAllBytes(vmPath) : null;
        var memory = new Dictionary<string, object>(ConfigurationManager.Maa.Config);
        try
        {
            var initial = Options(true);
            File.WriteAllText(vmPath, JObject.FromObject(initial).ToString());
            ConfigurationManager.Maa.SetConfig(initial);
            var model = new GameSettingsUserControlModel();
            model.EnableFrameworkLogging = false;
            Assert(JObject.Parse(File.ReadAllText(vmPath)).Value<bool>("logging") == false, "UI false not saved");
            ConfigurationManager.Maa.SetValue(ConfigurationKeys.SaveDraw, true);
            Assert(JObject.Parse(File.ReadAllText(vmPath)).Value<bool>("logging") == false,
                "saving another option lost explicit false");
            Assert(new GameSettingsUserControlModel().EnableFrameworkLogging == false, "UI reload lost false");
            model.EnableFrameworkLogging = true;
            Assert(new GameSettingsUserControlModel().EnableFrameworkLogging, "UI reload lost true");
            Assert(ConfigurationManager.Maa.Config["save_draw"] is true, "master switch cleared saved draw option");
            File.WriteAllText(vmPath, bytes);
            model.EnableFrameworkLogging = false;
            Assert(model.EnableFrameworkLogging && model.FrameworkLoggingStatus.Contains("保存失败")
                && File.ReadAllText(vmPath) == bytes, "UI failed-save rollback lost the prior setting");
            File.WriteAllBytes(vmPath, invalidUtf8);
            model.EnableFrameworkLogging = false;
            Assert(model.EnableFrameworkLogging && model.FrameworkLoggingStatus.Contains("保存失败")
                && File.ReadAllBytes(vmPath).SequenceEqual(invalidUtf8), "UI invalid UTF-8 rollback overwrote prior bytes");
        }
        finally
        {
            ConfigurationManager.Maa.SetConfig(memory);
            if (before is null) File.Delete(vmPath);
            else File.WriteAllBytes(vmPath, before);
        }
    }
}
