using System.Globalization;
using System.Runtime.Versioning;

namespace Mercury.Input;

/// <summary>
/// <c>mercury.hotkey.*</c> 的业务实现。
///
/// 这是「命令即暴露面」的落点:快捷键能力不再经由任何 CLR 契约暴露,调用方只需要知道
/// 命令名和参数名。因此 Mercury 可以任意重构快捷键实现,而宿主与其他模块一行不动。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HotkeyCommands
{
    /// <summary>注册一个「按键序列 → 命令」的全局快捷键。</summary>
    internal static string Register(string id, string stroke, string command, string? owner, int? interval)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id 不能为空。");
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command 不能为空。");

        var service = HotkeyService.Current
            ?? throw new InvalidOperationException("全局快捷键服务未启动(仅 Windows 后台宿主提供)。");

        var strokes = ParseStrokes(stroke);
        var descriptor = new GlobalShortcutDescriptor(
            id.Trim(),
            strokes,
            command.Trim(),
            interval is > 0 ? interval.Value : 350);

        HotkeyService.Register(descriptor, string.IsNullOrWhiteSpace(owner) ? "HistoryMercury" : owner.Trim());
        return $"已注册快捷键 {id.Trim()}: {Describe(strokes)} → {command.Trim()}";
    }

    /// <summary>注销此前注册的快捷键。</summary>
    internal static string Unregister(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("id 不能为空。");
        return HotkeyService.Unregister(id.Trim())
            ? $"已注销快捷键 {id.Trim()}。"
            : $"没有名为 {id.Trim()} 的快捷键注册。";
    }

    /// <summary>列出当前生效的快捷键注册。</summary>
    internal static object List()
    {
        var service = HotkeyService.Current;
        if (service == null)
            return new { enabled = false, count = 0, items = Array.Empty<object>() };

        var items = service.Registrations
            .Select(item => new
            {
                id = item.Id,
                owner = item.Owner,
                stroke = Describe(item.Strokes),
                command = item.CommandText,
                intervalMs = item.MaxIntervalMilliseconds,
            })
            .ToArray();

        return new { enabled = service.IsEnabled, count = items.Length, items };
    }

    // ---------------------------------------------------------------- 按键文法

    /// <summary>
    /// 按键序列文法:逗号分隔多次击键,每次击键为「修饰键+主键」。
    /// 例:<c>Ctrl+Alt+M</c>、<c>Slash,Slash</c>(连按两次 /)、<c>VK:0xBF</c>(直接给虚拟键码)。
    /// 修饰键:Ctrl/Control、Alt、Shift、Win/Windows。
    /// </summary>
    private static IReadOnlyList<GlobalShortcutStroke> ParseStrokes(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("stroke 不能为空。示例:Ctrl+Alt+M 或 Slash,Slash");

        var strokes = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseStroke)
            .ToList();

        if (strokes.Count == 0)
            throw new ArgumentException("stroke 至少要包含一次击键。");
        return strokes;
    }

    private static GlobalShortcutStroke ParseStroke(string text)
    {
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"无法解析击键: \"{text}\"");

        var modifiers = GlobalShortcutModifiers.None;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            modifiers |= parts[i].ToLowerInvariant() switch
            {
                "ctrl" or "control" => GlobalShortcutModifiers.Control,
                "alt" => GlobalShortcutModifiers.Alt,
                "shift" => GlobalShortcutModifiers.Shift,
                "win" or "windows" => GlobalShortcutModifiers.Windows,
                _ => throw new ArgumentException(
                    $"未知修饰键 \"{parts[i]}\";可用:Ctrl / Alt / Shift / Win"),
            };
        }

        return new GlobalShortcutStroke(ParseVirtualKey(parts[^1]), modifiers);
    }

    private static int ParseVirtualKey(string key)
    {
        // 直通口:命名表覆盖不到的键仍可直接给虚拟键码，避免文法成为新的瓶颈。
        if (key.StartsWith("VK:", StringComparison.OrdinalIgnoreCase))
        {
            var raw = key[3..].Trim();
            var parsed = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? int.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)
                    ? hex : -1
                : int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec)
                    ? dec : -1;
            if (parsed is < 1 or > 0xFF)
                throw new ArgumentException($"虚拟键码超出范围: \"{key}\"");
            return parsed;
        }

        if (key.Length == 1)
        {
            var c = char.ToUpperInvariant(key[0]);
            if (c is >= 'A' and <= 'Z') return c;
            if (c is >= '0' and <= '9') return c;
            if (c == '/') return 0xBF;
        }

        if (key.Length is 2 or 3
            && (key[0] is 'F' or 'f')
            && int.TryParse(key[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fn)
            && fn is >= 1 and <= 24)
        {
            return 0x70 + fn - 1;
        }

        return key.ToLowerInvariant() switch
        {
            "slash" => 0xBF,
            "backslash" => 0xDC,
            "comma" => 0xBC,
            "period" or "dot" => 0xBE,
            "semicolon" => 0xBA,
            "quote" => 0xDE,
            "space" => 0x20,
            "tab" => 0x09,
            "enter" or "return" => 0x0D,
            "escape" or "esc" => 0x1B,
            "backspace" => 0x08,
            "insert" => 0x2D,
            "delete" or "del" => 0x2E,
            "home" => 0x24,
            "end" => 0x23,
            "pageup" => 0x21,
            "pagedown" => 0x22,
            "up" => 0x26,
            "down" => 0x28,
            "left" => 0x25,
            "right" => 0x27,
            _ => throw new ArgumentException(
                $"未知按键 \"{key}\";可用字母/数字、F1-F24、Slash、Space、Enter 等名称，或 VK:0xBF 形式的虚拟键码"),
        };
    }

    /// <summary>把按键序列还原成文法文本，使 list 的输出可以直接回填给 register。</summary>
    internal static string Describe(IReadOnlyList<GlobalShortcutStroke> strokes)
        => string.Join(",", strokes.Select(DescribeStroke));

    private static string DescribeStroke(GlobalShortcutStroke stroke)
    {
        var parts = new List<string>();
        if (stroke.Modifiers.HasFlag(GlobalShortcutModifiers.Control)) parts.Add("Ctrl");
        if (stroke.Modifiers.HasFlag(GlobalShortcutModifiers.Alt)) parts.Add("Alt");
        if (stroke.Modifiers.HasFlag(GlobalShortcutModifiers.Shift)) parts.Add("Shift");
        if (stroke.Modifiers.HasFlag(GlobalShortcutModifiers.Windows)) parts.Add("Win");
        parts.Add(DescribeKey(stroke.VirtualKey));
        return string.Join("+", parts);
    }

    private static string DescribeKey(int virtualKey) => virtualKey switch
    {
        >= 'A' and <= 'Z' => ((char)virtualKey).ToString(),
        >= '0' and <= '9' => ((char)virtualKey).ToString(),
        >= 0x70 and <= 0x87 => "F" + (virtualKey - 0x70 + 1).ToString(CultureInfo.InvariantCulture),
        0xBF => "Slash",
        0xDC => "Backslash",
        0xBC => "Comma",
        0xBE => "Period",
        0x20 => "Space",
        0x09 => "Tab",
        0x0D => "Enter",
        0x1B => "Escape",
        _ => "VK:0x" + virtualKey.ToString("X2", CultureInfo.InvariantCulture),
    };
}
