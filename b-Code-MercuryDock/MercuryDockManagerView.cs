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
/// 本页面运行在桌面 Shell 进程，活动坞本体运行在服务宿主进程，两者不共享内存。
/// 所有写操作落到 state.json，由 <see cref="MercuryDockState.StartWatching"/> 的文件监视完成跨进程同步；
/// 不得假设改完内存对面就能看到。
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
        private readonly ComboBox _candidates = new() { Width = 220 };

        public ManagerPage()
        {
            var root = new DockPanel
            {
                Margin = new Thickness(12),
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

            ConfigureCandidates(_candidates);
            var add = new Button
            {
                Content = "加入扩展坞",
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockTheme.StyleButton(add, accent: true);
            add.Click += (_, _) =>
            {
                if (_candidates.SelectedItem is AddCandidate candidate
                    && MercuryDockState.AddToDock(candidate.Name))
                {
                    ReloadCandidates();
                }
            };

            var addBar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            addBar.Children.Add(_candidates);
            addBar.Children.Add(add);

            var optionsStack = new StackPanel();
            optionsStack.Children.Add(policyBar);
            optionsStack.Children.Add(addBar);

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
                Text = "点列表首列图钉固定/取消固定；右键可刷新、排除或恢复收录；策略改完自动保存。固定项无论打开次数如何都会显示，光圈亮度随权重变化。",
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
            _list.Resources[SystemColors.HighlightBrushKey] = DockTheme.AccentSoft;
            _list.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.Label;
            _list.ItemContainerStyle = BuildRowStyle();
            // 右键按下先选中行，菜单里的排除/恢复收录才有确定的作用对象。
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
                ReloadCandidates();
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
            view.Columns.Add(Column("项目", nameof(DockProject.Name), 150));
            view.Columns.Add(Column("权重", nameof(DockProject.Weight), 60, "F2"));
            view.Columns.Add(Column("点击", nameof(DockProject.Clicks), 50, "F1"));
            view.Columns.Add(Column("最近打开", nameof(DockProject.LastOpened), 120, "yyyy-MM-dd HH:mm"));
            view.Columns.Add(new GridViewColumn
            {
                Header = "已排除",
                Width = 56,
                DisplayMemberBinding = new Binding(nameof(DockProject.Excluded))
                {
                    Converter = ExcludedTextConverter.Instance,
                    ConverterCulture = CultureInfo.CurrentCulture,
                },
            });
            return view;
        }

        /// <summary>图钉开关：已固定全亮、未固定半透明，点击直接切换，不再占用按钮条。</summary>
        private static DataTemplate BuildPinTemplate()
        {
            var template = new DataTemplate();
            var button = new FrameworkElementFactory(typeof(Button));
            button.SetValue(Button.ContentProperty, "📌");
            button.SetValue(Button.CursorProperty, Cursors.Hand);
            button.SetValue(Button.BackgroundProperty, Brushes.Transparent);
            button.SetValue(Button.BorderThicknessProperty, new Thickness(0));
            button.SetValue(Button.PaddingProperty, new Thickness(2, 0, 2, 0));
            button.SetBinding(UIElement.OpacityProperty, new Binding(nameof(DockProject.Pinned))
            {
                Converter = PinOpacityConverter.Instance,
            });
            button.SetBinding(Button.ToolTipProperty, new Binding(nameof(DockProject.Pinned))
            {
                Converter = PinTipConverter.Instance,
            });
            button.AddHandler(Button.ClickEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Button { DataContext: DockProject row })
                    MercuryDockState.Pin(row.Name, !row.Pinned);
            }));
            template.VisualTree = button;
            return template;
        }

        /// <summary>被排除的项目整行调暗，与正常收录行一眼区分。</summary>
        private static Style BuildRowStyle()
        {
            var style = new Style(typeof(ListViewItem));
            var dimmed = new DataTrigger
            {
                Binding = new Binding(nameof(DockProject.Excluded)),
                Value = true,
            };
            dimmed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45));
            style.Triggers.Add(dimmed);
            return style;
        }

        private void RebuildContextMenu()
        {
            _listMenu.Items.Clear();

            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await MercuryDockState.RefreshAsync();
            _listMenu.Items.Add(refresh);

            if (_list.SelectedItem is DockProject row)
            {
                var exclude = new MenuItem { Header = row.Excluded ? "恢复收录" : "排除" };
                exclude.Click += (_, _) => MercuryDockState.Exclude(row.Name, !row.Excluded);
                _listMenu.Items.Add(new Separator());
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

        private void ConfigureCandidates(ComboBox input)
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
            input.ToolTip = "工作树中尚未入坞的项目；加入即固定";
        }

        private void OnChanged() => Dispatcher.BeginInvoke(() =>
        {
            LoadPolicy();
            Reload();
            ReloadCandidates();
        });

        private void Reload()
        {
            var selected = (_list.SelectedItem as DockProject)?.Name;
            _list.ItemsSource = MercuryDockState.AllProjects;
            if (selected == null)
                return;
            foreach (var item in _list.Items)
            {
                if (item is DockProject row && row.Name == selected)
                {
                    _list.SelectedItem = item;
                    break;
                }
            }
        }

        /// <summary>候选 = 工作树内存在但当前不在坞里的项目；被排除的排前并标注，加入即找回。</summary>
        private void ReloadCandidates()
        {
            var selected = (_candidates.SelectedItem as AddCandidate)?.Name;
            var docked = MercuryDockState.Projects
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var excluded = MercuryDockState.AllProjects
                .Where(item => item.Excluded)
                .Select(item => item.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            _candidates.ItemsSource = MercuryDockState.ListWorktreeProjects()
                .Where(name => !docked.Contains(name))
                .Select(name => new AddCandidate(name, excluded.Contains(name)))
                .OrderByDescending(candidate => candidate.Excluded)
                .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (selected == null)
                return;
            foreach (var item in _candidates.Items)
            {
                if (item is AddCandidate candidate && candidate.Name == selected)
                {
                    _candidates.SelectedItem = item;
                    break;
                }
            }
        }

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

        /// <summary>"加入扩展坞"下拉项；Excluded 仅用于标注与排序。</summary>
        private sealed record AddCandidate(string Name, bool Excluded)
        {
            public override string ToString() => Excluded ? $"{Name}（已排除）" : Name;
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
            => value is true ? "取消固定" : "固定";

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class ExcludedTextConverter : IValueConverter
    {
        public static readonly ExcludedTextConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is true ? "已排除" : string.Empty;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
