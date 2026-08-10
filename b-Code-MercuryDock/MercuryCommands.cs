using System.Diagnostics;
using HistoryVulcan.Core.Commands;

namespace Mercury;

public static class MercuryCommands
{
    public static ExplorerEntryStatus Status()
        => new(ExplorerNamespaceRegistration.IsRegistered(), DockShortcutFolder.Path);

    /// <summary>
    /// 切换控制台的域聚焦。聚焦状态存放在共享的命令目录会话里，与控制台域筛选下拉同源，
    /// 因此这里只写会话，下拉会自己跟上。
    /// </summary>
    /// <param name="domain">要聚焦的域；空表示退出聚焦。</param>
    public static string Go(string? domain)
    {
        var session = MercuryUiModule.CatalogSession;
        if (session == null)
            return "命令目录会话未就绪，无法切换域聚焦。";

        var requested = string.IsNullOrWhiteSpace(domain) ? DomainFocus.All : domain.Trim();
        if (!session.TrySetDomain(requested, out var available))
            return $"未知指令域 {requested}；可选：{string.Join("、", available)}。";

        return DomainFocus.IsUnfocused(requested)
            ? "已退出域聚焦，恢复全部指令域。"
            : $"已聚焦到 {requested} 域；之后只需输入「类.方法」，"
              + "输入其他已注册域的完整名仍可直接执行。";
    }

    public static string RegisterExplorer()
    {
        DockShortcutFolder.Synchronize(MercuryState.Projects);
        return ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path).Message;
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

    public static string OpenHost()
        => HistoryVulcanLauncher.Open() ? "已激活正在运行的 HistoryVulcan 窗口。" : "已启动 HistoryVulcan。";

    public static async Task<CommandResult> WakeConsoleAsync()
    {
        var bus = MercuryUiModule.Bus;
        if (bus == null)
            return CommandResult.Fail("指令总线未就绪。");

        // 冷启动走 Vulcan --focus-console（App 内组合窗口指令）；已连接则再组合 win/log。
        var show = await bus.ExecuteAsync(
                "vulcan.app.show startup=--focus-console",
                "Mercury")
            .ConfigureAwait(false);
        if (IsFrontendStarting(show))
            return CommandResult.Ok("前端正在启动，将聚焦控制台");
        if (!show.Success)
            return CommandResult.Fail(string.IsNullOrWhiteSpace(show.Message) ? "唤出前端失败" : show.Message);

        var showPane = await bus.ExecuteAsync("vulcan.ui.show name=console", "Mercury").ConfigureAwait(false);
        var focus = await bus.ExecuteAsync("vulcan.log.focus", "Mercury").ConfigureAwait(false);
        // Maximize last: vulcan.log.focus 内部 Show 可能退出最大化态。
        var maximize = await bus.ExecuteAsync("vulcan.ui.max name=console", "Mercury").ConfigureAwait(false);
        if (show.Success && showPane.Success && focus.Success && maximize.Success)
            return CommandResult.Ok("已通过 Vulcan 窗口指令唤出并聚焦控制台");

        var parts = new[] { show.Message, showPane.Message, focus.Message, maximize.Message }
            .Where(static text => !string.IsNullOrWhiteSpace(text));
        return CommandResult.Fail(string.Join("；", parts));
    }

    private static bool IsFrontendStarting(CommandResult result)
        => result.Success
           && result.Message.Contains("前端正在启动", StringComparison.Ordinal);

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
