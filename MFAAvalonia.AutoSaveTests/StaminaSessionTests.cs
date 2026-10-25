using MFAAvalonia.Helper;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Text;

static class StaminaSessionTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void WriteChildReport()
    {
        var inherited = Environment.GetEnvironmentVariable(MaaBanGDreamAgentSession.EnvironmentVariableName);
        var childInfo = new ProcessStartInfo();
        MaaBanGDreamAgentSession.Apply(childInfo);
        Console.WriteLine(new JObject
        {
            ["inherited_session"] = inherited,
            ["process_session"] = MaaBanGDreamAgentSession.ProcessSessionId,
            ["next_agent_session"] = childInfo.Environment[MaaBanGDreamAgentSession.EnvironmentVariableName]
        }.ToString(Newtonsoft.Json.Formatting.None));
    }

    private static ProcessStartInfo CreateChild()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("test host path unavailable");
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            startInfo.ArgumentList.Add(typeof(StaminaSessionTests).Assembly.Location);
        startInfo.ArgumentList.Add("--stamina-session-child");
        return startInfo;
    }

    private static async Task<JObject> RunChildAsync(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("test child did not start");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            Assert(process.ExitCode == 0, $"test child failed: {await errors}");
            return JObject.Parse(await output);
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
        var name = MaaBanGDreamAgentSession.EnvironmentVariableName;
        var inheritedBeforeTest = Environment.GetEnvironmentVariable(name);
        var processSession = MaaBanGDreamAgentSession.ProcessSessionId;
        Assert(Guid.TryParseExact(processSession, "N", out _), "process session is not a random GUID identity");
        var children = new HashSet<string>();
        try
        {
            foreach (var inherited in new string?[] { null, "", "   ", "stale-session-from-old-mfa" })
            {
                Environment.SetEnvironmentVariable(name, inherited);
                var startInfo = CreateChild();
                // 模拟 PI 等前置环境构造也写入旧值，最终注入必须仍覆盖它。
                startInfo.Environment[name] = inherited ?? "stale-builder-value";
                MaaBanGDreamAgentSession.Apply(startInfo);
                MaaBanGDreamAgentSession.Apply(startInfo);
                Assert(startInfo.Environment[name] == processSession,
                    "Agent/reconnect did not reuse the current process session");
                var report = await RunChildAsync(startInfo);
                Assert((string?)report["inherited_session"] == processSession,
                    "real Agent child did not receive the explicitly injected session");
                var childSession = (string?)report["process_session"] ?? "";
                Assert(Guid.TryParseExact(childSession, "N", out _) && childSession != processSession,
                    "new MFA process trusted the inherited old process session");
                Assert(children.Add(childSession), "different OS processes reused the same session");
                Assert((string?)report["next_agent_session"] == childSession,
                    "new MFA process did not replace inherited history for its own Agents");
            }
            var directChild = CreateChild();
            directChild.Environment.Remove(name);
            var directReport = await RunChildAsync(directChild);
            Assert(directReport["inherited_session"]?.Type == JTokenType.Null,
                "missing-environment process unexpectedly inherited a session");
            Assert(Guid.TryParseExact((string?)directReport["process_session"], "N", out _)
                && (string?)directReport["process_session"] == (string?)directReport["next_agent_session"],
                "direct EXE startup without launcher did not bootstrap its Agent session");
        }
        finally { Environment.SetEnvironmentVariable(name, inheritedBeforeTest); }
    }
}
