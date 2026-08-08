using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HistoryVulcan.Core.Docking;

namespace MercuryDock;

/// <summary>
/// 扩展坞管理页面，停靠在 OHS 主窗口右侧。
/// </summary>
/// <remarks>
/// 管理页与桌面坞都运行在桌面 Shell 进程；dock.* 与 mercury.dock.open 指令注册在服务进程，
/// 经总线远程转发透明执行。所有状态写操作落到 state.json，由
/// <see cref="MercuryDockState.StartWatching"/> 的文件监视完成跨进程同步。
/// </remarks>
public static class MercuryDockManagerView
{
    public static ToolWindowDescriptor CreateDescriptor() => new()
    {
        Id = "dock.manager",
        Title = "扩展坞管理",
        DefaultSide = DockSide.Right,
        DefaultRatio = 0.38,
        IsSingleton = true,
        ContentFactory = static () => new ManagerPage(),
    };

    private sealed class ManagerPage : UserControl
    {
        private readonly ListView _list = new();
        private readonly ContextMenu _listMenu = new();
        private readonly TextBox _minItems = new() { Width = 56 };
        private readonly TextBox _maxItems = new() { Width = 56 };
        private readonly TextBox _halfLife = new() { Width = 56 };
        private readonly SuggestBox _commandInput = new();
        private readonly SuggestBox _argumentInput = new();
        private readonly TextBlock _status = new();
        private CommandOptionTree _commandTree = CommandOptionTree.Build(MercuryDockAliasCommands.FallbackCommandCatalog());
        private bool _commandsLoaded;

