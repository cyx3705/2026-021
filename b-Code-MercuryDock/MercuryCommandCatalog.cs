using System.Collections;
using System.IO;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace Mercury;

internal static class MercuryCommandCatalog
{
    public const string ProjectOpenCommandName = "mercury.proj.open";
    public const string ShortcutOpenCommandName = "mercury.shortcut.open";

    public static void Register(CommandRegistry registry)
    {
        foreach (var command in CreateDescriptors())
            registry.Register(command);
    }

    public static string BuildOpenProjectCommand(string nameOrNumber)
        => ProjectOpenCommandName + " " + CommandParser.QuoteArg(nameOrNumber.Trim());

    public static string BuildOpenShortcutCommand(string path)
        => ShortcutOpenCommandName + " " + CommandParser.QuoteArg(Path.GetFullPath(path.Trim()));

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

    internal static IReadOnlyList<CommandDescriptor> CreateDescriptors() =>
    [
        // mercury.go 是本域的无类直接方法（两段名）：它切换的是控制台的域聚焦，
        // 不隶属任何业务类。因为首段 mercury 是已注册域，聚焦到任何域时都能直接输入，
        // 所以各域都不必自备「退出聚焦」指令。
        new CommandDescriptor
        {
            Name = "mercury.go",
            Summary = "聚焦到指定指令域；省略 domain 则退出聚焦。",
            RequiresUiThread = true,
            Parameters = [DomainParameter()],
            Annotations = CompletionProvider("domain", "registry.domains"),
            Handler = CommandDescriptor.Sync(context =>
                CommandResult.Ok(MercuryCommands.Go(context.GetString("domain")))),
        },
        Readonly("mercury.app.status", "app", "查看托管的资源管理器入口状态。",
            _ => MercuryCommands.Status()),
        Async("mercury.shortcut.wakeconsole", "shortcut", "兼容入口：调用 Vulcan 语义命令唤出并聚焦控制台。",
            async _ => await MercuryCommands.WakeConsoleAsync().ConfigureAwait(false)),
        Result("mercury.shortcut.open", "shortcut", "打开快捷文件、普通文件或目录。",
            context => MercuryCommands.OpenShortcut(context.GetString("path")), ShortcutPathParameter()),
        Result("mercury.shortcut.add", "shortcut", "把快捷文件注册为扩展坞常驻项。",
            context => MercuryCommands.AddShortcut(context.GetString("path")), ShortcutPathParameter()),
        Write("mercury.explorer.register", "explorer", "注册托管的资源管理器入口。",
            _ => MercuryCommands.RegisterExplorer()),
        Write("mercury.explorer.remove", "explorer", "移除托管的资源管理器入口。",
            _ => MercuryCommands.RemoveExplorer(), "确定移除托管的资源管理器入口？"),
        Readonly("mercury.proj.list", "proj", "列出活动项目。",
            _ => MercuryCommands.ListProjects()),
        ProjectWrite("mercury.proj.pin", "置顶活动项目。",
            context => MercuryCommands.PinProject(context.RequireString("name"))),
        ProjectWrite("mercury.proj.unpin", "取消置顶活动项目。",
            context => MercuryCommands.UnpinProject(context.RequireString("name"))),
        ProjectWrite("mercury.proj.add", "添加并置顶项目。",
            context => MercuryCommands.AddProject(context.RequireString("name"))),
        Async("mercury.proj.refresh", "proj", "重新扫描活动项目。",
            async _ => await MercuryCommands.RefreshProjectsAsync().ConfigureAwait(false)),
        ProjectWrite("mercury.proj.exclude", "从项目坞排除项目。",
            context => MercuryCommands.ExcludeProject(context.RequireString("name"))),
        ProjectWrite("mercury.proj.include", "将项目重新纳入项目坞。",
            context => MercuryCommands.IncludeProject(context.RequireString("name"))),
        new CommandDescriptor
        {
            Name = ProjectOpenCommandName,
            CommandClass = "proj",
            Summary = "打开项目目录。",
            Example = "mercury.proj.open 2026-021-HistoryMercury",
            Parameters = [NameParameter()],
            Annotations = ProjectCompletionProvider(includeDockEntryKind: true),
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
        Result("mercury.dock.add", "dock", "把任意总线指令注册为扩展坞常驻项。",
            context => MercuryCommands.AddDockCommand(
                context.GetString("command"),
                context.GetString("label")),
            DockCommandParameter(), DockLabelParameter()),
        new CommandDescriptor
        {
            Name = "mercury.dock.remove",
            CommandClass = "dock",
            Summary = "移除扩展坞中的常驻指令项。",
            Parameters = [DockCommandParameter()],
            Annotations = CompletionProvider("command", "mercury.dock.commands"),
            Handler = context => Task.FromResult(
                MercuryCommands.RemoveDockCommand(context.GetString("command"))),
        },
        Async("mercury.app.open", "app", "显示或启动 HistoryVulcan 前端。",
            async _ => await MercuryCommands.ShowHostAsync().ConfigureAwait(false)),
        Readonly("mercury.usage.list", "usage", "列出项目使用记录。", _ => MercuryCommands.ListUsage()),
        Write("mercury.usage.forget", "usage", "清除项目使用历史。",
            context => MercuryCommands.ForgetUsage(context.GetString("name")), "确定清除所选使用历史？", OptionalNameParameter()),

        // 全局快捷键改由命令暴露：调用方只需知道命令名与参数名，不引用任何 CLR 契约，
        // 因此 Mercury 可以自由重构快捷键实现而不触动宿主公开面。
        Readonly("mercury.hotkey.list", "hotkey", "列出当前生效的全局快捷键注册。",
            _ => Input.HotkeyCommands.List()),
        Write("mercury.hotkey.register", "hotkey", "注册「按键序列 → 命令」的全局快捷键。",
            context => Input.HotkeyCommands.Register(
                context.RequireString("id"),
                context.RequireString("stroke"),
                context.RequireString("command"),
                context.GetString("owner"),
                context.Has("interval") ? (int)context.GetDouble("interval") : null),
            HotkeyIdParameter(), HotkeyStrokeParameter(), HotkeyCommandParameter(),
            HotkeyOwnerParameter(), HotkeyIntervalParameter()),
        Write("mercury.hotkey.unregister", "hotkey", "注销此前注册的全局快捷键。",
            context => Input.HotkeyCommands.Unregister(context.RequireString("id")),
            HotkeyIdParameter()),
    ];

    private static ParameterSpec HotkeyIdParameter() => new()
    {
        Name = "id",
        Description = "快捷键标识；同 id 重复注册会覆盖旧的。",
        Required = true,
        Position = 0,
    };

    private static ParameterSpec HotkeyStrokeParameter() => new()
    {
        Name = "stroke",
        Description = "按键序列：逗号分隔多次击键，每次为「修饰键+主键」。"
            + "例 Ctrl+Alt+M、Slash,Slash（连按两次 /）、VK:0xBF（虚拟键码）。",
        Required = true,
        Position = 1,
    };

    private static ParameterSpec HotkeyCommandParameter() => new()
    {
        Name = "command",
        Description = "命中后要执行的指令文本。",
        Required = true,
        Position = 2,
    };

    private static ParameterSpec HotkeyOwnerParameter() => new()
    {
        Name = "owner",
        Description = "注册方标识；省略则记为 HistoryMercury。",
        Required = false,
    };

    private static ParameterSpec HotkeyIntervalParameter() => new()
    {
        Name = "interval",
        Description = "多次击键之间的最大间隔毫秒数；省略为 350。",
        Required = false,
    };

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

    private static ParameterSpec ShortcutPathParameter() => new()
    {
        Name = "path",
        Description = "快捷文件、普通文件或目录路径。",
        Required = true,
        Position = 0,
    };

    private static ParameterSpec DockCommandParameter() => new()
    {
        Name = "command",
        Description = "要常驻或移除的完整总线指令文本。",
        Required = true,
        Position = 0,
    };

    private static ParameterSpec DockLabelParameter() => new()
    {
        Name = "label",
        Description = "可选显示标签；省略时使用规范化后的指令文本。",
        Required = false,
    };

    private static IReadOnlyDictionary<string, string> CompletionProvider(string parameter, string provider)
        => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"completion.values.{parameter}"] = provider,
        };

    private static IReadOnlyDictionary<string, string> ProjectCompletionProvider(bool includeDockEntryKind = false)
    {
        var annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["completion.values.name"] = "mercury.projects",
        };
        if (includeDockEntryKind)
            annotations["mercury.dock.entry.kind"] = "project";
        return annotations;
    }

    private static CommandDescriptor ProjectWrite(
        string name,
        string summary,
        Func<CommandContext, string> handler)
        => new()
        {
            Name = name,
            CommandClass = "proj",
            Summary = summary,
            Parameters = [NameParameter()],
            Annotations = ProjectCompletionProvider(),
            Handler = CommandDescriptor.Sync(context => CommandResult.Ok(handler(context))),
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

    private static CommandDescriptor Result(
        string name,
        string commandClass,
        string summary,
        Func<CommandContext, CommandResult> handler,
        params ParameterSpec[] parameters)
        => new()
        {
            Name = name,
            CommandClass = commandClass,
            Summary = summary,
            Parameters = parameters,
            Handler = context => Task.FromResult(handler(context)),
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
