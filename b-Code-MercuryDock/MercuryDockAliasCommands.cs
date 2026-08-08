using System.Collections;
using System.Diagnostics;
using System.Text.Json;
using HistoryVulcan.Core.Commands;

namespace MercuryDock;

/// <summary>
/// 字面名别名指令：扩展坞条目的统一指令形态。
/// 经 IModuleContext.RegisterCommands 注册（服务进程生效），
/// 与 16 条 dock.* 反射指令并存，互不影响。
/// </summary>
internal static class MercuryDockAliasCommands
{
    /// <summary>打开项目目录的别名指令名；加入窗口的默认常驻指令。</summary>
    public const string OpenCommandName = "mercury.dock.open";

    public static void Register(CommandRegistry registry)
    {
        registry.Register(new CommandDescriptor
        {
            Name = OpenCommandName,
            CommandClass = "app",
            Summary = "打开指定项目的目录（扩展坞项目条目的指令形态）",
            Example = "mercury.dock.open 2026-021-HistoryMercury",
            Parameters =
            [
                new ParameterSpec
                {
                    Name = "name",
                    Description = "项目名或编号",
                    Required = true,
                    Position = 0,
                },
            ],
            Handler = CommandDescriptor.Sync(OpenProject),
        });
    }

    /// <summary>组装调用文本；参数含空白/引号/等号时自动加引号。</summary>
    public static string BuildOpenCommandText(string nameOrNumber)
        => OpenCommandName + " " + CommandParser.QuoteArg(nameOrNumber.Trim());

    /// <summary>command.list 的 Data 跨进程为 JsonElement，本进程为只读列表；模块侧自行解析各字段。</summary>
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

    /// <summary>服务清单不可得时的保底候选：常驻默认指令加本模块 16 条 dock.*。</summary>
    public static IReadOnlyList<CommandCatalogItem> FallbackCommandCatalog() =>
    [
        new(OpenCommandName, "MercuryDock", "app", "打开指定项目的目录（扩展坞项目条目的指令形态）"),
        new("dock.open", "MercuryDock", "app", "打开 OHS 主界面；已在运行则唤到前台"),
        new("dock.list", "MercuryDock", "projects", "列出活动项目"),
        new("dock.refresh", "MercuryDock", "projects", "重新扫描活动项目"),
        new("dock.add", "MercuryDock", "projects", "手动把项目加入扩展坞并固定"),
        new("dock.pin", "MercuryDock", "projects", "置顶活动项目"),
        new("dock.unpin", "MercuryDock", "projects", "取消置顶活动项目"),
        new("dock.exclude", "MercuryDock", "projects", "手动排除某个项目"),
        new("dock.include", "MercuryDock", "projects", "恢复收录某个被排除的项目"),
        new("dock.hide", "MercuryDock", "visibility", "持久化隐藏活动坞"),
        new("dock.show", "MercuryDock", "visibility", "显示活动坞"),
        new("dock.usage", "MercuryDock", "usage", "列出使用记录与当前权重"),
        new("dock.forget", "MercuryDock", "usage", "清除使用记录"),
        new("dock.policy", "MercuryDock", "policy", "查看或设置收录策略"),
        new("dock.explorer", "MercuryDock", "explorer", "返回 OHS 项目入口的当前注册状态"),
        new("dock.explorerRegister", "MercuryDock", "explorer", "把当前工作树注册到资源管理器左侧"),
        new("dock.explorerRemove", "MercuryDock", "explorer", "移除资源管理器入口"),
    ];

    private static CommandResult OpenProject(CommandContext context)
    {
        var name = context.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("用法: mercury.dock.open <项目名或编号>");
        if (!MercuryDockState.TryResolveWorktreeProject(name, out var projectName, out var path))
            return CommandResult.Fail($"未找到项目 {name}");
        try
        {
            MercuryDockState.RecordOpen(projectName);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return CommandResult.Ok($"已打开 {projectName}");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"打开 {projectName} 失败: {ex.GetType().Name}");
        }
    }
}

/// <summary>指令目录项：名字按点号分层（域.类.方法），Domain/CommandClass 为注册表元数据。</summary>
public sealed record CommandCatalogItem(string Name, string Domain, string CommandClass, string Summary);

/// <summary>
/// 指令名按点号分层的候选树：一级一级下钻（域 → 类 → 方法）。
/// 同一节点既是完整指令又带下级时按分支处理（指令本身仍可手工输入全文）。
/// </summary>
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

    /// <summary>按当前输入给出下一级候选：最后一个点号之前是已确认路径，之后是正在输入的过滤段。</summary>
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
            .Where(pair => pair.Key.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SuggestOption(
                pair.Value.Children.Count > 0 ? prefix + pair.Key + "." : prefix + pair.Key,
                pair.Value.Children.Count > 0 ? pair.Key + " ▸" : pair.Key,
                Describe(pair.Value),
                pair.Value.Children.Count > 0))
            .ToList();
    }

    private static string Describe(Node node)
    {
        if (node.Command is { Summary.Length: > 0 } command)
            return node.Children.Count > 0
                ? command.Summary + "（仍有下级）"
                : command.Summary;
        return $"{node.Children.Count} 个子项";
    }

    private sealed class Node
    {
        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CommandCatalogItem? Command { get; set; }
    }
}
