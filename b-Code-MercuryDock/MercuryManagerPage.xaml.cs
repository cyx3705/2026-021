using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Extensibility.CommandSurface;
using Mercury.CommandSurface;
using Microsoft.Win32;

namespace Mercury;

public sealed partial class MercuryManagerPage : UserControl
{
    private const double CompactWidth = 720;
    private readonly CommandCompletionEngine _completion = new();
    private IReadOnlyList<CommandCompletionDefinition> _definitions =
        MercuryCommandCatalog.CreateDescriptors()
            .Select(CommandCatalogSession.CreateCompletionDefinition)
            .ToList();
    private string _addKind = "command";
    private bool _busy;

    public MercuryManagerPage()
    {
        InitializeComponent();
        EntryInput.Source = EntryOptions;
        SizeChanged += (_, _) => UpdateResponsiveColumns();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        MercuryState.StartWatching();
        MercuryState.Changed -= OnStateChanged;
        MercuryState.Changed += OnStateChanged;
        LoadPolicy();
        Reload();
        UpdateResponsiveColumns();
        await LoadCommandCatalogAsync();
        await ExecuteAsync("mercury.proj.refresh", "正在刷新项目...");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
        => MercuryState.Changed -= OnStateChanged;

    private void OnStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        LoadPolicy();
        Reload();
    });

    private void OnFilterChanged(object sender, EventArgs e) => Reload();

    private void Reload()
    {
        if (DockList == null || TypeFilterBox == null)
            return;
        var selectedKey = (DockList.SelectedItem as DockManagerRow)?.Key;
        var filter = SearchBox?.Text.Trim() ?? string.Empty;
        var type = (TypeFilterBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var rows = MercuryState.Projects.Select(DockManagerRow.FromProject)
            .Concat(MercuryState.CommandEntries.Select(DockManagerRow.FromCommand))
            .Where(row => type == "all" || row.Type == type)
            .Where(row => filter.Length == 0
                || row.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Command.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        DockList.ItemsSource = rows;
        DockList.SelectedItem = selectedKey == null
            ? null
            : rows.FirstOrDefault(row => row.Key == selectedKey);
        StatusText.Text = $"显示 {rows.Count} 条扩展坞项目与常驻项";
    }

    private void OnAddMenuClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu })
        {
            menu.PlacementTarget = AddButton;
            menu.IsOpen = true;
        }
    }

    private void OnAddKindClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string kind })
            return;
        _addKind = kind;
        EditorBar.Visibility = Visibility.Visible;
        BrowseButton.Visibility = kind == "shortcut" ? Visibility.Visible : Visibility.Collapsed;
        EditorLabel.Text = kind switch
        {
            "project" => "项目",
            "shortcut" => "快捷文件",
            _ => "命令",
        };
        EntryInput.Text = kind == "command" ? "mercury." : string.Empty;
        EntryInput.Focus();
    }

    private async void OnConfirmAddClick(object sender, RoutedEventArgs e)
    {
        var value = EntryInput.Text.Trim();
        if (value.Length == 0)
        {
            SetStatus("请输入要加入的内容。");
            return;
        }
        var command = _addKind switch
        {
            "project" => "mercury.proj.add " + CommandParser.QuoteArg(value),
            "shortcut" => "mercury.shortcut.add " + CommandParser.QuoteArg(value),
            _ => "mercury.dock.add " + CommandParser.QuoteArg(value),
        };
        if (await ExecuteAsync(command, "正在加入扩展坞..."))
        {
            EntryInput.Text = string.Empty;
            EditorBar.Visibility = Visibility.Collapsed;
        }
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择要加入扩展坞的快捷文件或普通文件",
            Filter = "快捷文件 (*.lnk)|*.lnk|所有文件 (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() == true)
            EntryInput.Text = dialog.FileName;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await ExecuteAsync("mercury.proj.refresh", "正在刷新项目...");

    private void OnPolicyClick(object sender, RoutedEventArgs e)
    {
        LoadPolicy();
        PolicyPopup.IsOpen = true;
    }

    private async void OnApplyPolicyClick(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(MinItemsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var min)
            || !int.TryParse(MaxItemsBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var max)
            || !double.TryParse(HalfLifeBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var halfLife))
        {
            SetStatus("策略值格式不正确。");
            return;
        }
        var command = FormattableString.Invariant(
            $"mercury.dock.policy min={min} max={max} halflife={halfLife}");
        if (await ExecuteAsync(command, "正在保存策略..."))
            PolicyPopup.IsOpen = false;
    }

    private void LoadPolicy()
    {
        if (MinItemsBox == null)
            return;
        var policy = MercuryState.Policy;
        if (!MinItemsBox.IsKeyboardFocusWithin)
            MinItemsBox.Text = policy.MinItems.ToString(CultureInfo.InvariantCulture);
        if (!MaxItemsBox.IsKeyboardFocusWithin)
            MaxItemsBox.Text = policy.MaxItems.ToString(CultureInfo.InvariantCulture);
        if (!HalfLifeBox.IsKeyboardFocusWithin)
            HalfLifeBox.Text = policy.HalfLifeDays.ToString("G", CultureInfo.InvariantCulture);
    }

    private async void OnRowActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: DockManagerRow row })
            await ExecuteRowPrimaryAsync(row);
    }

    private async void OnRowPrimaryClick(object sender, RoutedEventArgs e)
    {
        if (DockList.SelectedItem is DockManagerRow row)
            await ExecuteRowPrimaryAsync(row);
    }

    private Task<bool> ExecuteRowPrimaryAsync(DockManagerRow row)
        => ExecuteAsync(row.PrimaryCommand, row.IsProject ? "正在更新固定状态..." : "正在移除常驻项...");

    private async void OnExcludeClick(object sender, RoutedEventArgs e)
    {
        if (DockList.SelectedItem is DockManagerRow { IsProject: true } row)
            await ExecuteAsync("mercury.proj.exclude " + CommandParser.QuoteArg(row.Name), "正在排除项目...");
    }

    private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = DockList.SelectedItem as DockManagerRow;
        RowPrimaryMenuItem.Visibility = row == null ? Visibility.Collapsed : Visibility.Visible;
        RowPrimaryMenuItem.Header = row?.ActionText;
        ExcludeMenuItem.Visibility = row?.IsProject == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject) is { } item)
            DockList.SelectedItem = item.DataContext;
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
        => CompactDetail.Visibility = ActualWidth < CompactWidth && DockList.SelectedItem != null
            ? Visibility.Visible
            : Visibility.Collapsed;

    private async Task<bool> ExecuteAsync(string command, string pending)
    {
        if (_busy)
            return false;
        if (MercuryUiModule.Bus is not { } bus)
        {
            SetStatus("指令总线未就绪。");
            return false;
        }
        _busy = true;
        IsEnabled = false;
        SetStatus(pending);
        try
        {
            var result = await bus.ExecuteAsync(command, "MercuryManager");
            SetStatus(string.IsNullOrWhiteSpace(result.Message)
                ? result.Success ? "操作完成。" : "操作失败。"
                : result.Message);
            return result.Success;
        }
        catch (Exception ex)
        {
            SetStatus($"操作失败：{ex.Message}");
            return false;
        }
        finally
        {
            _busy = false;
            IsEnabled = true;
        }
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private async Task LoadCommandCatalogAsync()
    {
        if (MercuryUiModule.CatalogSession is not { } session)
            return;
        try
        {
            _definitions = await session.CompletionDefinitionsAsync();
        }
        catch (Exception)
        {
            // Local descriptors remain a complete Mercury-only fallback.
        }
    }

    private IReadOnlyList<SuggestOption> EntryOptions(string text)
    {
        if (_addKind == "project")
        {
            return MercuryState.ListWorktreeProjects()
                .Where(value => text.Length == 0 || value.Contains(text, StringComparison.OrdinalIgnoreCase))
                .OrderBy(value => MatchRank(value, text))
                .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Select(value => new SuggestOption(value, value, "项目", false))
                .ToList();
        }
        if (_addKind != "command")
            return [];
        var result = _completion.Complete(text, text.Length, _definitions);
        return result.Candidates.Select(candidate => new SuggestOption(
            text.Remove(result.ReplaceStart, result.ReplaceLength)
                .Insert(result.ReplaceStart, candidate.InsertText),
            candidate.DisplayText,
            candidate.Description,
            candidate.Kind is ConsoleCompletionKind.Domain
                or ConsoleCompletionKind.Class
                or ConsoleCompletionKind.Method
                or ConsoleCompletionKind.Parameter)).ToList();
    }

    private static int MatchRank(string value, string filter)
        => value.Equals(filter, StringComparison.OrdinalIgnoreCase) ? 0
            : value.StartsWith(filter, StringComparison.OrdinalIgnoreCase) ? 1 : 2;

    private void UpdateResponsiveColumns()
    {
        if (NameColumn == null)
            return;
        var compact = ActualWidth < CompactWidth;
        CommandColumn.Width = compact ? 0 : Math.Max(180, ActualWidth - 646);
        WeightColumn.Width = compact ? 0 : 62;
        ClicksColumn.Width = compact ? 0 : 54;
        LastOpenedColumn.Width = compact ? 0 : 128;
        NameColumn.Width = compact ? Math.Max(104, ActualWidth - 232) : 180;
        TypeColumn.Width = compact ? 54 : 70;
        StateColumn.Width = compact ? 62 : 74;
        ActionColumn.Width = compact ? 72 : 76;
        CompactDetail.Visibility = compact && DockList.SelectedItem != null
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T match)
                return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }
}

