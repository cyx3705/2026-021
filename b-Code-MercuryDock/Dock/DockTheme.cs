using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Mercury;

/// <summary>
/// HistoryMercury 的视觉令牌。管理页与桌面坞只有深色模式：颜色不解析宿主主题令牌，
/// 恒用深色色板，宿主处于浅色模式时页面也不会变白。字体与间距与深浅无关，继续跟随宿主。
/// </summary>
public static class DockTheme
{
    public static SolidColorBrush PanelBackground => Frozen(Color.FromRgb(0x1D, 0x20, 0x1F));
    public static SolidColorBrush PanelBorder => Frozen(Color.FromRgb(0x34, 0x37, 0x36));
    public static SolidColorBrush Label => Frozen(Color.FromRgb(0xE2, 0xDA, 0xC6));
    public static SolidColorBrush Muted => Frozen(Color.FromRgb(0xAC, 0xA5, 0x93));
    public static SolidColorBrush Hover => Frozen(Color.FromRgb(0x2A, 0x2D, 0x2C));
    public static SolidColorBrush Pressed => Frozen(Color.FromRgb(0x34, 0x37, 0x36));
    public static SolidColorBrush SurfaceAlt => Frozen(Color.FromRgb(0x24, 0x26, 0x25));
    public static SolidColorBrush AccentSoft => Frozen(Color.FromRgb(0x5A, 0x48, 0x24));
    public static SolidColorBrush Accent => Frozen(Color.FromRgb(0xD9, 0xA4, 0x41));
    public static SolidColorBrush AccentHover => Frozen(Color.FromRgb(0xE8, 0xB6, 0x5C));
    public static SolidColorBrush TextOnAccent => Frozen(Color.FromRgb(0x17, 0x19, 0x18));

    /// <summary>列表选中色：淡黄底配深色文字，深底上醒目但不刺眼。</summary>
    public static SolidColorBrush Selection => Frozen(Color.FromRgb(0xEA, 0xDC, 0x9E));
    public static SolidColorBrush SelectionText => TextOnAccent;
    /// <summary>磁贴底色的两端：纯白 → 柔和强调背景。取自宿主 UI 文档的
    /// <c>Aurora.Brush.Surface</c>(#FFFFFF) 与 <c>Aurora.Brush.AccentSoft</c>(#FAF0D8) 浅色值。</summary>
    public static Color TileBase => Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>磁贴底色偏黄端。</summary>
    public static Color TileTintTarget => Color.FromRgb(0xFA, 0xF0, 0xD8);

    /// <summary>磁贴文字：深黄，取自 UI 文档浅色 <c>Aurora.Brush.Accent</c>(#A87A12)。
    /// 白底与 #FAF0D8 底上对比度都足够。</summary>
    public static SolidColorBrush TileText => Frozen(Color.FromRgb(0xA8, 0x7A, 0x12));

    /// <summary>按偏黄程度插值出磁贴底色画刷；<paramref name="tint"/> 取 [0,1]。</summary>
    public static SolidColorBrush TileBackground(double tint)
    {
        var t = Math.Clamp(tint, 0, 1);
        return Frozen(Color.FromRgb(
            (byte)Math.Round(TileBase.R + ((TileTintTarget.R - TileBase.R) * t)),
            (byte)Math.Round(TileBase.G + ((TileTintTarget.G - TileBase.G) * t)),
            (byte)Math.Round(TileBase.B + ((TileTintTarget.B - TileBase.B) * t))));
    }
    /// <summary>
    /// 字体与间距。取值与前端令牌 <c>Aurora.Font.*</c> / <c>Aurora.Space.ControlPad</c> 一致，
    /// 但**自己拿着**，不去查前端。
    /// </summary>
    /// <remarks>
    /// 原实现走 <c>Application.Current.TryFindResource</c>。桌面坞跑在自己的 STA 线程上，
    /// 前端在宿主进程内开窗时也刻意不建 WPF Application，这条查询因此恒为 null，
    /// 一直落在下面这些字面量上——写成"跟随宿主"只是错觉。坞是桌面上的常驻面，
    /// 它在前端起来之前就要显示，本来也不该跟着前端的主题走。
    /// </remarks>
    public static FontFamily FontFamily => new("Microsoft YaHei UI, Segoe UI");

    public static double BodyFontSize => 13;

    public static double SmallFontSize => 11;

    public static Thickness ControlPadding => new(10, 4, 10, 4);

    /// <summary>
    /// 输入框内边距：28 高控件若沿用 (10,4) 垂直内边距，13 号字行高加光标留白会被裁掉半行；
    /// 垂直压到 0，水平仍与按钮等无光标控件的 ControlPadding 对齐。
    /// </summary>
    public static Thickness InputPadding => new(ControlPadding.Left, 0, ControlPadding.Right, 0);

    public static void StyleButton(Button button, bool accent = false)
    {
        button.Height = 28;
        button.Padding = ControlPadding;
        button.FontFamily = FontFamily;
        button.FontSize = BodyFontSize;
        button.Foreground = accent ? TextOnAccent : Label;
        button.Background = accent ? Accent : SurfaceAlt;
        button.BorderBrush = accent ? Accent : PanelBorder;
        button.BorderThickness = new Thickness(1);

        var template = new ControlTemplate(typeof(Button));
        var surface = new FrameworkElementFactory(typeof(Border), "Surface");
        surface.SetValue(Border.BackgroundProperty, accent ? Accent : SurfaceAlt);
        surface.SetValue(Border.BorderBrushProperty, accent ? Accent : PanelBorder);
        surface.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        surface.SetValue(Border.PaddingProperty, ControlPadding);
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        surface.AppendChild(content);
        template.VisualTree = surface;

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, accent ? AccentHover : Hover, "Surface"));
        template.Triggers.Add(hover);
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty, Pressed, "Surface"));
        template.Triggers.Add(pressed);
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.55, "Surface"));
        template.Triggers.Add(disabled);
        button.Template = template;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
