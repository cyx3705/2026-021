using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;

namespace Mercury.CommandSurface;

/// <summary>
/// Mercury-owned command workbench: catalog session, command-set and detail tool windows.
/// Console remains in Vulcan Shell; this feature attaches the shared session via
/// <see cref="IShellCommandWorkbenchHost"/>.
/// </summary>
public sealed class CommandSurfaceFeature : IDisposable
{
    private readonly CommandCatalogSession _session;
    private readonly List<IDisposable> _registrations = [];
    private bool _disposed;

    private CommandSurfaceFeature(CommandCatalogSession session)
    {
        _session = session;
    }

    public CommandCatalogSession Session => _session;

    /// <summary>
    /// Creates the shared catalog session, registers mcp/commanddetail windows, and attaches
    /// the session to the Shell console deferred proxy.
    /// </summary>
    public static CommandSurfaceFeature? TryAttach(
        IShellCommandWorkbenchHost? host,
        IShellUiRegistrar? shellUi)
    {
        if (host == null || shellUi == null)
            return null;

        var session = new CommandCatalogSession(host.Bus, host.CommandSelection);
        host.AttachCommandCatalogSession(session);

        var feature = new CommandSurfaceFeature(session);
        var selection = host.CommandSelection;
        CommandBus Bus() => host.Bus;

        feature._registrations.Add(shellUi.RegisterToolWindow(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.Mcp,
            Title = "命令集",
            DefaultSide = DockSide.Center,
            DefaultRatio = 1,
            IsSingleton = true,
            ContentFactory = () => new McpToolsView(Bus, selection, session),
        }, "HistoryMercury"));

        feature._registrations.Add(shellUi.RegisterToolWindow(new ToolWindowDescriptor
        {
            Id = StandardWindowIds.CommandDetail,
            Title = "指令详情",
            // 左侧：右侧默认不再放页面。宽度与其他左侧页取同一个 0.38，
            // 否则左栏宽度会取决于哪个模块最后装载。
            DefaultSide = DockSide.Left,
            DefaultRatio = 0.38,
            IsSingleton = true,
            ContentFactory = () => new CommandDetailView(Bus, selection, session),
        }, "HistoryMercury"));

        return feature;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var registration in _registrations)
            registration.Dispose();
        _registrations.Clear();
        _session.Dispose();
    }
}
