using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.CommandSurface;
using HistoryVulcan.Shell.Mcp;
using System.Windows;
using System.Windows.Controls;

namespace Mercury.CommandSurface;

/// <summary>全部注册指令及其 MCP 投影的可筛选列表。</summary>
public partial class McpToolsView : UserControl
{
    private readonly Func<CommandBus?> _busAccessor;
    private readonly CommandSelectionState _selection;
    private CommandCatalogSession? _catalogSession;
    private bool _initialLoadDone;
    private bool _updatingList;
    private bool _updatingFilters;

    public McpToolsView(Func<CommandBus?> busAccessor, CommandSelectionState selection)
        : this(busAccessor, selection, null)
    {
    }

    public McpToolsView(
        Func<CommandBus?> busAccessor,
        CommandSelectionState selection,
        CommandCatalogSession? catalogSession)
    {
        InitializeComponent();
        _busAccessor = busAccessor;
        _selection = selection;
        _catalogSession = catalogSession;
        Loaded += async (_, _) =>
        {
            var session = EnsureSession();
            if (session == null)
                return;
            session.Changed -= OnCatalogChanged;
            session.Changed += OnCatalogChanged;
            if (!_initialLoadDone)
            {
                _initialLoadDone = true;
                await RefreshAsync();
            }
        };
        Unloaded += (_, _) =>
        {
            if (_catalogSession != null)
                _catalogSession.Changed -= OnCatalogChanged;
        };
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync(force: true);

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
        if (Dispatcher.CheckAccess())
        {
            HandleCatalogChanged(e);
            return;
        }

        Dispatcher.BeginInvoke(() => HandleCatalogChanged(e));
    }

    private void HandleCatalogChanged(CommandCatalogChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        if (e.Kind == CommandCatalogChangeKind.Invalidated)
        {
            _ = RefreshAsync();
            return;
        }
        SyncFilterControls();
        RenderList();
    }

    private async Task RefreshAsync(bool force = false)
    {
        if (_busAccessor() is not { } || EnsureSession() is not { } session)
            return;

        RefreshButton.IsEnabled = false;
        try
        {
            if (!await session.RefreshAsync(force))
            {
                StatusText.Text = "命令集加载失败，详见控制台";
                return;
            }

            if (_selection.CurrentCommandName is { } current
                && !session.AllRows.Any(row => row.CommandName.Equals(current, StringComparison.OrdinalIgnoreCase)))
            {
                _selection.CurrentCommandName = null;
            }

            SyncFilterControls();
            RenderList();
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void SyncFilterControls()
    {
        if (_catalogSession == null)
            return;
        _updatingFilters = true;
        try
        {
            var selected = _catalogSession.CurrentFilter.Domain;
            var values = new List<string> { "全部" };
            values.AddRange(_catalogSession.Domains);
            DomainFilterBox.ItemsSource = values;
            DomainFilterBox.SelectedItem = values.Contains(selected, StringComparer.OrdinalIgnoreCase)
                ? values.First(value => value.Equals(selected, StringComparison.OrdinalIgnoreCase))
                : "全部";
            var classValues = new List<string> { "全部" };
            classValues.AddRange(_catalogSession.Classes);
            ClassFilterBox.ItemsSource = classValues;
            ClassFilterBox.SelectedItem = classValues.Contains(
                _catalogSession.CurrentFilter.CommandClass,
                StringComparer.OrdinalIgnoreCase)
                ? classValues.First(value => value.Equals(
                    _catalogSession.CurrentFilter.CommandClass,
                    StringComparison.OrdinalIgnoreCase))
                : "全部";
            ClassFilterBox.IsEnabled = _catalogSession.CurrentFilter.Domain != "全部";
        }
        finally
        {
            _updatingFilters = false;
        }
    }

    private void ApplyMcpFilter()
    {
        if (!_initialLoadDone || _catalogSession == null || _updatingFilters)
            return;
        _catalogSession.SetFilter(_catalogSession.CurrentFilter with
        {
            McpFilter = McpFilterBox.SelectedIndex,
        });
    }

    private void RenderList()
    {
        if (_catalogSession == null)
            return;
        var list = _catalogSession.VisibleRows;
        var selectedName = _catalogSession.SelectedCommandName;
        _updatingList = true;
        try
        {
            ToolList.ItemsSource = list;
            ToolList.SelectedItem = selectedName == null
                ? null
                : list.FirstOrDefault(row => row.CommandName.Equals(
                    selectedName,
                    StringComparison.OrdinalIgnoreCase));
            if (ToolList.SelectedItem != null)
                ToolList.ScrollIntoView(ToolList.SelectedItem);
        }
        finally
        {
            _updatingList = false;
        }

        var allRows = _catalogSession.AllRows;
        var hardExcluded = allRows.Count(row => row.McpState == "hidden");
        var readonlyCount = allRows.Count(row => row.McpState == "readonly");
        var standardCount = allRows.Count(row => row.McpState == "standard");
        var dangerous = allRows.Count(row => row.McpState == "dangerous");
        var modules = allRows.Count(row => row.Source == "module");
        StatusText.Text = $"显示 {list.Count}/{allRows.Count} 条；硬排除 {hardExcluded}，" +
                          $"只读 {readonlyCount}，标准 {standardCount}，危险拒绝 {dangerous}，模块 {modules}";
    }

    private void OnFilterChanged(object sender, EventArgs e) => ApplyMcpFilter();

    private void OnDomainFilterChanged(object sender, EventArgs e)
    {
        if (_updatingFilters || !IsLoaded || DomainFilterBox.SelectedItem is not string domain)
            return;
        if (_busAccessor() is { } bus)
            _ = bus.ExecuteAsync($"vulcan.log.source source={CommandParser.QuoteArg(domain)}", "UI");
    }

    private void OnClassFilterChanged(object sender, EventArgs e)
    {
        if (_updatingFilters || !IsLoaded || ClassFilterBox.SelectedItem is not string commandClass)
            return;
        if (_busAccessor() is { } bus)
            _ = bus.ExecuteAsync($"vulcan.log.class class={CommandParser.QuoteArg(commandClass)}", "UI");
    }

    private void OnToolSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_updatingList && ToolList.SelectedItem is CommandCatalogRow row)
            _catalogSession?.Select(row.CommandName);
    }
}
