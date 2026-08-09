using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using HistoryVulcan.Core.Docking;

namespace Mercury;

/// <summary>
/// 扩展坞管理页面，默认注册为中央主文档区页签。
/// </summary>
/// <remarks>
/// 管理页与桌面坞都运行在桌面 Shell 进程；mercury 三段式指令注册在服务进程，
/// 经总线远程转发透明执行。所有状态写操作落到 state.json，由
/// <see cref="MercuryState.StartWatching"/> 的文件监视完成跨进程同步。
/// </remarks>
public static class MercuryManagerView
{
    public static ToolWindowDescriptor CreateDescriptor() => new()
    {
        Id = "dock.manager",
        Title = "扩展坞管理",
        // 中央主文档区页签；仍走 RegisterToolWindow，仅 DefaultSide 决定落位。
        DefaultSide = DockSide.Center,
        DefaultRatio = 1,
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
        private readonly SuggestBox _entryInput = new();
        private readonly TextBlock _status = new();
        private CommandOptionTree _commandTree = CommandOptionTree.Build(MercuryCommandCatalog.FallbackCommandCatalog());
        private bool _commandsLoaded;

        public ManagerPage()
        {
            // 页面只有深色模式：整页铺深色底，避免透出宿主浅色停靠框背景。
            Background = DockTheme.PanelBackground;
            // 收紧页边；顶栏只留工具条，状态挪到底部，避免头与表格之间被状态行撑开。
            var root = new DockPanel
            {
                Margin = new Thickness(6),
                Background = DockTheme.PanelBackground,
            };
            root.SetValue(TextElement.FontFamilyProperty, DockTheme.FontFamily);
            root.SetValue(TextElement.FontSizeProperty, DockTheme.BodyFontSize);
            ConfigureInput(_minItems);
            ConfigureInput(_maxItems);
            ConfigureInput(_halfLife);

            // 单行入口：指令与参数同一框。空格前按域→类→方法级联，空格后切参数候选；
            // Shift+W/S 移动、Tab 确认（叶子带参即加入）、Esc 关闭。
            _entryInput.Hint = "指令+参数同一框：输入即弹候选，Shift+W/S 移动，Tab 逐级确认（域→类→方法→空格→参数）；如 mercury.proj.open 2026-024-HistoryVulcan";
            _entryInput.Source = EntryOptions;
            _entryInput.Committed += text =>
            {
                // 级联选到参数（含空格）即视为完整指令行，直接加入；仅选到指令则继续等参数。
                if (text.Contains(' '))
                    AddEntry();
            };
            // 常驻默认指令：可删除改输其他指令，候选来自服务侧全量清单。
            _entryInput.Text = MercuryCommandCatalog.ProjectOpenCommandName;
            _entryInput.MinWidth = 220;
            _entryInput.VerticalAlignment = VerticalAlignment.Center;
            _entryInput.Margin = new Thickness(12, 0, 0, 0);

            var add = new Button
            {
                Content = "加入扩展坞",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockTheme.StyleButton(add, accent: true);
            add.Click += (_, _) => AddEntry();

            // 顶栏单行：策略三组靠左，指令入口占满中间，加入按钮钉右侧；不再套圆角框。
            var policyGroup = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            policyGroup.Children.Add(new TextBlock
            {
                Text = "最少显示",
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.BodyFontSize,
                Foreground = DockTheme.Label,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            });
            policyGroup.Children.Add(_minItems);
            policyGroup.Children.Add(Gap("最多显示"));
            policyGroup.Children.Add(_maxItems);
            policyGroup.Children.Add(Gap("半衰期(天)"));
            policyGroup.Children.Add(_halfLife);

            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(add, Dock.Right);
            DockPanel.SetDock(policyGroup, Dock.Left);
            toolbar.Children.Add(add);
            toolbar.Children.Add(policyGroup);
            toolbar.Children.Add(_entryInput);
            DockPanel.SetDock(toolbar, Dock.Top);
            root.Children.Add(toolbar);

            _status.TextWrapping = TextWrapping.Wrap;
            _status.FontFamily = DockTheme.FontFamily;
            _status.FontSize = DockTheme.SmallFontSize;
            _status.Foreground = DockTheme.Muted;

            var footer = new StackPanel { Margin = new Thickness(2, 4, 0, 0) };
            footer.Children.Add(_status);
            footer.Children.Add(new TextBlock
            {
                Text = "列表只显示已入坞的项目与指令；点首列图钉固定/取消固定项目；右键可刷新、排除项目或移除指令；策略改完自动保存。"
                    + "顶栏单行：指令+参数同一框，输入即弹候选，Shift+W/S 移动，Tab 逐级确认（域→类→方法→空格→参数），选到参数即加入；默认 mercury.proj.open 打开项目目录，可改输任意总线指令常驻桌面坞。",
                TextWrapping = TextWrapping.Wrap,
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Muted,
                Margin = new Thickness(0, 2, 0, 0),
            });
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            _list.View = BuildColumns();
            // 头与表间距压到 2px；正文字号 Small，行距 Padding(4,1)，比 Body/(8,4) 明显更紧。
            _list.Margin = new Thickness(0, 2, 0, 0);
            _list.Background = DockTheme.PanelBackground;
            _list.BorderBrush = DockTheme.PanelBorder;
            _list.BorderThickness = new Thickness(1);
            _list.Foreground = DockTheme.Label;
            _list.FontFamily = DockTheme.FontFamily;
            _list.FontSize = DockTheme.SmallFontSize;
            // 行距略收紧，并自带选中模板：淡黄底深字，覆盖宿主/系统白底选中。
            _list.ItemContainerStyle = BuildRowStyle();
            // 兜底：未走自定义模板时的系统选中色（含失焦白底）。
            _list.Resources[SystemColors.HighlightBrushKey] = DockTheme.Selection;
            _list.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.SelectionText;
            _list.Resources[SystemColors.InactiveSelectionHighlightBrushKey] = DockTheme.Selection;
            _list.Resources[SystemColors.InactiveSelectionHighlightTextBrushKey] = DockTheme.SelectionText;
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

            MercuryState.StartWatching();
            MercuryState.Changed += OnChanged;
            Unloaded += (_, _) => MercuryState.Changed -= OnChanged;
            Loaded += (_, _) =>
            {
                LoadPolicy();
                Reload();
                _ = LoadCommandCatalogAsync();
                _ = MercuryState.RefreshAsync();
            };
        }

        private static GridView BuildColumns()
        {
            var view = new GridView
            {
                // 对齐命令集页紧凑表头观感，但高度压到 20。
                ColumnHeaderContainerStyle = BuildHeaderStyle(),
            };
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

        /// <summary>
        /// 表格正文字号 Small、行距 Padding(4,1)，比命令集默认 Body/(8,4) 更紧；
        /// 自绘选中模板保留淡黄底深字。
        /// </summary>
        private static Style BuildRowStyle()
        {
            var style = new Style(typeof(ListViewItem));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 1, 4, 1)));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, DockTheme.FontFamily));
            style.Setters.Add(new Setter(Control.FontSizeProperty, DockTheme.SmallFontSize));
            style.Setters.Add(new Setter(TextElement.FontSizeProperty, DockTheme.SmallFontSize));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
            style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, DockTheme.Label));
            style.Setters.Add(new Setter(Control.TemplateProperty, BuildRowTemplate()));
            return style;
        }

        /// <summary>紧凑表头：Small 字号、高 20。</summary>
        private static Style BuildHeaderStyle()
        {
            var style = new Style(typeof(GridViewColumnHeader));
            style.Setters.Add(new Setter(Control.FontFamilyProperty, DockTheme.FontFamily));
            style.Setters.Add(new Setter(Control.FontSizeProperty, DockTheme.SmallFontSize));
            style.Setters.Add(new Setter(Control.ForegroundProperty, DockTheme.Muted));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4, 0, 4, 0)));
            style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 20.0));
            style.Setters.Add(new Setter(Control.TemplateProperty, BuildHeaderTemplate()));
            return style;
        }

        private static ControlTemplate BuildHeaderTemplate()
        {
            var template = new ControlTemplate(typeof(GridViewColumnHeader));
            var grid = new FrameworkElementFactory(typeof(Grid));
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.BackgroundProperty, DockTheme.SurfaceAlt);
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetBinding(
                FrameworkElement.MarginProperty,
                new Binding(nameof(Control.Padding))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                });
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);
            grid.AppendChild(border);

            var thumb = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.Thumb), "PART_HeaderGripper");
            thumb.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right);
            thumb.SetValue(FrameworkElement.WidthProperty, 8.0);
            thumb.SetValue(FrameworkElement.CursorProperty, Cursors.SizeWE);
            thumb.SetValue(Control.TemplateProperty, BuildHeaderGripperTemplate());
            grid.AppendChild(thumb);
            template.VisualTree = grid;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, DockTheme.Hover, "Bd"));
            template.Triggers.Add(hover);
            return template;
        }

        private static ControlTemplate BuildHeaderGripperTemplate()
        {
            var template = new ControlTemplate(typeof(System.Windows.Controls.Primitives.Thumb));
            var hit = new FrameworkElementFactory(typeof(Border));
            hit.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            var line = new FrameworkElementFactory(typeof(Border));
            line.SetValue(FrameworkElement.WidthProperty, 1.0);
            line.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            line.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 6));
            line.SetValue(Border.BackgroundProperty, DockTheme.PanelBorder);
            hit.AppendChild(line);
            template.VisualTree = hit;
            return template;
        }

        private static ControlTemplate BuildRowTemplate()
        {
            var template = new ControlTemplate(typeof(ListViewItem));
            var border = new FrameworkElementFactory(typeof(Border), "Bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetBinding(
                Border.PaddingProperty,
                new Binding(nameof(Control.Padding))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                });
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var row = new FrameworkElementFactory(typeof(GridViewRowPresenter), "Row");
            row.SetBinding(
                GridViewRowPresenter.ContentProperty,
                new Binding(nameof(ContentControl.Content))
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent),
                });
            row.SetBinding(
                GridViewRowPresenter.ColumnsProperty,
                new Binding("View.Columns")
                {
                    RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(ListView), 1),
                });
            border.AppendChild(row);
            template.VisualTree = border;

            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, DockTheme.Hover, "Bd"));
            template.Triggers.Add(hover);

            // 选中触发器放在悬停之后，保证选中色优先于悬停色。
            var selected = new Trigger { Property = ListViewItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Border.BackgroundProperty, DockTheme.Selection, "Bd"));
            selected.Setters.Add(new Setter(Control.ForegroundProperty, DockTheme.SelectionText));
            template.Triggers.Add(selected);
            return template;
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
            button.SetValue(Button.PaddingProperty, new Thickness(0));
            button.SetValue(Control.FontSizeProperty, DockTheme.SmallFontSize);
            button.SetValue(FrameworkElement.HeightProperty, 16.0);
            button.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
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
                    MercuryState.Pin(row.Name, !row.Pinned);
            }));
            template.VisualTree = button;
            return template;
        }

        private void RebuildContextMenu()
        {
            _listMenu.Items.Clear();

            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await MercuryState.RefreshAsync();
            _listMenu.Items.Add(refresh);

            if (_list.SelectedItem is not DockRow row)
                return;
            _listMenu.Items.Add(new Separator());
            if (row.IsCommand)
            {
                var remove = new MenuItem { Header = "移除该指令" };
                remove.Click += (_, _) =>
                {
                    if (MercuryState.RemoveCommand(row.Command))
                        SetStatus($"已移除指令 {row.Command}");
                };
                _listMenu.Items.Add(remove);
            }
            else
            {
                var exclude = new MenuItem { Header = "排除" };
                exclude.Click += (_, _) =>
                {
                    MercuryState.Exclude(row.Name, excluded: true);
                    SetStatus($"已排除 {row.Name}；在加入框选 mercury.proj.open 可找回");
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
        {
            // 显式 CellTemplate 固定 Small 字号；Foreground 不写死，选中时继承行上的深色字。
            var template = new DataTemplate();
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(path)
            {
                StringFormat = format,
                ConverterCulture = CultureInfo.CurrentCulture,
            });
            text.SetValue(TextBlock.FontFamilyProperty, DockTheme.FontFamily);
            text.SetValue(TextBlock.FontSizeProperty, DockTheme.SmallFontSize);
            text.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            template.VisualTree = text;
            return new GridViewColumn
            {
                Header = header,
                Width = width,
                CellTemplate = template,
            };
        }

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
            input.Padding = DockTheme.InputPadding;
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
            var rows = MercuryState.Projects
                .Select(project => new DockRow(
                    project.Name,
                    MercuryCommandCatalog.BuildOpenProjectCommand(project.Name),
                    project.Pinned,
                    false,
                    project.Weight,
                    project.Clicks,
                    project.LastOpened))
                .Concat(MercuryState.CommandEntries.Select(entry => new DockRow(
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
            var bus = MercuryUiModule.Bus;
            if (bus != null)
            {
                try
                {
                    var result = await bus.ExecuteAsync("command.list", "UI").ConfigureAwait(false);
                    if (result.Success)
                        items = MercuryCommandCatalog.ParseCommandCatalog(result.Data);
                }
                catch (Exception)
                {
                    // 服务不可达时回退静态清单，不打扰页面。
                }
            }

            await Dispatcher.BeginInvoke(() =>
            {
                if (items.Count == 0)
                    items = MercuryCommandCatalog.FallbackCommandCatalog();
                // 服务侧清单可能来自旧版模块：保证常驻默认指令永远在候选树里。
                if (items.All(item => !item.Name.Equals(
                        MercuryCommandCatalog.ProjectOpenCommandName, StringComparison.OrdinalIgnoreCase)))
                {
                    items = items.Concat(MercuryCommandCatalog.FallbackCommandCatalog()
                        .Where(item => item.Name.Equals(
                            MercuryCommandCatalog.ProjectOpenCommandName, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }
                _commandTree = CommandOptionTree.Build(items);
            });
        }

        /// <summary>
        /// 单行候选：首个空格之前是指令（域→类→方法级联），空格之后是参数。
        /// 打开项目指令的参数候选为工作树未入坞项目（排除项标注排前），其他指令参数自由输入。
        /// </summary>
        private IReadOnlyList<SuggestOption> EntryOptions(string text)
        {
            var spaceIndex = text.IndexOf(' ');
            if (spaceIndex < 0)
                return _commandTree.ChildrenOf(text);

            var command = text[..spaceIndex].Trim();
            var fragment = text[(spaceIndex + 1)..].Trim();
            if (command.Length == 0 || !IsOpenProjectCommand(command))
                return [];

            var docked = MercuryState.Projects
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = MercuryState.AllProjects
                .Where(item => item.Excluded)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return MercuryState.ListWorktreeProjects()
                .Where(name => !docked.Contains(name))
                .Where(name => fragment.Length == 0
                    || name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                .Select(name => (Name: name, Excluded: excluded.Contains(name)))
                .OrderByDescending(candidate => candidate.Excluded)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => new SuggestOption(
                    command + " " + HistoryVulcan.Core.Commands.CommandParser.QuoteArg(candidate.Name),
                    candidate.Name,
                    candidate.Excluded ? "已排除，加入即找回" : null,
                    false))
                .ToList();
        }

        /// <summary>加入：指令行拆为 指令+参数；打开项目且命中工作树走 AddToDock，其余作为自定义指令常驻入坞。</summary>
        private void AddEntry()
        {
            var line = _entryInput.Text.Trim();
            if (line.Length == 0)
            {
                SetStatus("请输入要加入的指令");
                return;
            }

            var spaceIndex = line.IndexOf(' ');
            var command = spaceIndex < 0 ? line : line[..spaceIndex];
            var argument = spaceIndex < 0 ? string.Empty : line[(spaceIndex + 1)..].Trim();
            // 剥掉候选或用户手工加的外层引号再匹配项目；重新组合时只需给裸参数补引号。
            var bareArgument = argument.Length > 1 && argument.StartsWith('"') && argument.EndsWith('"')
                ? argument[1..^1]
                : argument;
            if (IsOpenProjectCommand(command)
                && bareArgument.Length > 0
                && MercuryState.AddToDock(bareArgument))
            {
                SetStatus($"已加入扩展坞并固定 {bareArgument}");
                _entryInput.Text = MercuryCommandCatalog.ProjectOpenCommandName;
                return;
            }

            var text = argument.Length == 0
                ? command
                : command + " " + HistoryVulcan.Core.Commands.CommandParser.QuoteArg(bareArgument);
            if (MercuryState.AddCommand(text))
            {
                SetStatus($"已加入指令 {text}");
                _entryInput.Text = MercuryCommandCatalog.ProjectOpenCommandName;
            }
            else
            {
                SetStatus("指令为空，未加入");
            }
        }

        private static bool IsOpenProjectCommand(string? command)
            => string.Equals(
                command?.Trim(),
                MercuryCommandCatalog.ProjectOpenCommandName,
                StringComparison.OrdinalIgnoreCase);

        private void SetStatus(string text) => _status.Text = text;

        private void LoadPolicy()
        {
            var policy = MercuryState.Policy;
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
            MercuryState.SetPolicy(min, max, half);
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