        public ManagerPage()
        {
            // 页面只有深色模式：整页铺深色底，避免透出宿主浅色停靠框背景。
            Background = DockTheme.PanelBackground;
            var root = new DockPanel
            {
                Margin = new Thickness(12),
                Background = DockTheme.PanelBackground,
            };
            root.SetValue(TextElement.FontFamilyProperty, DockTheme.FontFamily);
            root.SetValue(TextElement.FontSizeProperty, DockTheme.BodyFontSize);
            ConfigureInput(_minItems);
            ConfigureInput(_maxItems);
            ConfigureInput(_halfLife);

            var policyBar = new WrapPanel();
            policyBar.Children.Add(new TextBlock
            {
                Text = "最少显示",
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.BodyFontSize,
                Foreground = DockTheme.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            policyBar.Children.Add(_minItems);
            policyBar.Children.Add(Gap("最多显示"));
            policyBar.Children.Add(_maxItems);
            policyBar.Children.Add(Gap("半衰期(天)"));
            policyBar.Children.Add(_halfLife);

            // 指令框：无下拉箭头，输入即弹候选，Shift+W/S 移动、Tab 确认并逐级下钻（域→类→方法）。
            _commandInput.Hint = "要执行的总线指令：输入即弹出候选，Shift+W/S 上下移动，Tab 确认并逐级下钻（域→类→方法）";
            _commandInput.Source = text => _commandTree.ChildrenOf(text);
            _commandInput.Committed += _ => _argumentInput.Focus();
            // 常驻默认指令：可删除改输其他指令，候选来自服务侧全量清单。
            _commandInput.Text = MercuryDockAliasCommands.OpenCommandName;

            _argumentInput.Hint = "指令参数：打开项目时为项目名或编号，输入即过滤；其他指令自由输入";
            _argumentInput.Source = ArgumentOptions;
            _argumentInput.Committed += _ => AddEntry();

            var add = new Button
            {
                Content = "加入扩展坞",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockTheme.StyleButton(add, accent: true);
            add.Click += (_, _) => AddEntry();

            // 指令框独占一行，参数框与按钮同行，窄面板下不再换行截断。
            _commandInput.Margin = new Thickness(0, 8, 0, 0);
            var argRow = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            DockPanel.SetDock(add, Dock.Right);
            argRow.Children.Add(add);
            argRow.Children.Add(_argumentInput);

            var addBar = new StackPanel();
            addBar.Children.Add(_commandInput);
            addBar.Children.Add(argRow);

            _status.TextWrapping = TextWrapping.Wrap;
            _status.FontFamily = DockTheme.FontFamily;
            _status.FontSize = DockTheme.SmallFontSize;
            _status.Foreground = DockTheme.Muted;
            _status.Margin = new Thickness(0, 6, 0, 0);

            var optionsStack = new StackPanel();
            optionsStack.Children.Add(policyBar);
            optionsStack.Children.Add(addBar);
            optionsStack.Children.Add(_status);

            // 圆角矩形把策略与加入扩展坞收进同一块区域，与列表、按钮的 8 圆角保持一致。
            var optionsFrame = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderBrush = DockTheme.PanelBorder,
                BorderThickness = new Thickness(1),
                Background = DockTheme.SurfaceAlt,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 8),
                Child = optionsStack,
            };
            DockPanel.SetDock(optionsFrame, Dock.Top);
            root.Children.Add(optionsFrame);

            root.Children.Add(new TextBlock
            {
                Text = "列表只显示已入坞的项目与指令；点首列图钉固定/取消固定项目；右键可刷新、排除项目或移除指令；策略改完自动保存。"
                    + "指令框输入即弹候选：Shift+W/S 上下移动，Tab 确认并逐级下钻（域→类→方法）；默认 mercury.dock.open 打开项目目录，可改输任意总线指令，加入后常驻桌面坞。",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Muted,
                Margin = new Thickness(0, 0, 0, 6),
            });
            DockPanel.SetDock(root.Children[^1], Dock.Bottom);

            _list.View = BuildColumns();
            _list.Background = DockTheme.PanelBackground;
            _list.BorderBrush = DockTheme.PanelBorder;
            _list.BorderThickness = new Thickness(1);
            _list.Foreground = DockTheme.Label;
            _list.FontFamily = DockTheme.FontFamily;
            _list.FontSize = DockTheme.BodyFontSize;
            // 选中恒为淡黄底深字，聚焦与失焦一致，不再用白色或暗金。
            _list.Resources[SystemColors.HighlightBrushKey] = DockTheme.Selection;
            _list.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.SelectionText;
            _list.Resources[SystemColors.ControlBrushKey] = DockTheme.Selection;
            _list.Resources[SystemColors.ControlTextBrushKey] = DockTheme.SelectionText;
            // 右键按下先选中行，菜单里的排除/移除才有确定的作用对象。
            _list.PreviewMouseRightButtonDown += (_, args) =>
            {
                if (args.OriginalSource is DependencyObject source
                    && FindAncestor<ListViewItem>(source) is { } item)
                {
                    _list.SelectedItem = item.DataContext;
                }
            };
            _listMenu.Background = DockTheme.PanelBackground;
            _listMenu.Foreground = DockTheme.Label;
            _listMenu.BorderBrush = DockTheme.PanelBorder;
            _listMenu.FontFamily = DockTheme.FontFamily;
            _listMenu.FontSize = DockTheme.BodyFontSize;
            _listMenu.Resources[SystemColors.MenuBrushKey] = DockTheme.PanelBackground;
            _listMenu.Resources[SystemColors.MenuTextBrushKey] = DockTheme.Label;
            _listMenu.Resources[SystemColors.HighlightBrushKey] = DockTheme.Hover;
            _listMenu.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.Label;
            _list.ContextMenu = _listMenu;
            // 菜单实例必须在打开前挂好；打开事件里只换项，换实例会晚一拍显示旧菜单。
            _list.ContextMenuOpening += (_, _) => RebuildContextMenu();
            root.Children.Add(_list);
            Content = root;

            MercuryDockState.StartWatching();
            MercuryDockState.Changed += OnChanged;
            Unloaded += (_, _) => MercuryDockState.Changed -= OnChanged;
            Loaded += (_, _) =>
            {
                LoadPolicy();
                Reload();
                _ = LoadCommandCatalogAsync();
                _ = MercuryDockState.RefreshAsync();
            };
        }

        private static GridView BuildColumns()
        {
            var view = new GridView();
            view.Columns.Add(new GridViewColumn
            {
                Header = "固定",
                Width = 44,
                CellTemplate = BuildPinTemplate(),
            });
            view.Columns.Add(Column("名称", nameof(DockRow.Name), 140));
            view.Columns.Add(Column("指令", nameof(DockRow.Command), 220));
            view.Columns.Add(Column("权重", nameof(DockRow.Weight), 60, "F2"));
            view.Columns.Add(Column("点击", nameof(DockRow.Clicks), 50, "F1"));
            view.Columns.Add(Column("最近打开", nameof(DockRow.LastOpened), 120, "yyyy-MM-dd HH:mm"));
            return view;
        }

        /// <summary>图钉开关：项目行点击切换；指令行常驻，点击仅提示。</summary>
        private static DataTemplate BuildPinTemplate()
        {
            var template = new DataTemplate();
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(Button.ContentProperty, "📌");
            button.SetValue(Button.CursorProperty, Cursors.Hand);
            button.SetValue(Button.BackgroundProperty, Brushes.Transparent);
            button.SetValue(Button.BorderThicknessProperty, new Thickness(0));
            button.SetValue(Button.PaddingProperty, new Thickness(2, 0, 2, 0));
            button.SetBinding(UIElement.OpacityProperty, new Binding(nameof(DockRow.Pinned))
            {
                Converter = PinOpacityConverter.Instance,
            });
            button.SetBinding(Button.ToolTipProperty, new Binding(nameof(DockRow.IsCommand))
            {
                Converter = PinTipConverter.Instance,
            });
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
            {
                // 指令项恒为常驻，图钉只对项目行生效。
                if (sender is Button { DataContext: DockRow { IsCommand: false } row })
                    MercuryDockState.Pin(row.Name, !row.Pinned);
            }));
            template.VisualTree = button;
            return template;
        }

