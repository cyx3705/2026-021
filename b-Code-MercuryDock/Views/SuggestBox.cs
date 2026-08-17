using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Mercury;

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
            Padding = new Thickness(10, 4, 10, 4),
            BorderThickness = new Thickness(1),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _input.SetResourceReference(Control.FontFamilyProperty, "Shell.Font.Family");
        _input.SetResourceReference(Control.FontSizeProperty, "Shell.Font.Body");
        _input.SetResourceReference(Control.ForegroundProperty, "Shell.Brush.TextPrimary");
        _input.SetResourceReference(Control.BackgroundProperty, "Shell.Brush.Surface");
        _input.SetResourceReference(Control.BorderBrushProperty, "Shell.Brush.ControlBorder");
        Content = _input;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            MaxHeight = 264,
            ItemTemplate = BuildOptionTemplate(),
        };
        _list.SetResourceReference(Control.ForegroundProperty, "Shell.Brush.TextPrimary");
        _list.SetResourceReference(Control.FontFamilyProperty, "Shell.Font.Family");
        _list.SetResourceReference(Control.FontSizeProperty, "Shell.Font.Body");
        _list.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
        _popup = new Popup
        {
            PlacementTarget = _input,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = CreatePopupBorder(),
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

    private Border CreatePopupBorder()
    {
        var border = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(2),
            Child = _list,
        };
        border.SetResourceReference(Border.BackgroundProperty, "Shell.Brush.Surface");
        border.SetResourceReference(Border.BorderBrushProperty, "Shell.Brush.Hairline");
        border.SetResourceReference(Border.EffectProperty, "Shell.Shadow.Flyout");
        return border;
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
        detail.SetResourceReference(TextBlock.FontSizeProperty, "Shell.Font.Small");
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
