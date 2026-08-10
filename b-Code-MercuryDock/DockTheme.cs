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
    /// <c>Shell.Brush.Surface</c>(#FFFFFF) 与 <c>Shell.Brush.AccentSoft</c>(#FAF0D8) 浅色值。</summary>
    public static Color TileBase => Color.FromRgb(0xFF, 0xFF, 0xFF);

    /// <summary>磁贴底色偏黄端。</summary>
    public static Color TileTintTarget => Color.FromRgb(0xFA, 0xF0, 0xD8);

    /// <summary>磁贴文字：深黄，取自 UI 文档浅色 <c>Shell.Brush.Accent</c>(#A87A12)。
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
    public static FontFamily FontFamily => Find("Shell.Font.Family") as FontFamily ?? new FontFamily("Microsoft YaHei UI, Segoe UI");
    public static double BodyFontSize => FindDouble("Shell.Font.Body", 13);
    public static double SmallFontSize => FindDouble("Shell.Font.Small", 11);
    public static Thickness ControlPadding => Find("Shell.Space.ControlPad") is Thickness thickness
        ? thickness
        : new Thickness(10, 4, 10, 4);

    /// <summary>
    /// 输入框内边距：28 高控件若沿用 (10,4) 垂直内边距，13 号字行高加光标留白会被裁掉半行；
    /// 垂直压到 0，水平仍跟宿主；按钮等无光标控件继续用 ControlPadding。
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

    private static object? Find(string key)
        => Application.Current?.TryFindResource(key);

    private static double FindDouble(string key, double fallback)
        => Find(key) is double value && !double.IsNaN(value) && !double.IsInfinity(value) ? value : fallback;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
