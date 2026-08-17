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
            peers = Process.GetProcessesByName("HistoryVulcan");
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

    internal static string? ResolveExecutable()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("MERCURY_VULCAN_EXECUTABLE"),
            Path.Combine(AppContext.BaseDirectory, ExecutableName),
            Environment.ProcessPath,
        };

        var assemblyDirectory = Path.GetDirectoryName(typeof(HistoryVulcanLauncher).Assembly.Location);
        if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            candidates.Add(Path.Combine(assemblyDirectory, ExecutableName));

        foreach (var root in Ancestors(Environment.CurrentDirectory)
                     .Concat(Ancestors(assemblyDirectory)))
        {
            candidates.Add(Path.Combine(
                root,
                "2026-023-HistoryVulcan",
                "z-Publish",
                "host",
                ExecutableName));
        }

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!))
            .FirstOrDefault(path =>
                Path.GetFileName(path).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(path));
    }

    private static IEnumerable<string> Ancestors(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            yield break;
        DirectoryInfo? current;
        try
        {
            current = new DirectoryInfo(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            yield break;
        }
        while (current != null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
}
