using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MercuryDock;

/// <summary>
/// 打开 OHS 主界面。
/// </summary>
/// <remarks>
/// OHS 是单入口产品：桌面前端与服务宿主同为一个 OneHistoryStudio.exe，靠 --service-host 区分。
/// 活动坞运行在服务进程内，因此 Environment.ProcessPath 就是要启动的目标，不需要任何路径配置。
/// 已有前端时唤到前台而不是再开一个；识别前端时必须排除本进程，否则会把服务宿主自己当成前端
/// （服务宿主持有活动坞窗口，MainWindowHandle 并不为空）。
/// </remarks>
internal static class OhsLauncher
{
    private const int ShowRestore = 9;

    /// <summary>返回是否唤起了已有前端；false 表示新启动了一个。</summary>
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
                // 进程可能在枚举后退出，忽略并继续。
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
