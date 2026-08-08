using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MercuryDock;

/// <summary>
/// 让活动坞永不遮挡其他窗口：保持普通顶层窗口，但把 z-order 钉死在最底。
/// </summary>
/// <remarks>
/// 1.1.0/1.1.1 曾把窗口重定父到承载壁纸的 WorkerW，视觉上确实成为桌面的一部分，
/// Win+D 时随桌面露出。但壁纸层位于 SHELLDLL_DefView(桌面图标层)之下，DefView 覆盖整屏并吞掉
/// 桌面区域的全部鼠标输入，贴附后的窗口画得出来却收不到点击，项目图标与尺寸调整全部失效。
/// 该限制是 Explorer 的层次决定的，无法在不放弃交互的前提下绕开，因此 1.1.2 起放弃重定父。
///
/// 现方案：WS_EX_NOACTIVATE 不抢焦点，并在每次 WM_WINDOWPOSCHANGING 时把 hwndInsertAfter
/// 强制改为 HWND_BOTTOM。窗口因此始终位于所有普通窗口之下，同时保留完整的鼠标输入。
/// 代价是 Win+D 时它跟普通窗口一起被隐藏。
/// </remarks>
public static class DesktopLayer
{
    private const int GwlExStyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint MonitorDefaultToPrimary = 0x00000001;

    private static readonly IntPtr HwndBottom = new(1);
    private static readonly object Gate = new();

    /// <summary>准备窗口：不抢焦点、不进任务栏切换列表，并立即压到最底。</summary>
    public static void Prepare(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;
        var style = GetWindowLong(hwnd, GwlExStyle);
        SetWindowLong(hwnd, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        PushToBottom(hwnd);
        Log("已置为桌面底层窗口(可点击，不遮挡其他窗口)");
    }

    /// <summary>把窗口压回 z-order 最底。</summary>
    public static void PushToBottom(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// 在 Win32 的同一坐标系中把窗口贴合到主显示器工作区右下角。
    /// </summary>
    /// <remarks>
    /// WPF 的 <c>SystemParameters.WorkArea</c> 使用 DIPs，而无感知/系统感知宿主中的
    /// 顶层 HWND 位置可能仍按物理像素解释。两者混用会在 150% 等缩放下留下明显空白。
    /// 这里让工作区、窗口矩形与 SetWindowPos 全部走同一组 User32 API，避免坐标系漂移。
    /// </remarks>
    public static bool AnchorToPrimaryBottomRight(IntPtr hwnd, int margin)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var window))
            return false;

        var monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
        if (monitor == IntPtr.Zero)
            return false;

        var monitorInfo = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref monitorInfo))
            return false;

        var width = window.Right - window.Left;
        var height = window.Bottom - window.Top;
        var left = monitorInfo.WorkArea.Right - width - margin;
        var top = monitorInfo.WorkArea.Bottom - height - margin;
        return SetWindowPos(hwnd, HwndBottom, left, top, 0, 0, SwpNoSize | SwpNoActivate);
    }

    /// <summary>
    /// 在 WM_WINDOWPOSCHANGING 里把插入位置改写为 HWND_BOTTOM。
    /// 相比在 WM_WINDOWPOSCHANGED 里再调一次 SetWindowPos，这里不会引发递归。
    /// </summary>
    public static void PinToBottom(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return;
        var position = Marshal.PtrToStructure<WindowPos>(lParam);
        position.InsertAfter = HwndBottom;
        position.Flags &= ~SwpNoZOrder;
        Marshal.StructureToPtr(position, lParam, false);
    }

    /// <summary>取窗口自身的屏幕物理矩形，命中测试以此为基准。</summary>
    public static bool TryGetWindowRect(IntPtr hwnd, out double left, out double top, out double width, out double height)
    {
        left = top = width = height = 0;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect))
            return false;
        left = rect.Left;
        top = rect.Top;
        width = rect.Right - rect.Left;
        height = rect.Bottom - rect.Top;
        return true;
    }

    private static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                var root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OneHistoryStudio",
                    "MercuryDock");
                Directory.CreateDirectory(root);
                File.AppendAllText(
                    Path.Combine(root, "layer.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // 日志失败不得影响界面。
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect MonitorArea;
        public Rect WorkArea;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Window;
        public IntPtr InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value);

    private static int GetWindowLong(IntPtr handle, int index) => (int)GetWindowLongPtr(handle, index);

    private static void SetWindowLong(IntPtr handle, int index, int value)
        => SetWindowLongPtr(handle, index, new IntPtr(value));
}
