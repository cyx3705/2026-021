using System.Diagnostics;
using System.IO;
using HistoryVulcan.Core.Commands;

namespace Mercury;

public static class MercuryCommands
{
    public static ExplorerEntryStatus Status()
        => new(ExplorerNamespaceRegistration.IsRegistered(), DockShortcutFolder.Path);

    /// <summary>
    /// 切换控制台的域聚焦。
    /// </summary>
    /// <remarks>
    /// 4.x 时代聚焦状态存在 Mercury 自持的命令目录会话里；宿主 5.0 删除了命令工作台的挂接点，
    /// 控制台连同它的域筛选一起归 HistoryAurora。因此这里改成在总线上调
    /// <c>aurora.log.source</c>——那正是控制台域筛选的唯一写入口，
    /// <c>ConsoleView</c> 用同一个值做 <c>DomainFocus.Resolve</c>，语义与旧实现一致。
    ///
    /// 未装前端时该指令不存在，总线立刻返回明确失败，不等超时。
    /// </remarks>
    /// <param name="domain">要聚焦的域；空表示退出聚焦。</param>
    public static async Task<CommandResult> GoAsync(string? domain)
    {
        var bus = MercuryModule.Bus;
        if (bus == null)
            return CommandResult.Fail("指令总线未就绪。");

        var requested = string.IsNullOrWhiteSpace(domain) ? DomainFocus.All : domain.Trim();
        var result = await bus
            .ExecuteAsync("aurora.log.source " + CommandParser.QuoteArg(requested), "Mercury")
            .ConfigureAwait(false);
        if (!result.Success)
            return result;

        return CommandResult.Ok(DomainFocus.IsUnfocused(requested)
            ? "已退出域聚焦，恢复全部指令域。"
            : $"已聚焦到 {requested} 域；之后只需输入「类.方法」，"
              + "输入其他已注册域的完整名仍可直接执行。");
    }

