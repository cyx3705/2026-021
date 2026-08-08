using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace MercuryDock;

/// <summary>建议项：Text 为确认后落入输入框的全文，Display 为弹窗段落显示，IsBranch 表示还有下一级可下钻。</summary>
public sealed record SuggestOption(string Text, string Display, string? Detail, bool IsBranch);

/// <summary>
/// 无下拉箭头的建议输入框：输入即过滤并向下自动弹出候选，
/// Shift+W/S 或方向键上下移动，Tab/Enter 确认（分支项确认后继续下一级），Esc 关闭。
/// </summary>
internal sealed class SuggestBox : UserControl
{
    private readonly TextBox _input;
    private readonly Popup _popup;
    private readonly ListBox _list;
    private bool _committing;

    public SuggestBox()
    {
        _input = new TextBox
        {
            Height = 28,
            // 垂直内边距过大会把文字裁掉一半，输入框专用 InputPadding（垂直 0）。
            Padding = DockTheme.InputPadding,
            FontFamily = DockTheme.FontFamily,
            FontSize = DockTheme.BodyFontSize,
            Foreground = DockTheme.Label,
            Background = DockTheme.PanelBackground,
            BorderBrush = DockTheme.PanelBorder,
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Content = _input;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            Foreground = DockTheme.Label,
            BorderThickness = new Thickness(0),
            MaxHeight = 264,
            FontFamily = DockTheme.FontFamily,
            FontSize = DockTheme.BodyFontSize,
            ItemTemplate = BuildOptionTemplate(),
        };
        _list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _list.Resources[SystemColors.HighlightBrushKey] = DockTheme.Selection;
        _list.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.SelectionText;
        _list.Resources[SystemColors.ControlBrushKey] = DockTheme.Selection;
        _list.Resources[SystemColors.ControlTextBrushKey] = DockTheme.SelectionText;
        var itemStyle = new Style(typeof(ListBoxItem));
        itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(8, 4, 8, 4)));
        _list.ItemContainerStyle = itemStyle;

        _popup = new Popup
        {
            PlacementTarget = _input,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = new Border
            {
                Background = DockTheme.PanelBackground,
                BorderBrush = DockTheme.PanelBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(2),
                Child = _list,
            },
        };

        _input.TextChanged += (_, _) =>
        {
            if (_committing)
                return;
            Edited?.Invoke(_input.Text);
            RefreshOptions();
        };
        _input.GotKeyboardFocus += (_, _) => RefreshOptions();
        _input.PreviewKeyDown += OnKeyDown;
        _list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            if (FindAncestor<ListBoxItem>(args.OriginalSource as DependencyObject) is { DataContext: SuggestOption option })
                CommitOption(option);
        };
        // 弹窗宽度跟随输入框，长文本也不截断候选列表本身。
        _input.SizeChanged += (_, _) => _list.MinWidth = _input.ActualWidth;
    }

    /// <summary>按当前输入给出候选；为 null 时永不弹出。</summary>
    public Func<string, IReadOnlyList<SuggestOption>>? Source { get; set; }

    /// <summary>确认叶子项时触发；分支项确认不触发（继续下钻）。</summary>
    public event Action<string>? Committed;

    /// <summary>输入框文本发生任何变化（含确认回填）时触发。</summary>
    public event Action<string>? Edited;

    public string Text
    {
        get => _input.Text;
        set
        {
            _committing = true;
            _input.Text = value;
            _committing = false;
            _popup.IsOpen = false;
        }
    }

    public string Hint
    {
        set => _input.ToolTip = value;
    }

    public new void Focus() => _input.Focus();

    private void OnKeyDown(object? sender, KeyEventArgs args)
    {
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        switch (args.Key)
        {
            // 约定的导航键：Shift+W 上、Shift+S 下；方向键同样可用。
            case Key.W when shift:
                MoveSelection(-1);
                args.Handled = true;
                break;
            case Key.S when shift:
                MoveSelection(1);
                args.Handled = true;
                break;
            case Key.Up when !shift:
                MoveSelection(-1);
                args.Handled = true;
                break;
            case Key.Down when !shift:
                MoveSelection(1);
                args.Handled = true;
                break;
            case Key.Tab when !shift:
            case Key.Enter:
                if (_popup.IsOpen && _list.SelectedItem is SuggestOption option)
                {
                    CommitOption(option);
                    args.Handled = true;
                }
                break;
            case Key.Escape:
                _popup.IsOpen = false;
                args.Handled = true;
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (!_popup.IsOpen || _list.Items.Count == 0)
            return;
        var index = _list.SelectedIndex;
        index = index < 0
            ? (delta > 0 ? 0 : _list.Items.Count - 1)
            : (index + delta + _list.Items.Count) % _list.Items.Count;
        _list.SelectedIndex = index;
        _list.ScrollIntoView(_list.SelectedItem);
    }

    private void RefreshOptions()
    {
        if (_committing)
            return;
        var options = Source?.Invoke(_input.Text) ?? [];
        _list.ItemsSource = options;
        var open = _input.IsKeyboardFocused && options.Count > 0;
        _popup.IsOpen = open;
        if (open)
            _list.SelectedIndex = 0;
    }

    private void CommitOption(SuggestOption option)
    {
        _committing = true;
        _input.Text = option.Text;
        _input.CaretIndex = _input.Text.Length;
        _committing = false;
        Edited?.Invoke(_input.Text);
        if (option.IsBranch)
        {
            // 分支确认后留在输入框并立即展开下一级候选。
            RefreshOptions();
        }
        else
        {
            _popup.IsOpen = false;
            Committed?.Invoke(option.Text);
        }
    }

    private static DataTemplate BuildOptionTemplate()
    {
        var template = new DataTemplate();
        var stack = new FrameworkElementFactory(typeof(StackPanel));
        stack.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        // 不写死前景色：继承 ListBoxItem 的前景色，选中淡黄底时自动反为深色。
        var display = new FrameworkElementFactory(typeof(TextBlock));
        display.SetBinding(TextBlock.TextProperty, new Binding(nameof(SuggestOption.Display)));
        stack.AppendChild(display);
        var detail = new FrameworkElementFactory(typeof(TextBlock));
        detail.SetBinding(TextBlock.TextProperty, new Binding(nameof(SuggestOption.Detail)));
        detail.SetValue(TextBlock.FontSizeProperty, DockTheme.SmallFontSize);
        detail.SetValue(UIElement.OpacityProperty, 0.7);
        detail.SetValue(FrameworkElement.MarginProperty, new Thickness(8, 0, 0, 0));
        detail.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        stack.AppendChild(detail);
        template.VisualTree = stack;
        return template;
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
