using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Mercury;

/// <summary>
/// 坞上「+」磁贴弹出的项目列表：库里全部项目，可搜索，点一行就固定到坞上（已固定的再点一次取消）。
/// </summary>
/// <remarks>
/// 5.3.0 起它取代了 Aurora 里的「扩展坞管理」页（用户定：不再往前端注册场景）。
///
/// **它是一个会激活的独立窗口，不是坞里的 Popup。** 坞带 WS_EX_NOACTIVATE 永不拿焦点，
/// 挂在它下面的 Popup 里的搜索框收不到键盘输入。这里单开一个普通顶层窗口、显示时激活，
/// 失去激活（点了别处）就自己关掉，读起来仍是一个弹出层。
///
/// 外观跟坞一样自持颜色，圆角卡片、不加投影。
/// </remarks>
internal sealed class ProjectPickerWindow : Window
{
    private const double PickerWidth = 340;
    private const double PickerHeight = 440;
    private const int Gap = 8;

    private readonly IntPtr _anchor;
    private readonly TextBox _query;
    private readonly ListBox _list;
    private readonly TextBlock _status;
    private bool _closing;

    private ProjectPickerWindow(IntPtr anchor)
    {
        _anchor = anchor;
        Width = PickerWidth;
        Height = PickerHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        FontFamily = DockTheme.FontFamily;
        FontSize = DockTheme.BodyFontSize;
        // 搜索框、滚动条、列表选中都照 OneHistory 风格（见 DockStyles）。
        Resources.MergedDictionaries.Add(DockStyles.Create());

        _query = new TextBox
        {
            Style = (Style)Resources[DockStyles.SearchBoxKey],
            FontFamily = DockTheme.FontFamily,
            FontSize = DockTheme.BodyFontSize,
            ToolTip = "按名称或编号搜索；空格分开的几个词都要命中",
        };
        _query.TextChanged += (_, _) => Fill();
        _query.PreviewKeyDown += OnQueryKey;

        var hint = new TextBlock
        {
            Text = "搜索项目名或编号",
            Foreground = DockTheme.Muted,
            Margin = new Thickness(11, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
        _query.TextChanged += (_, _) => hint.Visibility = _query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var search = new Grid();
        search.Children.Add(_query);
        search.Children.Add(hint);

        _status = new TextBlock
        {
            Foreground = DockTheme.Muted,
            FontSize = DockTheme.SmallFontSize,
            Margin = new Thickness(2, 6, 2, 6),
            TextWrapping = TextWrapping.Wrap,
        };

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = DockTheme.Ink,
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            ItemContainerStyle = (Style)Resources[DockStyles.ItemKey],
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.PreviewKeyDown += OnListKey;

        var layout = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(search, System.Windows.Controls.Dock.Top);
        DockPanel.SetDock(_status, System.Windows.Controls.Dock.Top);
        layout.Children.Add(search);
        layout.Children.Add(_status);
        layout.Children.Add(_list);

        Content = new Border
        {
            Background = DockTheme.PanelBackground,
            BorderBrush = DockTheme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Child = layout,
        };

        SourceInitialized += (_, _) =>
            DesktopLayer.PlaceAbove(new WindowInteropHelper(this).Handle, _anchor, Gap);
        Loaded += (_, _) =>
        {
            Activate();
            _query.Focus();
        };
        // 点了别处就收起：这正是弹出层的行为。
        Deactivated += (_, _) => SafeClose();
        MercuryState.Changed += OnStateChanged;
        Closed += (_, _) => MercuryState.Changed -= OnStateChanged;
        Fill();
    }

    /// <summary>在坞的线程上弹出项目列表，贴在坞的上方、右边对齐。</summary>
    public static ProjectPickerWindow Open(IntPtr dock)
    {
        var picker = new ProjectPickerWindow(dock);
        picker.Show();
        return picker;
    }

    /// <summary>
    /// 搜索：按空白切成几个词，每个词都要在项目名里出现（不分大小写）。编号就在目录名里，所以编号也搜得到。
    /// 纯函数，Smoke 直接断言。
    /// </summary>
    internal static IReadOnlyList<DockProject> Filter(IEnumerable<DockProject> projects, string? query)
    {
        var terms = (query ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return projects
            .Where(project => terms.All(term => project.Name.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(Fill);

    private void Fill()
    {
        var all = MercuryState.AllProjects;
        var shown = Filter(all, _query.Text);
        var pinned = all.Count(project => project.Pinned && !project.Excluded);
        var selected = (_list.SelectedItem as ListBoxItem)?.Tag as string;

        _list.Items.Clear();
        foreach (var project in shown)
        {
            var item = Row(project);
            _list.Items.Add(item);
            if (string.Equals(project.Name, selected, StringComparison.OrdinalIgnoreCase))
                _list.SelectedItem = item;
        }

        _status.Text = all.Count == 0
            ? "还没扫描到项目，稍等片刻。"
            : $"已固定 {pinned}/{DockPolicy.ItemCount} · 点一行固定到坞上，已固定的再点取消";
    }

    private ListBoxItem Row(DockProject project)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // 名字不写死颜色：跟着行的 Foreground 走，选中时由样式换成墨色块上的反白。
        var name = new TextBlock
        {
            Text = project.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var state = new TextBlock
        {
            Text = project.Excluded ? "已排除" : project.Pinned ? "已固定" : string.Empty,
            FontSize = DockTheme.SmallFontSize,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(state, 1);
        grid.Children.Add(name);
        grid.Children.Add(state);

        var item = new ListBoxItem
        {
            Content = grid,
            Tag = project.Name,
            ToolTip = project.Path,
            Cursor = Cursors.Hand,
        };
        // 「已固定」是金字；落在墨色块上要换成 on-ink 的那一档（网站风格 §2.4），否则米底上看不见。
        var pinned = project.Pinned && !project.Excluded;
        void Paint() => state.Foreground = item.IsSelected
            ? pinned ? DockTheme.AccentOnInk : DockTheme.MutedOnInk
            : pinned ? DockTheme.Accent : DockTheme.Muted;
        item.Selected += (_, _) => Paint();
        item.Unselected += (_, _) => Paint();
        Paint();
        item.PreviewMouseLeftButtonUp += (_, _) => Toggle(project);
        return item;
    }

    /// <summary>固定 / 取消固定放到后台做：它要同步快捷方式文件夹并通知外壳，不该卡住这个窗口。</summary>
    private async void Toggle(DockProject project)
    {
        var pin = !(project.Pinned && !project.Excluded);
        var outcome = await Task.Run(() => MercuryState.Pin(project.Name, pin));
        if (outcome == PinOutcome.Full)
            _status.Text = $"坞上已固定满 {DockPolicy.ItemCount} 个，先在这里取消一个再加 {project.Name}。";
        else if (outcome == PinOutcome.NotFound)
            _status.Text = $"找不到 {project.Name}，可能刚被改名或删除。";
        // 成功时 MercuryState.Changed 会重排这张表。
    }

    private void OnQueryKey(object sender, KeyEventArgs args)
    {
        switch (args.Key)
        {
            case Key.Escape:
                SafeClose();
                args.Handled = true;
                break;
            case Key.Down when _list.Items.Count > 0:
                _list.SelectedIndex = 0;
                (_list.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                args.Handled = true;
                break;
            case Key.Enter when _list.Items.Count > 0:
                ToggleAt(_list.SelectedIndex < 0 ? 0 : _list.SelectedIndex);
                args.Handled = true;
                break;
        }
    }

    private void OnListKey(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape)
        {
            SafeClose();
            args.Handled = true;
        }
        else if (args.Key == Key.Enter && _list.SelectedIndex >= 0)
        {
            ToggleAt(_list.SelectedIndex);
            args.Handled = true;
        }
        else if (args.Key == Key.Up && _list.SelectedIndex == 0)
        {
            _query.Focus();
            args.Handled = true;
        }
    }

    private void ToggleAt(int index)
    {
        if ((_list.Items[index] as ListBoxItem)?.Tag is not string name)
            return;
        var project = MercuryState.AllProjects.FirstOrDefault(item =>
            item.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (project != null)
            Toggle(project);
    }

    private void SafeClose()
    {
        if (_closing)
            return;
        _closing = true;
        Close();
    }
}
