using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Mercury;

public static class ProjectIconGenerator
{
    private static readonly Color[] Palette =
    [
        Color.FromRgb(38, 99, 156),
        Color.FromRgb(33, 122, 91),
        Color.FromRgb(156, 69, 58),
        Color.FromRgb(97, 76, 150),
        Color.FromRgb(157, 108, 30),
        Color.FromRgb(45, 116, 128),
    ];

    public static BitmapSource Create(string projectName, string? cacheRoot = null)
    {
        var cache = cacheRoot ?? Path.Combine(MercuryPaths.DataRoot, "icons");
        Directory.CreateDirectory(cache);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v1:" + projectName)));
        var path = Path.Combine(cache, hash + ".png");
        if (!File.Exists(path))
            Render(projectName, path);

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    private static void Render(string name, string path)
    {
        const int size = 64;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var color = Palette[(int)((uint)StringComparer.Ordinal.GetHashCode(name) % Palette.Length)];
            context.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, size, size), 8, 8);
            var label = ShortLabel(name);
            var fontSize = FitFontSize(label, 52, 18);
            var text = new FormattedText(
                label,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                fontSize,
                Brushes.White,
                1.0);
            context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    /// <summary>磁贴上显示的项目后缀：去掉编号前缀及品牌前缀，不添加省略号。</summary>
    public static string ShortLabel(string name)
    {
        var dash = name.IndexOf('-', 8);
        var value = dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : name;
        if (value.StartsWith("History", StringComparison.OrdinalIgnoreCase)
            && value.Length > "History".Length)
            value = value["History".Length..];
        return value.Length == 0 ? name : value;
    }

    /// <summary>按像素宽度选择完整标签可用的字号，保证 UI 不以省略号牺牲项目特征。</summary>
    public static double FitFontSize(string text, double maxWidth, double maximum = 18)
    {
        var size = maximum;
        while (size > 6)
        {
            var formatted = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                size,
                Brushes.White,
                1.0);
            if (formatted.Width <= maxWidth)
                break;
            size--;
        }
        return size;
    }
}
