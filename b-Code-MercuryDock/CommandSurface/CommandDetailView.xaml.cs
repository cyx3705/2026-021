using HistoryVulcan.Core;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.CommandSurface;
using HistoryVulcan.Services.Mcp;
using System.Windows;
using System.Windows.Controls;

namespace Mercury.CommandSurface;

/// <summary>所选指令的 Help、MCP 映射与目录状态详情。</summary>
public partial class CommandDetailView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly CommandSelectionState _selection;
    private CommandCatalogSession? _catalogSession;
    private CommandCatalogRow? _current;

    public CommandDetailView(Func<CommandBus?> busAccessor, CommandSelectionState selection)
        : this(busAccessor, selection, null)
    {
    }

    public CommandDetailView(
        Func<CommandBus?> busAccessor,
        CommandSelectionState selection,
        CommandCatalogSession? catalogSession)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        _catalogSession = catalogSession;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _selection.Changed -= OnSelectionChanged;
        _selection.Changed += OnSelectionChanged;
        if (EnsureSession() is { } session)
        {
            session.Changed -= OnCatalogChanged;
            session.Changed += OnCatalogChanged;
        }
        await LoadSelectionAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _selection.Changed -= OnSelectionChanged;
        if (_catalogSession != null)
            _catalogSession.Changed -= OnCatalogChanged;
    }

    private CommandCatalogSession? EnsureSession()
    {
        if (_catalogSession != null)
            return _catalogSession;
        if (_busAccessor() is not { } bus)
            return null;
        _catalogSession = new CommandCatalogSession(bus, _selection);
        return _catalogSession;
    }

    private void OnCatalogChanged(object? sender, CommandCatalogChangedEventArgs e)
    {
        if (e.Kind != CommandCatalogChangeKind.Snapshot)
            return;
        Dispatcher.BeginInvoke(async () => await LoadSelectionAsync());
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        var unloaded = _current != null && _selection.CurrentCommandName == null;
        Dispatcher.BeginInvoke(async () =>
        {
            if (unloaded && _selection.CurrentCommandName == null)
                ClearDetails();
            else
                await LoadSelectionAsync();
        });
    }

    private bool IsCurrent(string commandName)
        => string.Equals(_selection.CurrentCommandName, commandName, StringComparison.OrdinalIgnoreCase);

    private async Task LoadSelectionAsync()
    {
        var commandName = _selection.CurrentCommandName;
        if (commandName == null)
        {
            // 空选中态不显示任何详情行(3.1 修订)
            ClearDetails();
            return;
        }

        if (EnsureSession() is not { } session)
            return;

        var detail = await session.GetDetailAsync(commandName);
        if (!IsCurrent(commandName))
            return;
        if (detail == null)
        {
            _selection.CurrentCommandName = null;
            ClearDetails();
            return;
        }

        _current = detail.Command;
        DetailTabs.IsEnabled = true;
        SummaryBox.Text = detail.Command.Summary;
        ExampleBox.Text = detail.Command.Example ?? "(无示例)";
        ParameterList.ItemsSource = detail.Parameters;
        CopyExampleButton.IsEnabled = !string.IsNullOrWhiteSpace(detail.Command.Example);
        var reason = detail.Command.HardExclusionReason == null
            ? string.Empty
            : $"\n硬排除原因: {detail.Command.HardExclusionReason}";
        McpStatusText.Text = $"状态: {detail.Command.McpState} | 当前策略可见: " +
                             $"{(detail.Command.PolicyVisible ? "是" : "否")} | " +
                             $"工具名: {detail.Command.McpToolName ?? "(无)"}{reason}";
        SchemaBox.Text = detail.McpInputSchema ?? "该指令没有 MCP 工具形态";
        ShowSchemaButton.IsEnabled = detail.Command.McpToolName != null
                                     && session.ContainsCommand("vulcan.mcp.schema");

        UpdateDescriptionStatus(detail.Command);
    }

    private async void OnHelpClick(object sender, RoutedEventArgs e)
    {
        if (_current is { } row)
            await (_busAccessor()?.ExecuteAsync($"vulcan.command.help {CommandParser.QuoteArg(row.CommandName)}", "UI")
                   ?? Task.FromResult(CommandResult.Fail("总线未就绪")));
    }

    private async void OnCopyExampleClick(object sender, RoutedEventArgs e)
    {
        if (_current?.Example is not { Length: > 0 } || _busAccessor() is not { } bus)
            return;
        var result = await bus.ExecuteAsync(
            $"vulcan.command.copyexample name={CommandParser.QuoteArg(_current.CommandName)}", "UI");
        StatusText.Text = result.Success ? "示例已复制" : "复制失败，详见控制台";
    }

    private async void OnSchemaClick(object sender, RoutedEventArgs e)
    {
        if (_current is { McpToolName: not null } row)
            await (_busAccessor()?.ExecuteAsync(
                       $"vulcan.mcp.schema name={CommandParser.QuoteArg(row.CommandName)}", "UI")
                   ?? Task.FromResult(CommandResult.Fail("总线未就绪")));
    }

    private void UpdateDescriptionStatus(CommandCatalogRow row)
    {
        var projection = row.McpToolName == null ? "未投影为 MCP 工具" : "已投影为 MCP 工具";
        RevisionStatusText.Text =
            $"{projection} | 当前修订: {row.CurrentRevision ?? "(默认)"} | " +
            $"自定义: {(row.Customized ? "是" : "否")} | " +
            $"待处理提案: {row.OpenProposals} | 事故: {row.IncidentCount}";
        DefaultDescBox.Text = row.Summary +
                              (row.Example == null ? "" : $"\n示例: {row.Example}");
    }

    private void ClearDetails()
    {
        _current = null;
        DetailTabs.IsEnabled = false;
        StatusText.Text = "";
        SummaryBox.Text = "";
        ParameterList.ItemsSource = null;
        ExampleBox.Text = "";
        McpStatusText.Text = "";
        SchemaBox.Text = "";
        RevisionStatusText.Text = "当前修订: (默认)";
        DefaultDescBox.Text = "";
    }
}