    public static string RegisterExplorer()
    {
        var sync = DockShortcutFolder.Synchronize(MercuryState.Projects);
        var registration = ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path);
        // 用户显式发起的修复动作：注册项本来就对时也定点刷新一次那个目录，让入口立刻可见。
        if (!registration.Changed)
            ExplorerNamespaceRegistration.NotifyFolderChanged(sync.Folder);
        return registration.Message;
    }

    public static string RemoveExplorer()
        => ExplorerNamespaceRegistration.RemoveRegistration().Message;

    public static IReadOnlyList<DockProject> ListProjects() => MercuryState.Projects;

    public static string PinProject(string name)
        => MercuryState.Pin(name, pinned: true) ? $"已置顶 {name}。" : $"未找到项目：{name}。";

    public static string UnpinProject(string name)
        => MercuryState.Pin(name, pinned: false) ? $"已取消置顶 {name}。" : $"未找到项目：{name}。";

    public static string AddProject(string name)
        => MercuryState.AddToDock(name) ? $"已添加并置顶 {name}。" : $"未找到项目：{name}。";

    public static Task<IReadOnlyList<DockProject>> RefreshProjectsAsync()
        => MercuryState.RefreshAsync();

    public static string HideDock()
    {
        MercuryState.SetHidden(true);
        return "项目坞已隐藏。";
    }

    public static string ShowDock()
    {
        MercuryState.SetHidden(false);
        return "项目坞已显示。";
    }

    public static async Task<CommandResult> ShowHostAsync()
    {
        var bus = MercuryModule.Bus;
        if (bus != null)
        {
            var result = await bus.ExecuteAsync("vulcan.app.show", "Mercury").ConfigureAwait(false);
            if (result.Success)
                return CommandResult.Ok(IsFrontendStarting(result)
                    ? "HistoryVulcan 前端正在启动。"
                    : "已显示 HistoryVulcan 前端。");
        }

        return HistoryVulcanLauncher.Open();
    }

    public static async Task<CommandResult> WakeConsoleAsync()
    {
        var bus = MercuryModule.Bus;
        if (bus == null)
            return CommandResult.Fail("指令总线未就绪。");

        var result = await bus.ExecuteAsync("vulcan.app.focusconsole", "Mercury")
            .ConfigureAwait(false);
        if (IsFrontendStarting(result))
            return CommandResult.Ok("前端正在启动，将聚焦控制台");
        return result.Success
            ? CommandResult.Ok("已通过 Vulcan 语义命令唤出并聚焦控制台")
            : CommandResult.Fail(string.IsNullOrWhiteSpace(result.Message) ? "唤出前端失败" : result.Message);
    }

    private static bool IsFrontendStarting(CommandResult result)
        => result.Success
           && result.Message.Contains("前端正在启动", StringComparison.Ordinal);

    public static CommandResult OpenShortcut(string? path)
        => ShortcutFileService.Open(path);

    public static CommandResult AddShortcut(string? path)
    {
        if (!ShortcutFileService.TryResolve(path, out var source, out _, out var error))
            return CommandResult.Fail(error);

        var command = MercuryCommandCatalog.BuildOpenShortcutCommand(source);
        var label = Path.GetFileNameWithoutExtension(source);
        if (!MercuryState.AddCommand(command, label))
            return CommandResult.Fail("快捷文件路径为空，未加入扩展坞。");
        return CommandResult.Ok($"已将快捷文件加入扩展坞：{label}。");
    }

    /// <summary>
    /// 选一个文件并把它加进扩展坞。
    /// </summary>
    /// <remarks>
    /// 4.x 的管理页上「浏览」按钮开对话框、把路径填回输入框，再点「加入」。描述化协议里
    /// 没有「把一条指令的结果写回某个控件」这种表达，硬拆成两步就会得到一个点了没反应的按钮。
    /// 因此把两步收成一条指令：选择归前端（<c>aurora.ui.selectfile</c>），加入归本模块。
    /// 取消不是失败——用户改主意了而已。
    /// </remarks>
    public static async Task<CommandResult> PickShortcutAsync()
    {
        var bus = MercuryModule.Bus;
        if (bus == null)
            return CommandResult.Fail("指令总线未就绪。");

        var picked = await bus.ExecuteAsync("aurora.ui.selectfile", "Mercury").ConfigureAwait(false);
        if (!picked.Success)
            return picked;
        if (picked.Data is not string path || string.IsNullOrWhiteSpace(path))
            return CommandResult.Ok("已取消选择，未加入扩展坞。");

        return AddShortcut(path);
    }

    public static CommandResult AddDockCommand(string? command, string? label = null)
    {
        var normalized = MercuryState.NormalizeCommand(command);
        if (normalized.Length == 0)
            return CommandResult.Fail("要加入扩展坞的指令不能为空。");
        if (!MercuryState.AddCommand(normalized, label))
            return CommandResult.Fail("常驻指令未加入扩展坞。");
        return CommandResult.Ok($"已加入常驻指令：{normalized}。");
    }

    public static CommandResult RemoveDockCommand(string? command)
    {
        var normalized = MercuryState.NormalizeCommand(command);
        if (normalized.Length == 0)
            return CommandResult.Fail("要移除的指令不能为空。");
        return MercuryState.RemoveCommand(normalized)
            ? CommandResult.Ok($"已移除常驻指令：{normalized}。")
            : CommandResult.Fail($"未找到常驻指令：{normalized}。");
    }

    public static IReadOnlyList<DockUsageRow> ListUsage()
        => MercuryState.Projects
            .Select(item => new DockUsageRow(
                item.Name,
                Math.Round(item.Weight, 3),
                Math.Round(item.Clicks, 3),
                item.LastOpened,
                item.Pinned,
                item.Excluded))
            .ToList();

    public static string ForgetUsage(string? name = null)
    {
        MercuryState.Forget(name);
        return string.IsNullOrWhiteSpace(name) ? "已清除使用历史。" : $"已清除 {name} 的使用历史。";
    }

    public static string ExcludeProject(string name)
    {
        MercuryState.Exclude(name, excluded: true);
        return $"已排除 {name}。";
    }

    public static string IncludeProject(string name)
    {
        MercuryState.Exclude(name, excluded: false);
        return $"已重新纳入 {name}。";
    }

    public static string SetDockPolicy(int? min = null, int? max = null, double? halflife = null)
    {
        if (min != null || max != null || halflife != null)
            MercuryState.SetPolicy(min, max, halflife);
        var current = MercuryState.Policy;
        return $"策略：min={current.MinItems}，max={current.MaxItems}，halflife={current.HalfLifeDays}。";
    }

    public static CommandResult OpenProject(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return CommandResult.Fail("用法：mercury.proj.open <name>。");
        if (!MercuryState.TryResolveWorktreeProject(name, out var projectName, out var path))
            return CommandResult.Fail($"未找到项目：{name}。");
        try
        {
            MercuryState.RecordOpen(projectName);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return CommandResult.Ok($"已打开 {projectName}。");
        }
        catch (Exception ex)
        {
            return CommandResult.Fail($"无法打开 {projectName}：{ex.GetType().Name}。");
        }
    }
}

public sealed record DockUsageRow(
    string Name,
    double Weight,
    double Clicks,
    DateTimeOffset? LastOpened,
    bool Pinned,
    bool Excluded);

public sealed record ExplorerEntryStatus(bool Registered, string Path);
