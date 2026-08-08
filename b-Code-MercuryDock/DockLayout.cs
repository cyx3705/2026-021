using System.Windows;

namespace MercuryDock;

/// <summary>
/// 活动坞的纯布局规则：锚点、尺寸钳制与边框命中测试。
/// 不依赖窗口实例，便于 Smoke 直接断言。
/// </summary>
public static class DockLayout
{
    /// <summary>距工作区右下角的留白。</summary>
    public const double Margin = 16;

    /// <summary>可拖拽调整的边框宽度。</summary>
    public const double BorderWidth = 6;

    public const double MinWidth = 240;
    public const double MaxWidth = 720;
    public const double MinHeight = 112;
    public const double MaxHeight = 390;
    public const double DefaultWidth = 360;
    public const double DefaultHeight = 200;

    /// <summary>交回默认处理，即该点不可用于调整尺寸。</summary>
    public const int HitNone = 0;

    // 与 Win32 WM_NCHITTEST 的 HTLEFT / HTTOP / HTTOPLEFT 对齐。
    public const int HitLeft = 10;
    public const int HitTop = 12;
    public const int HitTopLeft = 13;

    /// <summary>把窗口吸附到工作区右下角；右下角是锚点，尺寸变化只影响左上。</summary>
    public static (double Left, double Top) Anchor(Rect workArea, double width, double height)
        => (workArea.Right - width - Margin, workArea.Bottom - height - Margin);

    public static double ClampWidth(double value) => Clamp(value, MinWidth, MaxWidth, DefaultWidth);

    public static double ClampHeight(double value) => Clamp(value, MinHeight, MaxHeight, DefaultHeight);

    /// <summary>
    /// 只在左边框、上边框和左上角返回调整代码；右边框、下边框与其余三角一律交回默认，
    /// 因此右下角在调整过程中天然不动，不需要补偿位置。
    /// </summary>
    public static int HitTest(double x, double y, double width, double height, double border = BorderWidth)
    {
        if (x < 0 || y < 0 || x > width || y > height)
            return HitNone;

        var left = x < border;
        var top = y < border;
        if (left && top)
            return HitTopLeft;
        if (left)
            return HitLeft;
        return top ? HitTop : HitNone;
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            return fallback;
        return Math.Min(Math.Max(value, min), max);
    }
}
