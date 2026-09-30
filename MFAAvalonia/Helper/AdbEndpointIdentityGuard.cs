using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MFAAvalonia.Helper;

public sealed class AdbTargetMismatchException(string message) : Exception(message);

public readonly record struct AdbTcpListener(IPAddress Address, int Port, int ProcessId);

public static class AdbEndpointIdentityGuard
{
    public const string EnableEnvironmentVariable = "MFA_VERIFY_ADB_ENDPOINT";
    private const int AddressFamilyInterNetwork = 2;
    private const int TcpTableOwnerPidListener = 3;
    private const int InsufficientBuffer = 122;

    public static bool IsEnabled => Environment.GetEnvironmentVariable(EnableEnvironmentVariable) == "1";

    public static void EnsureSelectedTarget(string serial, string config, bool enabled)
    {
        if (!enabled || !OperatingSystem.IsWindows()
            || !TryGetLdTarget(serial, config, out var port, out var pid))
            return;

        var problem = CheckLdEndpoint(port, pid, ReadWindowsTcpListeners());
        if (problem != null)
            throw new AdbTargetMismatchException(problem);
    }

    public static string? CheckLdEndpoint(int port, int expectedPid, IEnumerable<AdbTcpListener> listeners)
    {
        var candidates = listeners.Where(listener => listener.Port == port).ToArray();
        // Windows 优先选择精确回环绑定；它会遮蔽同端口的 0.0.0.0 绑定。
        var routed = candidates.Where(listener => listener.Address.Equals(IPAddress.Loopback)).ToArray();
        if (routed.Length == 0)
            routed = candidates.Where(listener => listener.Address.Equals(IPAddress.Any)).ToArray();

        if (routed.Length == 1 && routed[0].ProcessId == expectedPid)
            return null;

        return $"所选雷电模拟器的 ADB 端口 {port} 未连接到该雷电进程。为避免误操作，已阻止连接。请关闭占用同端口的模拟器，或为雷电设置独立 ADB 端口后重新选择设备。";
    }

    public static bool TryGetLdTarget(string serial, string config, out int port, out int pid)
    {
        port = 0;
        pid = 0;
        if (!serial.StartsWith("emulator-", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(serial.AsSpan("emulator-".Length), out var consolePort)
            || consolePort < 1 || consolePort >= 65535)
            return false;

        try
        {
            using var document = JsonDocument.Parse(config);
            var root = document.RootElement;
            if (!root.TryGetProperty("extras", out var extras)
                || !extras.TryGetProperty("ld", out var ld)
                || !ld.TryGetProperty("enable", out var enabled)
                || enabled.ValueKind != JsonValueKind.True)
                return false;

            if (ld.TryGetProperty("pid", out var processId))
                processId.TryGetInt32(out pid);
            port = consolePort + 1;
            return true;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static IReadOnlyList<AdbTcpListener> ReadWindowsTcpListeners()
    {
        var size = 0;
        var status = GetExtendedTcpTable(IntPtr.Zero, ref size, false,
            AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0);
        if (status != InsufficientBuffer)
            throw new AdbTargetMismatchException("无法核验模拟器 ADB 端口归属，已阻止连接。");

        var table = Marshal.AllocHGlobal(size);
        try
        {
            status = GetExtendedTcpTable(table, ref size, false,
                AddressFamilyInterNetwork, TcpTableOwnerPidListener, 0);
            if (status != 0)
                throw new AdbTargetMismatchException("无法核验模拟器 ADB 端口归属，已阻止连接。");

            var count = Marshal.ReadInt32(table);
            var listeners = new List<AdbTcpListener>(count);
            const int rowSize = 24;
            for (var index = 0; index < count; index++)
            {
                var row = IntPtr.Add(table, 4 + index * rowSize);
                var address = new IPAddress(BitConverter.GetBytes(Marshal.ReadInt32(row, 4)));
                var port = (ushort)IPAddress.NetworkToHostOrder((short)Marshal.ReadInt32(row, 8));
                var pid = Marshal.ReadInt32(row, 20);
                listeners.Add(new AdbTcpListener(address, port, pid));
            }
            return listeners;
        }
        finally
        {
            Marshal.FreeHGlobal(table);
        }
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(IntPtr tcpTable, ref int size, bool order,
        int addressFamily, int tableClass, uint reserved);
}
