using System.Globalization;
using System.Text.Json;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Services.Commands;

namespace Mercury.Ui;

/// <summary>
/// 页面取数（协议 §2.3）。表格数据不写进描述，组件按需调 <c>mercury.ui.data view=</c> 取。
/// </summary>
/// <remarks>
/// 返回值一律是「字符串到字符串」的行数组 JSON。Aurora 的渲染器优先读
/// <see cref="CommandResult.Data"/>、回退 <see cref="CommandResult.Message"/>，
/// 因此两处放同一份 JSON：<c>Data</c> 是 <c>object?</c>，跨进程中继后结构化载荷不会原样存活，
/// 只放 <c>Data</c> 的话「装在宿主进程里能用、跨进程取数静默拿到空表」。
/// </remarks>
internal static class MercuryUiData
{
    public const string DataCommandName = "mercury.ui.data";

    public const string EntriesView = "entries";

    public const string CommandsView = "commands";

    /// <summary>行键：<c>proj:&lt;项目名&gt;</c> 或 <c>cmd:&lt;指令文本&gt;</c>。</summary>
    private const string ProjectKeyPrefix = "proj:";

    private const string CommandKeyPrefix = "cmd:";

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<CommandResult> ReadAsync(string? view, CommandBus? bus)
    {
        var rows = (view ?? EntriesView).Trim().ToLowerInvariant() switch
        {
            EntriesView or "" => Entries(),
            CommandsView => await CommandsAsync(bus).ConfigureAwait(false),
            _ => null,
        };

        if (rows == null)
            return CommandResult.Fail($"未知取数视图：{view}。可选 {EntriesView} / {CommandsView}。");

        var json = JsonSerializer.Serialize(rows, Options);
        return CommandResult.Ok(json, json);
    }

    /// <summary>扩展坞条目：活动项目在前，手动加入的常驻指令项在后，与坞上的顺序一致。</summary>
    public static List<Dictionary<string, string>> Entries()
    {
        var rows = MercuryState.AllProjects.Select(ProjectRow).ToList();
        rows.AddRange(MercuryState.CommandEntries.Select(CommandRow));
        return rows;
    }

    private static Dictionary<string, string> ProjectRow(DockProject project) => new()
    {
        ["key"] = ProjectKeyPrefix + project.Name,
        ["name"] = project.Name,
        ["type"] = "项目",
        ["state"] = project.Excluded ? "排除" : project.Pinned ? "固定" : "自动",
        ["command"] = MercuryCommandCatalog.BuildOpenProjectCommand(project.Name),
        ["weight"] = project.Weight.ToString("F2", CultureInfo.InvariantCulture),
        ["clicks"] = project.Clicks.ToString("F1", CultureInfo.InvariantCulture),
        ["lastopened"] = project.LastOpened?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            ?? "-",
    };

    private static Dictionary<string, string> CommandRow(DockCommandEntry entry)
    {
        var shortcut = entry.Command.StartsWith(
            MercuryCommandCatalog.ShortcutOpenCommandName + " ", StringComparison.OrdinalIgnoreCase);
        return new Dictionary<string, string>
        {
            ["key"] = CommandKeyPrefix + entry.Command,
            ["name"] = entry.Label,
            ["type"] = shortcut ? "快捷文件" : "命令",
            ["state"] = "常驻",
            ["command"] = entry.Command,
            ["weight"] = "-",
            ["clicks"] = "-",
            ["lastopened"] = "-",
        };
    }

    /// <summary>
    /// 命令集：目录权威始终是宿主注册表，本模块只做一次形状转换。
    /// 取不到目录时返回空表而不是失败——一张空表比整页红字更接近事实。
    /// </summary>
    private static async Task<List<Dictionary<string, string>>> CommandsAsync(CommandBus? bus)
    {
        if (bus == null)
            return [];

        CommandResult result;
        try
        {
            result = await bus.ExecuteAsync("vulcan.command.list", "Mercury").ConfigureAwait(false);
        }
        catch (Exception)
        {
            return [];
        }

        if (!result.Success || !CommandResultData.TryRead<IReadOnlyList<CommandCatalogRow>>(result.Data, out var rows))
            return [];

        return rows
            .OrderBy(row => row.CommandName, StringComparer.OrdinalIgnoreCase)
            .Select(row => new Dictionary<string, string>
            {
                ["name"] = row.CommandName,
                ["domain"] = row.Domain,
                ["class"] = row.CommandClass,
                ["summary"] = row.Summary,
            })
            .ToList();
    }

    /// <summary>
    /// 执行一个扩展坞条目。
    /// </summary>
    /// <remarks>
    /// 只接受**当前坞里确实存在**的行键，不接受任意指令文本。页面按钮把选中行的
    /// <c>key</c> 递进来，这里再去状态里查一次——否则这条命令就成了「代为执行任意指令」的
    /// 旁路，逐条排除的远端策略会被它整个绕开。同理它自己也带 <c>HiddenReason</c>。
    /// </remarks>
    public static async Task<CommandResult> RunEntryAsync(string? key, CommandBus? bus)
    {
        var value = (key ?? string.Empty).Trim();
        if (value.Length == 0)
            return CommandResult.Fail("用法：mercury.dock.run key=<行键>。");

        if (value.StartsWith(ProjectKeyPrefix, StringComparison.Ordinal))
        {
            var name = value[ProjectKeyPrefix.Length..];
            if (!MercuryState.AllProjects.Any(project =>
                    string.Equals(project.Name, name, StringComparison.OrdinalIgnoreCase)))
                return CommandResult.Fail($"扩展坞里没有项目：{name}。");
            return MercuryCommands.OpenProject(name);
        }

        if (value.StartsWith(CommandKeyPrefix, StringComparison.Ordinal))
        {
            var text = value[CommandKeyPrefix.Length..];
            var entry = MercuryState.CommandEntries.FirstOrDefault(item =>
                string.Equals(item.Command, text, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return CommandResult.Fail($"扩展坞里没有常驻项：{text}。");
            if (bus == null)
                return CommandResult.Fail("指令总线未就绪。");
            return await bus.ExecuteAsync(entry.Command, "Mercury").ConfigureAwait(false);
        }

        return CommandResult.Fail($"无法识别的行键：{value}。");
    }
}