internal sealed record DockManagerRow(
    string Key,
    string Name,
    string Type,
    string TypeText,
    string StateText,
    string Command,
    string WeightText,
    string ClicksText,
    string LastOpenedText,
    string ActionText,
    string PrimaryCommand,
    bool IsProject)
{
    public static DockManagerRow FromProject(DockProject project) => new(
        "proj:" + project.Name,
        project.Name,
        "project",
        "项目",
        project.Pinned ? "固定" : "自动",
        MercuryCommandCatalog.BuildOpenProjectCommand(project.Name),
        project.Weight.ToString("F2", CultureInfo.CurrentCulture),
        project.Clicks.ToString("F1", CultureInfo.CurrentCulture),
        project.LastOpened?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture) ?? "-",
        project.Pinned ? "取消固定" : "固定",
        (project.Pinned ? "mercury.proj.unpin " : "mercury.proj.pin ") + CommandParser.QuoteArg(project.Name),
        true);

    public static DockManagerRow FromCommand(DockCommandEntry entry)
    {
        var shortcut = entry.Command.StartsWith(
            MercuryCommandCatalog.ShortcutOpenCommandName + " ", StringComparison.OrdinalIgnoreCase);
        return new(
            "cmd:" + entry.Command,
            entry.Label,
            shortcut ? "shortcut" : "command",
            shortcut ? "快捷文件" : "命令",
            "常驻",
            entry.Command,
            "-",
            "-",
            "-",
            "移除",
            "mercury.dock.remove " + CommandParser.QuoteArg(entry.Command),
            false);
    }
}

internal sealed class NullToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
