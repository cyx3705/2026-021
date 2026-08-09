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
        IReadOnlyList<CommandCompletionDefinition> definitions)
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
                definitions
                    .Where(command => MatchesCommandPrefix(command.Name, token))
                    .Select(command => Candidate(command.Name, command.Name, command.Summary, ConsoleCompletionKind.Command)));
        }

        var commandName = beforeTokens[0];
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
    /// Matches full names (`vulcan.win.reset`) and shorter typed prefixes (`win.` / `win.re`).
    /// </summary>
    private static bool MatchesCommandPrefix(string name, string token)
    {
        if (string.IsNullOrEmpty(token))
            return true;
        if (name.StartsWith(token, StringComparison.OrdinalIgnoreCase))
            return true;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] == '.'
                && name.AsSpan(i + 1).StartsWith(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
            Candidates = candidates
                .OrderBy(candidate => candidate.DisplayText, StringComparer.OrdinalIgnoreCase)
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
