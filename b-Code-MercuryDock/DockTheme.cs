using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MercuryDock;

/// <summary>MercuryDock 的 AppShell 视觉令牌适配层。</summary>
public static class DockTheme
{
    // Resolve AppShell tokens at control creation time. The fallback palette keeps
    // the module usable when loaded by a host without AppShell resource dictionaries.
    public static SolidColorBrush PanelBackground => Brush("Shell.Brush.Surface", Color.FromRgb(0x1D, 0x20, 0x1F));
    public static SolidColorBrush PanelBorder => Brush("Shell.Brush.ControlBorder", Color.FromRgb(0x34, 0x37, 0x36));
    public static SolidColorBrush Label => Brush("Shell.Brush.TextPrimary", Color.FromRgb(0xE2, 0xDA, 0xC6));
    public static SolidColorBrush Muted => Brush("Shell.Brush.TextSecondary", Color.FromRgb(0xAC, 0xA5, 0x93));
    public static SolidColorBrush Hover => Brush("Shell.Brush.SurfaceHover", Color.FromRgb(0x2A, 0x2D, 0x2C));
    public static SolidColorBrush Pressed => Brush("Shell.Brush.SurfacePressed", Color.FromRgb(0x34, 0x37, 0x36));
    public static SolidColorBrush SurfaceAlt => Brush("Shell.Brush.SurfaceAlt", Color.FromRgb(0x24, 0x26, 0x25));
    public static SolidColorBrush AccentSoft => Brush("Shell.Brush.AccentSoft", Color.FromRgb(0x5A, 0x48, 0x24));
    public static SolidColorBrush Accent => Brush("Shell.Brush.Accent", Color.FromRgb(0xD9, 0xA4, 0x41));
    public static SolidColorBrush AccentHover => Brush("Shell.Brush.AccentHover", Color.FromRgb(0xE8, 0xB6, 0x5C));
    public static SolidColorBrush TextOnAccent => Brush("Shell.Brush.TextOnAccent", Color.FromRgb(0x17, 0x19, 0x18));
    public static Color Glow => Accent.Color;
    public static FontFamily FontFamily => Find("Shell.Font.Family") as FontFamily ?? new FontFamily("Microsoft YaHei UI, Segoe UI");
    public static double BodyFontSize => FindDouble("Shell.Font.Body", 13);
    public static double SmallFontSize => FindDouble("Shell.Font.Small", 11);
    public static Thickness ControlPadding => Find("Shell.Space.ControlPad") is Thickness thickness
        ? thickness
        : new Thickness(10, 4, 10, 4);

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

    private static SolidColorBrush Brush(string key, Color fallback)
        => Find(key) as SolidColorBrush ?? Frozen(fallback);

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