        private void RebuildContextMenu()
        {
            _listMenu.Items.Clear();

            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await MercuryDockState.RefreshAsync();
            _listMenu.Items.Add(refresh);

            if (_list.SelectedItem is not DockRow row)
                return;
            _listMenu.Items.Add(new Separator());
            if (row.IsCommand)
            {
                var remove = new MenuItem { Header = "移除该指令" };
                remove.Click += (_, _) =>
                {
                    if (MercuryDockState.RemoveCommand(row.Command))
                        SetStatus($"已移除指令 {row.Command}");
                };
                _listMenu.Items.Add(remove);
            }
            else
            {
                var exclude = new MenuItem { Header = "排除" };
                exclude.Click += (_, _) =>
                {
                    MercuryDockState.Exclude(row.Name, excluded: true);
                    SetStatus($"已排除 {row.Name}；在加入框选 mercury.dock.open 可找回");
                };
                _listMenu.Items.Add(exclude);
            }
        }

        private static T? FindAncestor<T>(DependencyObject node) where T : DependencyObject
        {
            while (node != null)
            {
                if (node is T match)
                    return match;
                node = VisualTreeHelper.GetParent(node);
            }
            return null;
        }

        private static GridViewColumn Column(string header, string path, double width, string? format = null)
            => new()
            {
                Header = header,
                Width = width,
                DisplayMemberBinding = new Binding(path)
                {
                    StringFormat = format,
                    ConverterCulture = CultureInfo.CurrentCulture,
                },
            };

