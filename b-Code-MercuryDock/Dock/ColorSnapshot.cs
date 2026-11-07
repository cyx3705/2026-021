using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;

namespace Mercury;

// REQ-DOCK-COLOR-01: validated offline snapshot acquired with diana.z.read.
internal static class ColorSnapshot
{
    internal static string Revision { get; private set; } = "";
    private static readonly JsonElement Tokens = Read();
    internal static Color Get(string key, bool desktop = false)
    {
        var value = desktop ? Tokens.GetProperty("desktop").GetProperty(key)
            : Tokens.GetProperty("common").TryGetProperty(key, out var shared) ? shared
            : Tokens.GetProperty("themes").GetProperty("dark").GetProperty(key);
        return (Color)ColorConverter.ConvertFromString(value.GetString()!);
    }
    private static JsonElement Read()
    {
        using var stream = typeof(ColorSnapshot).Assembly.GetManifestResourceStream("Mercury.ColorSnapshot")
            ?? throw new InvalidOperationException("Missing consumed OneHistory color snapshot");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("source").GetString() != "2026-031-OneHistorySite/z-OneHistoryID/colors.json"
            || !Regex.IsMatch(root.GetProperty("sha256").GetString() ?? "", "^[A-F0-9]{64}$"))
            throw new InvalidOperationException("Invalid color source receipt");
        var tokens = root.GetProperty("tokens");
        if (tokens.GetProperty("schemaVersion").GetInt32() != 1 || tokens.GetProperty("id").GetString() != "onehistory.colors")
            throw new InvalidOperationException("Unsupported color schema");
        foreach (var group in new[] { tokens.GetProperty("common"), tokens.GetProperty("themes").GetProperty("light"), tokens.GetProperty("themes").GetProperty("dark"), tokens.GetProperty("desktop") })
            foreach (var entry in group.EnumerateObject())
                if (!(entry.Value.GetString() ?? "").StartsWith("rgba(") && !Regex.IsMatch(entry.Value.GetString() ?? "", "^#[A-Fa-f0-9]{6}$"))
                    throw new InvalidOperationException("Invalid color token: " + entry.Name);
        Revision = root.GetProperty("sha256").GetString()!;
        return tokens.Clone();
    }
}
