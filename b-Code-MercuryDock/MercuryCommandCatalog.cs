using System.Collections;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace Mercury;

internal static class MercuryCommandCatalog
{
    public const string ProjectOpenCommandName = "mercury.proj.open";

    public static void Register(CommandRegistry registry)
    {
        foreach (var command in CreateDescriptors())
            registry.Register(command);
    }

    public static string BuildOpenProjectCommand(string nameOrNumber)
        => ProjectOpenCommandName + " " + CommandParser.QuoteArg(nameOrNumber.Trim());

    public static IReadOnlyList<CommandCatalogItem> FallbackCommandCatalog()
        => CreateDescriptors()
            .Select(command => new CommandCatalogItem(
                command.Name,
                "Mercury",
                command.CommandClass ?? string.Empty,
                command.Summary))
            .ToList();

    public static IReadOnlyList<CommandCatalogItem> ParseCommandCatalog(object? data)
    {
        try
        {
            if (data is JsonElement json && json.ValueKind == JsonValueKind.Array)
            {
                return json.EnumerateArray()
                    .Select(ReadJsonItem)
                    .Where(item => item != null)
                    .Cast<CommandCatalogItem>()
                    .ToList();
            }

            if (data is IEnumerable items)
            {
                return items.Cast<object?>()
                    .Select(ReadTypedItem)
                    .Where(item => item != null)
                    .Cast<CommandCatalogItem>()
                    .ToList();
            }
        }
        catch (Exception)
        {
        }

        return [];
    }

    private static IReadOnlyList<CommandDescriptor> CreateDescriptors() =>
    [
        // mercury.go 是本域的无类直接方法（两段名）：它切换的是控制台的域聚焦，
        // 不隶属任何业务类。因为首段 mercury 是已注册域，聚焦到任何域时都能直接输入，
        // 所以各域都不必自备「退出聚焦」指令。
        Direct("mercury.go", "聚焦到指定指令域；省略 domain 则退出聚焦。",
            context => MercuryCommands.Go(context.GetString("domain")),
            true,
            DomainParameter()),
        Readonly("mercury.app.status", "app", "查看托管的资源管理器入口状态。",
            _ => MercuryCommands.Status()),
        Async("mercury.shortcut.wakeconsole", "shortcut", "兼容入口：调用 Vulcan 语义命令唤出并聚焦控制台。",
            async _ => await MercuryCommands.WakeConsoleAsync().ConfigureAwait(false)),
        Write("mercury.explorer.register", "explorer", "注册托管的资源管理器入口。",
            _ => MercuryCommands.RegisterExplorer()),
        Write("mercury.explorer.remove", "explorer", "移除托管的资源管理器入口。",
            _ => MercuryCommands.RemoveExplorer(), "确定移除托管的资源管理器入口？"),
        Readonly("mercury.proj.list", "proj", "列出活动项目。",
            _ => MercuryCommands.ListProjects()),
        Write("mercury.proj.pin", "proj", "置顶活动项目。",
            context => MercuryCommands.PinProject(context.RequireString("name")), NameParameter()),
        Write("mercury.proj.unpin", "proj", "取消置顶活动项目。",
            context => MercuryCommands.UnpinProject(context.RequireString("name")), NameParameter()),
        Write("mercury.proj.add", "proj", "添加并置顶项目。",
            context => MercuryCommands.AddProject(context.RequireString("name")), NameParameter()),
        Async("mercury.proj.refresh", "proj", "重新扫描活动项目。",
            async _ => await MercuryCommands.RefreshProjectsAsync().ConfigureAwait(false)),
        Write("mercury.proj.exclude", "proj", "从项目坞排除项目。",
            context => MercuryCommands.ExcludeProject(context.RequireString("name")), NameParameter()),
        Write("mercury.proj.include", "proj", "将项目重新纳入项目坞。",
            context => MercuryCommands.IncludeProject(context.RequireString("name")), NameParameter()),
        new CommandDescriptor
        {
            Name = ProjectOpenCommandName,
            CommandClass = "proj",
            Summary = "打开项目目录。",
            Example = "mercury.proj.open 2026-021-HistoryMercury",
            Parameters = [NameParameter()],
            Handler = CommandDescriptor.Sync(context => MercuryCommands.OpenProject(context.GetString("name"))),
        },
        Write("mercury.dock.hide", "dock", "隐藏项目坞。", _ => MercuryCommands.HideDock()),
        Write("mercury.dock.show", "dock", "显示项目坞。", _ => MercuryCommands.ShowDock()),
        Write("mercury.dock.policy", "dock", "查看或更新项目坞策略。",
            context => MercuryCommands.SetDockPolicy(
                context.Has("min") ? context.GetInt("min") : null,
                context.Has("max") ? context.GetInt("max") : null,
                context.Has("halflife") ? context.GetDouble("halflife") : null),
            OptionalIntParameter("min"), OptionalIntParameter("max"), OptionalDoubleParameter("halflife")),
        Write("mercury.app.open", "app", "打开 HistoryVulcan。", _ => MercuryCommands.OpenHost()),
        Readonly("mercury.usage.list", "usage", "列出项目使用记录。", _ => MercuryCommands.ListUsage()),
        Write("mercury.usage.forget", "usage", "清除项目使用历史。",
            context => MercuryCommands.ForgetUsage(context.GetString("name")), "确定清除所选使用历史？", OptionalNameParameter()),
    ];

