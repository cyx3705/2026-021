using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using HistoryVulcan.Core.Commands;

namespace Mercury;

/// <summary>打开或唤起 HistoryVulcan 主界面。</summary>
internal static class HistoryVulcanLauncher
{
    private const int ShowRestore = 9;
    private const string ExecutableName = "HistoryVulcan.exe";

    public static CommandResult Open(string arguments = "--show")
    {
        Process[] peers;
        try
        {
            peers = ResolveExecutable() is { } hostExecutable
                ? Process.GetProcessesByName(Path.GetFileNameWithoutExtension(hostExecutable))
                : [];
        }
        catch (Exception)
        {
            peers = [];
        }

        foreach (var peer in peers)
        {
            try
            {
                if (peer.Id == Environment.ProcessId)
                    continue;
                var window = peer.MainWindowHandle;
                if (window == IntPtr.Zero)
                    continue;
                if (IsIconic(window))
                    ShowWindow(window, ShowRestore);
                SetForegroundWindow(window);
                return CommandResult.Ok("已激活正在运行的 HistoryVulcan 前端。");
            }
            catch (Exception)
            {
                // The peer can exit while the process list is being enumerated.
            }
        }

        var executable = ResolveExecutable();
        if (executable == null)
            return CommandResult.Fail("找不到正式 HistoryVulcan.exe，无法启动前端。");

        try
        {
            Process.Start(new ProcessStartInfo(executable)
            {
                UseShellExecute = true,
                Arguments = arguments,
            });
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Fail($"启动 HistoryVulcan 前端失败：{ex.Message}");
        }

        return CommandResult.Ok("HistoryVulcan 前端正在启动。");
    }

    /// <summary>
    /// 宿主 5.9.0 起由 <c>vulcan.host.info</c> 报告正式服务程序的位置（Attach 时取到）；
    /// 不再按固定目录名或进程名猜。环境变量覆盖只给烟测用。
    /// </summary>
    internal static string? ResolveExecutable()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("MERCURY_VULCAN_EXECUTABLE"),
            MercuryPaths.HostExecutable,
        };
        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(File.Exists);
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
}
