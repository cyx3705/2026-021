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
            var fontSize = 18d;
            FormattedText text;
            do
            {
                text = new FormattedText(
                    label,
                    CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                    fontSize,
                    Brushes.White,
                    1.0);
                fontSize--;
            } while (text.Width > 52 && fontSize > 9);
            context.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static string ShortLabel(string name)
    {
        var dash = name.IndexOf('-', 8);
        var value = dash >= 0 && dash + 1 < name.Length ? name[(dash + 1)..] : name;
        return value.Length <= 6 ? value : value[..6];
    }
}