    private static CommandDescriptor Async(
        string name,
        string commandClass,
        string summary,
        Func<CommandContext, Task<CommandResult>> handler)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Handler = handler,
        };

    /// <summary>
    /// 无类直接方法：只有 <c>&lt;域&gt;.&lt;方法&gt;</c> 两段，不声明 CommandClass。
    /// 注册表据段数判为无类（DEC-025），命令集里归入「无类」分组。
    /// </summary>
    private static CommandDescriptor Direct(
        string name,
        string summary,
        Func<CommandContext, string> handler,
        bool requiresUiThread = false,
        params ParameterSpec[] parameters)
        => new()
        {
            Name = name,
            Summary = summary,
            RequiresUiThread = requiresUiThread,
            Parameters = parameters,
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(handler(context))),
        };

    private static ParameterSpec DomainParameter() => new()
    {
        Name = "domain",
        Description = "要聚焦的指令域；省略则退出聚焦回到「全部」。",
        Required = false,
        Position = 0,
    };

    private static CommandDescriptor Readonly(string name, string commandClass, string summary, Func<CommandContext, object?> handler)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Readonly = true,
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(data: handler(context))),
        };

    private static CommandDescriptor Write(
        string name,
        string commandClass,
        string summary,
        Func<CommandContext, string> handler,
        params ParameterSpec[] parameters)
        => Write(name, commandClass, summary, handler, null, parameters);

    private static CommandDescriptor Write(
        string name,
        string commandClass,
        string summary,
        Func<CommandContext, string> handler,
        string? confirm,
        params ParameterSpec[] parameters)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Parameters = parameters,
            ConfirmPrompt = confirm == null ? null : _ => confirm,
            Dangerous = confirm != null,
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(handler(context))),
        };

    private static CommandDescriptor Async(string name, string commandClass, string summary, Func<CommandContext, Task<object?>> handler)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Handler = async context => CommandResult.Ok(data: await handler(context).ConfigureAwait(false)),
        };

    private static ParameterSpec NameParameter() => new()
    {
        Name = "name",
        Description = "项目名称或序号。",
        Required = true,
        Position = 0,
    };

    private static ParameterSpec OptionalNameParameter() => new()
    {
        Name = "name",
        Description = "项目名称或序号；省略则清除全部。",
        Required = false,
        Position = 0,
    };

    private static ParameterSpec OptionalIntParameter(string name) => new()
    {
        Name = name,
        Description = "可选整数策略值。",
        Type = ParamType.Int,
        Required = false,
    };

    private static ParameterSpec OptionalDoubleParameter(string name) => new()
    {
        Name = name,
        Description = "可选小数策略值。",
        Type = ParamType.Double,
        Required = false,
    };

    private static CommandCatalogItem? ReadJsonItem(JsonElement item)
    {
        if (!item.TryGetProperty("commandName", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
            return null;
        return new CommandCatalogItem(
            name.GetString()!,
            ReadJsonString(item, "domain"),
            ReadJsonString(item, "commandClass"),
            ReadJsonString(item, "summary"));
    }

    private static string ReadJsonString(JsonElement item, string property)
        => item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static CommandCatalogItem? ReadTypedItem(object? item)
    {
        var type = item?.GetType();
        var name = type?.GetProperty("CommandName")?.GetValue(item) as string;
        if (string.IsNullOrWhiteSpace(name))
            return null;
        return new CommandCatalogItem(
            name,
            type?.GetProperty("Domain")?.GetValue(item) as string ?? string.Empty,
            type?.GetProperty("CommandClass")?.GetValue(item) as string ?? string.Empty,
            type?.GetProperty("Summary")?.GetValue(item) as string ?? string.Empty);
    }
}

public sealed record CommandCatalogItem(string Name, string Domain, string CommandClass, string Summary);

internal sealed class CommandOptionTree
{
    private readonly Node _root = new();

    public static CommandOptionTree Build(IReadOnlyList<CommandCatalogItem> commands)
    {
        var tree = new CommandOptionTree();
        foreach (var command in commands)
        {
            var segments = command.Name.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;
            var node = tree._root;
            foreach (var segment in segments)
                node = node.Children.TryGetValue(segment, out var child)
                    ? child
                    : node.Children[segment] = new Node();
            node.Command = command;
        }
        return tree;
    }

    public IReadOnlyList<SuggestOption> ChildrenOf(string text)
    {
        var lastDot = text.LastIndexOf('.');
        var prefix = lastDot < 0 ? string.Empty : text[..(lastDot + 1)];
        var fragment = lastDot < 0 ? text : text[(lastDot + 1)..];

        var node = _root;
        if (prefix.Length > 0)
        {
            foreach (var segment in prefix.Split('.', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!node.Children.TryGetValue(segment, out var child))
                    return [];
                node = child;
            }
        }

        return node.Children
            .Where(pair => Matches(pair.Key, fragment))
            .OrderBy(pair => MatchRank(pair.Key, fragment))
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SuggestOption(
                pair.Value.Children.Count > 0 ? prefix + pair.Key + "." : prefix + pair.Key,
                pair.Value.Children.Count > 0 ? pair.Key + " >" : pair.Key,
                Describe(pair.Value),
                pair.Value.Children.Count > 0))
            .ToList();
    }

    private static string Describe(Node node)
    {
        if (node.Command is { Summary.Length: > 0 } command)
            return node.Children.Count > 0 ? command.Summary + "（含下级命令）" : command.Summary;
        return $"{node.Children.Count} 个下级命令";
    }

    private static bool Matches(string value, string filter)
        => filter.Length == 0 || value.Contains(filter, StringComparison.OrdinalIgnoreCase);

    private static int MatchRank(string value, string filter)
    {
        if (filter.Length == 0)
            return 2;
        if (value.Equals(filter, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (value.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
            return 1;
        return 2;
    }

    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CommandCatalogItem? Command { get; set; }
    }
}
