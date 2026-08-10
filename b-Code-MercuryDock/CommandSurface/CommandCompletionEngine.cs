using System.Text;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.CommandSurface;

namespace Mercury.CommandSurface;

internal sealed record CommandCompletionDefinition(
    string Name,
    string Summary,
    IReadOnlyList<ParameterSpec> Parameters);

/// <summary>为控制台输入提供命令、参数名和允许值候选；不执行命令也不改变注册表。</summary>
internal sealed class CommandCompletionEngine
{
    public ConsoleCompletionResult Complete(
        string text,
        int caretIndex,
        IReadOnlyList<CommandCompletionDefinition> definitions,
        string? focusedDomain = null)
    {
        text ??= "";
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        var (tokenStart, tokenEnd) = TokenBounds(text, caret);
        var token = text[tokenStart..caret];
        var beforeTokens = Lex(text[..tokenStart]);

        if (beforeTokens.Count == 0)
        {
            var exact = definitions.FirstOrDefault(command =>
                command.Name.Equals(token, StringComparison.OrdinalIgnoreCase));
            if (caret == tokenEnd && exact is { Parameters.Count: 0 })
                return ConsoleCompletionResult.Empty;

            return CreateResult(
                tokenStart,
                tokenEnd,
                CompleteCommandName(token, definitions, focusedDomain));
        }

        var commandName = ResolveAgainstFocus(beforeTokens[0], definitions, focusedDomain);
        var command = definitions.FirstOrDefault(definition =>
            definition.Name.Equals(commandName, StringComparison.OrdinalIgnoreCase));
        if (command == null)
            return ConsoleCompletionResult.Empty;

        var equals = FindUnquotedEquals(token);
        if (equals >= 0)
        {
            var key = token[..equals];
            var parameter = command.Parameters.FirstOrDefault(item =>
                item.Name.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (parameter?.AllowedValues is not { Length: > 0 } values)
                return ConsoleCompletionResult.Empty;

            var valueStart = tokenStart + equals + 1;
            var rawPrefix = text[valueStart..caret];
            var quoted = rawPrefix.StartsWith('"');
            var valuePrefix = quoted ? rawPrefix[1..] : rawPrefix;
            var candidates = values
                .Where(value => value.StartsWith(valuePrefix, StringComparison.OrdinalIgnoreCase))
                .Select(value => Candidate(
                    value,
                    quoted ? $"\"{value}\"" : value,
                    parameter.Description,
                    ConsoleCompletionKind.Value));
            return CreateResult(valueStart, tokenEnd, candidates);
        }

        var usedNames = beforeTokens.Skip(1)
            .Select(item =>
            {
                var index = FindUnquotedEquals(item);
                return index > 0 ? item[..index] : null;
            })
            .Where(item => item != null)
            .Select(item => item!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var parameterCandidates = command.Parameters
            .Where(parameter => !usedNames.Contains(parameter.Name))
            .Where(parameter => parameter.Name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Select(parameter => Candidate(
                parameter.Name + "=",
                parameter.Name + "=",
                parameter.Description,
                ConsoleCompletionKind.Parameter))
            .ToList();

        if (parameterCandidates.Count > 0 || token.Length == 0)
            return CreateResult(tokenStart, tokenEnd, parameterCandidates);

        var positionalIndex = beforeTokens.Skip(1).Count(item => FindUnquotedEquals(item) < 0);
        var positional = command.Parameters
            .Where(parameter => parameter.Position == positionalIndex)
            .SelectMany(parameter => parameter.AllowedValues ?? [])
            .Where(value => value.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Select(value => Candidate(value, value, "位置参数", ConsoleCompletionKind.Value));
        return CreateResult(tokenStart, tokenEnd, positional);
    }

    internal static string? CommandNameBeforeCurrentToken(string text, int caretIndex)
    {
        text ??= "";
        var caret = Math.Clamp(caretIndex, 0, text.Length);
        var (tokenStart, _) = TokenBounds(text, caret);
        var beforeTokens = Lex(text[..tokenStart]);
        return beforeTokens.Count == 0 ? null : beforeTokens[0];
    }

    /// <summary>
    /// 命令名按 域 → 类 → 方法 分段推进补全（DEC-025）。
    /// </summary>
    /// <remarks>
    /// 已输入的点号数量决定当前处在哪一段：0 个点选域、1 个点选类、2 个点选方法。
    /// 域聚焦时首段可以省略，此时同一个 token 既可能是域也可能是聚焦域下的类，
    /// 两种解释都给出候选，由用户选（REQ-CMD-012 第 4 条）。
    /// 域清单不来自任何常量表，而是从传入的已注册命令定义现算。
    /// </remarks>
    private static IEnumerable<ConsoleCompletionCandidate> CompleteCommandName(
        string token,
        IReadOnlyList<CommandCompletionDefinition> definitions,
        string? focusedDomain)
    {
        var typedDots = token.Count(character => character == '.');

        if (typedDots >= 2)
            return MethodCandidates(token, definitions, prefix: null);

        if (typedDots == 1)
        {
            // `域.` → 选该域的类；聚焦时 `类.` → 选该类的方法。
            var candidates = ClassCandidates(token, definitions).ToList();
            candidates.AddRange(DirectMethodCandidates(token, definitions));
            if (IsFocused(focusedDomain))
                candidates.AddRange(MethodCandidates($"{focusedDomain}.{token}", definitions, focusedDomain));
            return candidates;
        }

        // 未输入点号：选域；聚焦时同时给出聚焦域下的类，且类排在前面（更常用）。
        var result = new List<ConsoleCompletionCandidate>();
        if (IsFocused(focusedDomain))
        {
            result.AddRange(ClassCandidates($"{focusedDomain}.{token}", definitions, stripPrefix: $"{focusedDomain}."));
            result.AddRange(DirectMethodCandidates($"{focusedDomain}.{token}", definitions, stripPrefix: $"{focusedDomain}."));
        }

        result.AddRange(DomainCandidates(token, definitions));
        return result;
    }

    private static bool IsFocused(string? domain)
        => !string.IsNullOrWhiteSpace(domain) && domain.Trim() != "全部";

    /// <summary>已注册域的候选。域集合从命令定义现算，不硬编码。</summary>
    private static IEnumerable<ConsoleCompletionCandidate> DomainCandidates(
        string token,
        IReadOnlyList<CommandCompletionDefinition> definitions)
        => definitions
            .Select(command => Segment(command.Name, 0))
            .Where(domain => domain.Length > 0
                             && domain.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(domain => domain, StringComparer.Ordinal)
            .Select(domain => Candidate(
                domain + ".",
                domain + ".",
                "域",
                ConsoleCompletionKind.Domain));

    /// <summary>`域.类前缀` → 该域下匹配的类。</summary>
    private static IEnumerable<ConsoleCompletionCandidate> ClassCandidates(
        string token,
        IReadOnlyList<CommandCompletionDefinition> definitions,
        string? stripPrefix = null)
    {
        var domain = Segment(token, 0);
        var typed = Segment(token, 1);
        return definitions
            .Where(command => Segment(command.Name, 0)
                                  .Equals(domain, StringComparison.OrdinalIgnoreCase)
                              && SegmentCount(command.Name) >= 3)
            .Select(command => Segment(command.Name, 1))
            .Where(item => item.Length > 0
                           && item.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.Ordinal)
            .Select(item =>
            {
                var full = $"{domain}.{item}.";
                var insert = stripPrefix != null && full.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase)
                    ? full[stripPrefix.Length..]
                    : full;
                return Candidate(insert, insert, "类", ConsoleCompletionKind.Class);
            });
    }

    /// <summary>该域的无类直接方法（两段名）。补全后带尾随空格，直接进入参数段。</summary>
    private static IEnumerable<ConsoleCompletionCandidate> DirectMethodCandidates(
        string token,
        IReadOnlyList<CommandCompletionDefinition> definitions,
        string? stripPrefix = null)
    {
        var domain = Segment(token, 0);
        var typed = Segment(token, 1);
        return definitions
            .Where(command => SegmentCount(command.Name) == 2
                              && Segment(command.Name, 0)
                                  .Equals(domain, StringComparison.OrdinalIgnoreCase)
                              && Segment(command.Name, 1)
                                  .StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name, StringComparer.Ordinal)
            .Select(command =>
            {
                var insert = stripPrefix != null
                             && command.Name.StartsWith(stripPrefix, StringComparison.OrdinalIgnoreCase)
                    ? command.Name[stripPrefix.Length..]
                    : command.Name;
                return Candidate(
                    insert + " ",
                    insert + " ",
                    string.IsNullOrWhiteSpace(command.Summary) ? "直接方法" : command.Summary,
                    ConsoleCompletionKind.Method);
            });
    }

    /// <summary>`域.类.方法前缀` → 匹配的方法。补全后带尾随空格。</summary>
    private static IEnumerable<ConsoleCompletionCandidate> MethodCandidates(
        string token,
        IReadOnlyList<CommandCompletionDefinition> definitions,
        string? prefix)
    {
        var full = prefix != null ? $"{prefix}.{token}" : token;
        var domain = Segment(full, 0);
        var commandClass = Segment(full, 1);
        var typed = Segment(full, 2);
        var strip = prefix != null ? $"{prefix}." : null;

        return definitions
            .Where(command => SegmentCount(command.Name) >= 3
                              && Segment(command.Name, 0)
                                  .Equals(domain, StringComparison.OrdinalIgnoreCase)
                              && Segment(command.Name, 1)
                                  .Equals(commandClass, StringComparison.OrdinalIgnoreCase)
                              && Segment(command.Name, 2)
                                  .StartsWith(typed, StringComparison.OrdinalIgnoreCase))
            .OrderBy(command => command.Name, StringComparer.Ordinal)
            .Select(command =>
            {
                var insert = strip != null
                             && command.Name.StartsWith(strip, StringComparison.OrdinalIgnoreCase)
                    ? command.Name[strip.Length..]
                    : command.Name;
                return Candidate(insert + " ", insert + " ", command.Summary, ConsoleCompletionKind.Method);
            });
    }

    /// <summary>
    /// 把用户输入的命令名按域聚焦规则还原成完整名：首段命中已注册域则原样，否则补聚焦域前缀。
    /// 与宿主 <c>DomainFocus.Resolve</c> 同一条规则，此处只对单个命令名生效。
    /// </summary>
    private static string ResolveAgainstFocus(
        string commandName,
        IReadOnlyList<CommandCompletionDefinition> definitions,
        string? focusedDomain)
    {
        if (!IsFocused(focusedDomain))
            return commandName;

        var head = Segment(commandName, 0);
        var isDomain = definitions.Any(command =>
            Segment(command.Name, 0).Equals(head, StringComparison.OrdinalIgnoreCase));
        return isDomain ? commandName : $"{focusedDomain!.Trim()}.{commandName}";
    }

    private static int SegmentCount(string name)
        => name.Split('.', StringSplitOptions.RemoveEmptyEntries).Length;

    /// <summary>取第 <paramref name="index"/> 段；越界返回空串（尾随点号即空末段）。</summary>
    private static string Segment(string name, int index)
    {
        var parts = name.Split('.');
        return index < parts.Length ? parts[index] : string.Empty;
    }

    private static ConsoleCompletionCandidate Candidate(
        string displayText,
        string insertText,
        string description,
        ConsoleCompletionKind kind)
        => new()
        {
            DisplayText = displayText,
            InsertText = insertText,
            Description = description,
            Kind = kind,
        };

    private static ConsoleCompletionResult CreateResult(
        int start,
        int end,
        IEnumerable<ConsoleCompletionCandidate> candidates)
        => new()
        {
            // 域候选排在类/方法之后：域聚焦时同一段里既有聚焦域的类，也有用于脱固的其他域，
            // 前者才是常用项。同一档内仍按字母序，保证相同输入的候选顺序稳定可比。
            Candidates = candidates
                .OrderBy(candidate => candidate.Kind == ConsoleCompletionKind.Domain ? 1 : 0)
                .ThenBy(candidate => candidate.DisplayText, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.DisplayText, StringComparer.Ordinal)
                .ToList(),
            ReplaceStart = start,
            ReplaceLength = end - start,
        };

    private static (int Start, int End) TokenBounds(string text, int caret)
    {
        var start = 0;
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            if (i >= caret)
                break;
            if (text[i] == '"' && !IsEscaped(text, i))
                inQuote = !inQuote;
            else if (!inQuote && char.IsWhiteSpace(text[i]))
                start = i + 1;
        }

        inQuote = IsInsideQuote(text, caret);
        var end = caret;
        while (end < text.Length)
        {
            if (text[end] == '"' && !IsEscaped(text, end))
                inQuote = !inQuote;
            else if (!inQuote && char.IsWhiteSpace(text[end]))
                break;
            end++;
        }
        return (start, end);
    }

    private static List<string> Lex(string text)
    {
        var result = new List<string>();
        var builder = new StringBuilder();
        var inQuote = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '"' && !IsEscaped(text, i))
            {
                inQuote = !inQuote;
                continue;
            }
            if (c == '\\' && inQuote && i + 1 < text.Length && text[i + 1] is '"' or '\\')
            {
                builder.Append(text[++i]);
                continue;
            }
            if (!inQuote && char.IsWhiteSpace(c))
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }
                continue;
            }
            builder.Append(c);
        }
        if (builder.Length > 0)
            result.Add(builder.ToString());
        return result;
    }

    private static int FindUnquotedEquals(string token)
    {
        var inQuote = false;
        for (var i = 0; i < token.Length; i++)
        {
            if (token[i] == '"' && !IsEscaped(token, i))
                inQuote = !inQuote;
            else if (!inQuote && token[i] == '=')
                return i;
        }
        return -1;
    }

    private static bool IsInsideQuote(string text, int index)
    {
        var inQuote = false;
        for (var i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '"' && !IsEscaped(text, i))
                inQuote = !inQuote;
        }
        return inQuote;
    }

    private static bool IsEscaped(string text, int index)
    {
        var slashes = 0;
        for (var i = index - 1; i >= 0 && text[i] == '\\'; i--)
            slashes++;
        return slashes % 2 == 1;
    }
}
