using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mercury;

/// <summary>打开或唤起 HistoryVulcan 主界面。</summary>
internal static class HistoryVulcanLauncher
{
    private const int ShowRestore = 9;

    /// <summary>返回是否唤起了已存在的前端；false 表示启动了新的前端。</summary>
    public static bool Open()
    {
        var current = Process.GetCurrentProcess();
        Process[] peers;
        try
        {
            peers = Process.GetProcessesByName(current.ProcessName);
        }
        catch (Exception)
        {
            peers = [];
        }

        foreach (var peer in peers)
        {
            try
            {
                if (peer.Id == current.Id)
                    continue;
                var window = peer.MainWindowHandle;
                if (window == IntPtr.Zero)
                    continue;
                if (IsIconic(window))
                    ShowWindow(window, ShowRestore);
                SetForegroundWindow(window);
                return true;
            }
            catch (Exception)
            {
                // The peer can exit while the process list is being enumerated.
            }
        }

        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            return false;

        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Exception)
        {
            return false;
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
}
