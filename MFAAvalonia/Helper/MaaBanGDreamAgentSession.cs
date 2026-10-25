using System;
using System.Diagnostics;

namespace MFAAvalonia.Helper;

public static class MaaBanGDreamAgentSession
{
    public const string EnvironmentVariableName = "MAABANGDREAM_MFA_SESSION_ID";

    // 任务 ID 会跨进程复用；不能沿用启动器或旧 MFA 继承的会话值。
    public static string ProcessSessionId { get; } = Guid.NewGuid().ToString("N");

    public static void Apply(ProcessStartInfo startInfo)
    {
        // 同进程的所有实例、Agent 和重连共用身份，显式覆盖子进程环境。
        startInfo.Environment[EnvironmentVariableName] = ProcessSessionId;
    }
}
