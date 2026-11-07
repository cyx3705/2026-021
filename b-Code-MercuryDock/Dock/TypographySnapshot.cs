using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mercury;

// REQ-DOCK-FONT-02: embedded output of diana.z.read, not a second editable token source.
internal static class TypographySnapshot
{
    private static readonly JsonElement Tokens = Read();
    internal static string FontFamily => string.Join(", ", Tokens.GetProperty("families").GetProperty("desktopSerif")
        .EnumerateArray().Select(name => name.GetString()));
    internal static int BodyWeight => Tokens.GetProperty("weights").GetProperty("body").GetInt32();
    internal static int HeadingWeight => Tokens.GetProperty("weights").GetProperty("heading").GetInt32();

    private static JsonElement Read()
    {
        using var stream = typeof(TypographySnapshot).Assembly.GetManifestResourceStream("Mercury.TypographySnapshot")
            ?? throw new InvalidOperationException("Missing consumed OneHistory typography snapshot");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        if (root.GetProperty("source").GetString() != "2026-031-OneHistorySite/z-OneHistoryID/typography.json"
            || !Regex.IsMatch(root.GetProperty("sha256").GetString() ?? "", "^[A-F0-9]{64}$"))
            throw new InvalidOperationException("Invalid typography source receipt");
        var tokens = root.GetProperty("tokens");
        if (tokens.GetProperty("schemaVersion").GetInt32() != 1
            || tokens.GetProperty("id").GetString() != "onehistory.typography")
            throw new InvalidOperationException("Unsupported typography schema");
        foreach (var key in new[] { "body", "heading" })
        {
            var weight = tokens.GetProperty("weights").GetProperty(key).GetInt32();
            if (weight is < 100 or > 900) throw new InvalidOperationException("Invalid typography weight");
        }
        var families = tokens.GetProperty("families").GetProperty("desktopSerif");
        if (families.GetArrayLength() == 0 || families.EnumerateArray().Any(name =>
                !Regex.IsMatch(name.GetString() ?? "", "^[A-Za-z0-9 -]+$")))
            throw new InvalidOperationException("Invalid typography family");
        return tokens.Clone();
    }
}