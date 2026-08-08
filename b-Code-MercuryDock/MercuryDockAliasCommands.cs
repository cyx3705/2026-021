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

    /// <summary>command.list 的 Data 跨进程为 JsonElement，本进程为只读列表；模块侧自行解析名字段。</summary>
    public static IReadOnlyList<string> ParseCommandNames(object? data)
    {
        try
        {
            if (data is JsonElement json && json.ValueKind == JsonValueKind.Array)
            {
                return json.EnumerateArray()
                    .Select(item => item.TryGetProperty("commandName", out var name)
                        ? name.GetString()
                        : null)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToList();
            }

            if (data is IEnumerable items)
            {
                return items.Cast<object?>()
                    .Select(item => item?.GetType()
                        .GetProperty("CommandName")?.GetValue(item) as string)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Cast<string>()
                    .ToList();
            }
        }
        catch (Exception)
        {
        }

        return [];
    }

    /// <summary>服务清单不可得时的保底候选：常驻默认指令加本模块 16 条 dock.*。</summary>
    public static IReadOnlyList<string> FallbackCommandNames() =>
    [
        OpenCommandName,
        "dock.open", "dock.list", "dock.refresh", "dock.add",
        "dock.pin", "dock.unpin", "dock.exclude", "dock.include",
        "dock.hide", "dock.show", "dock.usage", "dock.forget",
        "dock.policy", "dock.explorer", "dock.explorerRegister", "dock.explorerRemove",
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
