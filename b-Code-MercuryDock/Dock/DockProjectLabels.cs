using System.Text;
using System.Text.RegularExpressions;

namespace Mercury;

/// <summary>REQ-DOCK-LABEL-01：项目磁贴使用单字或短英文，并在完整项目库内消除重名。</summary>
internal static class DockProjectLabels
{
    internal static IReadOnlyDictionary<string, string> Create(IEnumerable<string> projectNames)
    {
        // 按完整名字分配，而不是按权重或当前十二格分配；刷新、固定与排序不会让简称互换。
        var names = projectNames.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        var candidates = names.ToDictionary(name => name, Candidates, StringComparer.Ordinal);
        var reserved = new HashSet<string>(candidates.Values.Select(values => values[0]), StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HC" };
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            var values = candidates[name];
            var label = values.FirstOrDefault(value => !used.Contains(value)
                && (value == values[0] || !reserved.Contains(value)));
            // 名称完全相同的后缀、大小写碰撞或汉字耗尽时，用唯一的短英文字母编号兜底。
            // 保留所有项目的首选简称，避免给前面的冲突项分走后面项目的首选字。
            for (var index = 0; label == null; index++)
            {
                var fallback = "X" + Letters(index);
                if (!used.Contains(fallback) && !reserved.Contains(fallback))
                    label = fallback;
            }
            used.Add(label);
            labels.Add(name, label);
        }
        return labels;
    }

    private static string[] Candidates(string name)
    {
        var suffix = Regex.Replace(name, @"^\d{4}-\d{3}-", "");
        if (suffix.StartsWith("History", StringComparison.OrdinalIgnoreCase) && suffix.Length > 7)
            suffix = suffix[7..];
        var values = new List<string>();
        foreach (var rune in suffix.EnumerateRunes())
        {
            if (rune.Value is >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
                or >= 0x20000 and <= 0x323AF)
                values.Add(rune.ToString());
        }
        var words = Regex.Matches(suffix, @"[A-Z]?[a-z]+|[A-Z]+(?![a-z])");
        var initials = string.Concat(words.Select(word => word.Value[..1])).ToUpperInvariant();
        if (initials.Length is >= 2 and <= 4)
            values.Add(initials);
        var letters = string.Concat(suffix.Where(char.IsAsciiLetter)).ToUpperInvariant();
        if (letters.Length > 0)
        {
            values.Add(letters[..Math.Min(3, letters.Length)]);
            for (var index = 1; index < letters.Length; index++)
                values.Add(letters.Substring(index, Math.Min(3, letters.Length - index)));
        }
        if (values.Count == 0)
            values.Add("PRJ");
        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Letters(int index)
    {
        var result = "";
        do
        {
            result = (char)('A' + index % 26) + result;
            index = index / 26 - 1;
        } while (index >= 0);
        return result;
    }
}