        private static UIElement Gap(string text) => new TextBlock
        {
            Text = text,
            FontFamily = DockTheme.FontFamily,
            FontSize = DockTheme.BodyFontSize,
            Foreground = DockTheme.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 4, 0),
        };

        private void ConfigureInput(TextBox input)
        {
            input.Height = 28;
            input.Padding = DockTheme.ControlPadding;
            input.FontFamily = DockTheme.FontFamily;
            input.FontSize = DockTheme.BodyFontSize;
            input.Foreground = DockTheme.Label;
            input.Background = DockTheme.PanelBackground;
            input.BorderBrush = DockTheme.PanelBorder;
            input.BorderThickness = new Thickness(1);
            input.VerticalContentAlignment = VerticalAlignment.Center;
            // 策略即改即存：离开输入框或回车直接落盘，不再要"保存策略"按钮。
            input.LostFocus += (_, _) => ApplyPolicy();
            input.KeyDown += (_, args) =>
            {
                if (args.Key == Key.Enter)
                {
                    ApplyPolicy();
                    Keyboard.ClearFocus();
                }
            };
        }

        private void OnChanged() => Dispatcher.BeginInvoke(() =>
        {
            LoadPolicy();
            Reload();
        });

        /// <summary>列表只显示已入坞的条目：项目（固定+策略选中）加手动指令项。</summary>
        private void Reload()
        {
            var selected = (_list.SelectedItem as DockRow)?.Key;
            var rows = MercuryDockState.Projects
                .Select(project => new DockRow(
                    project.Name,
                    MercuryDockAliasCommands.BuildOpenCommandText(project.Name),
                    project.Pinned,
                    false,
                    project.Weight,
                    project.Clicks,
                    project.LastOpened))
                .Concat(MercuryDockState.CommandEntries.Select(entry => new DockRow(
                    entry.Label,
                    entry.Command,
                    true,
                    true,
                    0,
                    0,
                    null)))
                .ToList();
            _list.ItemsSource = rows;
            if (selected == null)
                return;
            foreach (var item in _list.Items)
            {
                if (item is DockRow row && row.Key == selected)
                {
                    _list.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>指令候选：服务侧全量清单（command.list 经远程转发），失败回退本模块静态清单。</summary>
        private async Task LoadCommandCatalogAsync()
        {
            if (_commandsLoaded)
                return;
            _commandsLoaded = true;

            IReadOnlyList<CommandCatalogItem> items = [];
            var bus = MercuryDockUiModule.Bus;
            if (bus != null)
            {
                try
                {
                    var result = await bus.ExecuteAsync("command.list", "UI").ConfigureAwait(false);
                    if (result.Success)
                        items = MercuryDockAliasCommands.ParseCommandCatalog(result.Data);
                }
                catch (Exception)
                {
                    // 服务不可达时回退静态清单，不打扰页面。
                }
            }

            await Dispatcher.BeginInvoke(() =>
            {
                if (items.Count == 0)
                    items = MercuryDockAliasCommands.FallbackCommandCatalog();
                // 服务侧清单可能来自旧版模块：保证常驻默认指令永远在候选树里。
                if (items.All(item => !item.Name.Equals(
                        MercuryDockAliasCommands.OpenCommandName, StringComparison.OrdinalIgnoreCase)))
                {
                    items = items.Concat(MercuryDockAliasCommands.FallbackCommandCatalog()
                        .Where(item => item.Name.Equals(
                            MercuryDockAliasCommands.OpenCommandName, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
                _commandTree = CommandOptionTree.Build(items);
            });
        }

        /// <summary>参数候选：打开项目指令时为工作树未入坞项目（排除项标注排前），其他指令自由输入。</summary>
        private IReadOnlyList<SuggestOption> ArgumentOptions(string text)
        {
            if (!IsOpenProjectCommand(_commandInput.Text))
                return [];

            var filter = text.Trim();
            var docked = MercuryDockState.Projects
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = MercuryDockState.AllProjects
                .Where(item => item.Excluded)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return MercuryDockState.ListWorktreeProjects()
                .Where(name => !docked.Contains(name))
                .Where(name => filter.Length == 0
                    || name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(name => (Name: name, Excluded: excluded.Contains(name)))
                .OrderByDescending(candidate => candidate.Excluded)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => new SuggestOption(
                    candidate.Name,
                    candidate.Name,
                    candidate.Excluded ? "已排除，加入即找回" : null,
                    false))
                .ToList();
        }

        /// <summary>加入：打开项目指令且参数命中工作树项目走 AddToDock，其余作为自定义指令常驻入坞。</summary>
        private void AddEntry()
        {
            var command = _commandInput.Text.Trim();
            var argument = _argumentInput.Text.Trim();
            if (command.Length == 0)
            {
                SetStatus("请输入要加入的指令");
                return;
            }

            if (IsOpenProjectCommand(command)
                && argument.Length > 0
                && MercuryDockState.AddToDock(argument))
            {
                SetStatus($"已加入扩展坞并固定 {argument}");
                _argumentInput.Text = string.Empty;
                return;
            }

            var text = argument.Length == 0
                ? command
                : command + " " + HistoryVulcan.Core.Commands.CommandParser.QuoteArg(argument);
            if (MercuryDockState.AddCommand(text))
            {
                SetStatus($"已加入指令 {text}");
                _argumentInput.Text = string.Empty;
            }
            else
            {
                SetStatus("指令为空，未加入");
            }
        }

        private static bool IsOpenProjectCommand(string? command)
            => string.Equals(
                command?.Trim(),
                MercuryDockAliasCommands.OpenCommandName,
                StringComparison.OrdinalIgnoreCase);

        private void SetStatus(string text) => _status.Text = text;

        private void LoadPolicy()
        {
            var policy = MercuryDockState.Policy;
            // 跨进程回写不得覆盖正在输入的框，否则用户打一半的字会被顶掉。
            if (!_minItems.IsKeyboardFocusWithin)
                _minItems.Text = policy.MinItems.ToString(CultureInfo.InvariantCulture);
            if (!_maxItems.IsKeyboardFocusWithin)
                _maxItems.Text = policy.MaxItems.ToString(CultureInfo.InvariantCulture);
            if (!_halfLife.IsKeyboardFocusWithin)
                _halfLife.Text = policy.HalfLifeDays.ToString("G", CultureInfo.InvariantCulture);
        }

        private void ApplyPolicy()
        {
            int? min = int.TryParse(_minItems.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var m) ? m : null;
            int? max = int.TryParse(_maxItems.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : null;
            double? half = double.TryParse(_halfLife.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : null;
            MercuryDockState.SetPolicy(min, max, half);
            LoadPolicy();
        }

        /// <summary>入坞列表行：项目行与指令行共用。Key 为选中恢复的标识。</summary>
        private sealed record DockRow(
            string Name,
            string Command,
            bool Pinned,
            bool IsCommand,
            double Weight,
            double Clicks,
            DateTimeOffset? LastOpened)
        {
            public string Key => IsCommand ? "cmd:" + Command : "proj:" + Name;
        }

    }

    private sealed class PinOpacityConverter : IValueConverter
    {
        public static readonly PinOpacityConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? 1.0 : 0.25;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class PinTipConverter : IValueConverter
    {
        public static readonly PinTipConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? "指令项常驻，右键可移除" : "固定/取消固定";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
