using System.Reflection;
using System.Diagnostics;

internal static class UpdaterTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Apply(string source, string target)
    {
        typeof(Program).GetMethod("Main", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
            [new[] { "--no-progress", "--apply-package", source, "--target", target,
                "--parent-pid", int.MaxValue.ToString(), "--launcher", "启动 MaaBanGDream.cmd", "--manifest", "update-manifest.json" }]);
    }

    private static void RestartRenameKeepsLogsAtNewRoot(string root, bool fail)
    {
        var parent = Path.Combine(root, fail ? "rename-failure" : "rename-success");
        var source = Path.Combine(parent, "package");
        var target = Path.Combine(parent, "MaaBanGDream-v0.0.0-win-x64");
        Directory.CreateDirectory(Path.Combine(source, "scripts"));
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(source, "update-manifest.json"), "{\"version\":\"9.9.9\"}");
        File.WriteAllText(Path.Combine(source, "启动 MaaBanGDream.cmd"), "@exit /b 99");
        var restart = """
                      param([string]$ResultPath)
                      $root = Split-Path -Parent $PSScriptRoot
                      $parent = Split-Path -Parent $root
                      Set-Location -LiteralPath $parent
                      Rename-Item -LiteralPath $root -NewName 'MaaBanGDream-v9.9.9-win-x64'
                      $newRoot = Join-Path $parent 'MaaBanGDream-v9.9.9-win-x64'
                      $result = @{ install_root = $newRoot } | ConvertTo-Json -Compress
                      [IO.File]::WriteAllText($ResultPath, $result, [Text.UTF8Encoding]::new($false))
                      """;
        File.WriteAllText(Path.Combine(source, "scripts", "restart-release.ps1"), restart + (fail ? "\nexit 17" : "\nexit 0"));
        Environment.ExitCode = 0;
        Apply(source, target);
        var renamed = Path.Combine(parent, "MaaBanGDream-v9.9.9-win-x64");
        Assert(Environment.ExitCode == (fail ? 1 : 0), "renamed restart result was lost");
        Assert(!Directory.Exists(target), "updater logs recreated the old install directory");
        var log = Path.Combine(renamed, "logs", "updater_log.txt");
        Assert(File.Exists(log), "updater log was not saved in the renamed directory");
        var logDirectory = (string)typeof(Program).GetMethod("GetLogDirectory", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
        Assert(logDirectory == Path.GetDirectoryName(log), "failure log action still points to the old directory");
        Assert(Directory.Exists(source) == fail, "renamed restart discarded failure evidence or retained successful extraction");
    }

    public static void Main()
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(AppContext.BaseDirectory, "更新 测试 ' " + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(root, "package");
            var target = Path.Combine(root, "install");
            Directory.CreateDirectory(source);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(source, "update-manifest.json"), "{\"version\":\"9.9.9\"}");
            File.WriteAllText(Path.Combine(source, "启动 MaaBanGDream.cmd"), "@exit /b 99");
            Directory.CreateDirectory(Path.Combine(source, "scripts"));
            File.WriteAllText(Path.Combine(source, "scripts", "restart-release.ps1"), "$root = Split-Path -Parent $PSScriptRoot; [IO.File]::WriteAllText((Join-Path $root 'started.txt'), 'ready'); exit 0");
            File.WriteAllText(Path.Combine(source, "payload.txt"), "new");
            foreach (var name in new[] { "config", "profiles", "debug", "logs", "screencap", ".maabangdream-backup" })
            {
                Directory.CreateDirectory(Path.Combine(source, name));
                Directory.CreateDirectory(Path.Combine(target, name));
                File.WriteAllText(Path.Combine(source, name, "user.txt"), "overwrite");
                File.WriteAllText(Path.Combine(target, name, "user.txt"), "keep");
            }
            Directory.CreateDirectory(Path.Combine(source, "runtime", "python"));
            Directory.CreateDirectory(Path.Combine(target, "runtime", "python"));
            File.WriteAllText(Path.Combine(target, "runtime", "python", "python.exe"), "existing");
            File.WriteAllText(Path.Combine(source, "runtime", "python", "python.exe"), "bad replacement");
            Apply(source, target);
            Assert(Environment.ExitCode == 0, "portable update failed");
            Assert(File.Exists(Path.Combine(target, "started.txt")), "hidden PowerShell did not restart from the Unicode path");
            Assert(File.ReadAllText(Path.Combine(target, "payload.txt")) == "new", "payload was not applied");
            foreach (var name in new[] { "config", "profiles", "debug", "logs", "screencap", ".maabangdream-backup" })
                Assert(File.ReadAllText(Path.Combine(target, name, "user.txt")) == "keep", "user directory was replaced: " + name);
            Assert(File.ReadAllText(Path.Combine(target, "runtime", "python", "python.exe")) == "existing", "existing Python runtime was replaced");
            Assert(!Directory.Exists(source), "successful update did not clean extraction directory");

            var wait = typeof(Program).GetMethod("WaitForMainProcessExitStrict", BindingFlags.NonPublic | BindingFlags.Static)!;
            try
            {
                wait.Invoke(null, [Environment.ProcessId, TimeSpan.FromMilliseconds(30)]);
                throw new InvalidOperationException("live parent was not rejected");
            }
            catch (TargetInvocationException ex) when (ex.InnerException is TimeoutException) { }
            Assert(!Process.GetCurrentProcess().HasExited, "strict wait killed the live parent");

            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "update-manifest.json"), "new marker");
            File.WriteAllText(Path.Combine(source, "启动 MaaBanGDream.cmd"), "@exit /b 99");
            Directory.CreateDirectory(Path.Combine(source, "scripts"));
            File.WriteAllText(Path.Combine(source, "scripts", "restart-release.ps1"), "exit 17");
            Apply(source, target);
            Assert(Environment.ExitCode == 1, "restart failure was reported as success");
            Assert(Directory.Exists(source), "failed restart discarded extraction evidence");
            Assert(File.ReadAllText(Path.Combine(target, "logs", "updater_log.txt")).Contains("重新启动失败"), "restart failure was not logged");
            RestartRenameKeepsLogsAtNewRoot(root, false);
            RestartRenameKeepsLogsAtNewRoot(root, true);
            Console.WriteLine("Updater tests passed: Unicode restart, protected directories, strict parent wait, restart failure, renamed log paths");
            Environment.ExitCode = 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
