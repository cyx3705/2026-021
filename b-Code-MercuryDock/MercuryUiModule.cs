using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using Mercury.CommandSurface;

namespace Mercury;

/// <summary>
/// 活动坞界面生命周期。窗口自持、不依赖宿主窗口，因此只在有 WPF Application 的宿主中建窗。
/// </summary>
/// <remarks>
/// 当前 HistoryVulcan 组合下只有桌面 Shell 进程会实例化 UI 模块（服务进程 EnableUiModules=false），
/// 因此管理页与桌面坞都运行在 Shell 进程；模块指令由 mercury 三段式目录注册在服务进程，
/// Shell 侧经 <see cref="CommandBus"/> 的远程执行器透明转发，模块代码无需关心进程边界。
/// </remarks>
public sealed class MercuryUiModule : IUiModule, IShellUiAware, IModuleContextAware, IShellCommandWorkbenchAware
{
    private static DockWindow? _window;
    private IShellUiRegistrar? _shellUi;
    private IShellCommandWorkbenchHost? _commandWorkbench;
    private IDisposable? _managerWindow;
    private CommandSurfaceFeature? _commandSurface;

    /// <summary>宿主注入的指令总线；Shell 进程中自带远程转发。宿主不注入时（旧宿主/烟测）为 null。</summary>
    internal static CommandBus? Bus { get; private set; }

    IShellUiRegistrar IShellUiAware.ShellUi
    {
        set => _shellUi = value;
    }

    IShellCommandWorkbenchHost? IShellCommandWorkbenchAware.CommandWorkbench
    {
        set => _commandWorkbench = value;
    }

    public void Attach(IModuleContext context)
    {
        Bus = context.Bus;
        context.RegisterCommands(MercuryCommandCatalog.Register);
    }

    public void CreateUi()
    {
        MercuryState.StartWatching();
        if (!DockShortcutFolder.IsExplorerRegistrationDisabled)
        {
            DockShortcutFolder.StartWatching(MercuryState.ApplyShortcutFolderChanges);
            _ = ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path);
        }

        _ = MercuryState.RefreshAsync();

        if (_shellUi != null)
            _managerWindow ??= _shellUi.RegisterToolWindow(MercuryManagerView.CreateDescriptor(), "HistoryMercury");

        _commandSurface ??= CommandSurfaceFeature.TryAttach(_commandWorkbench, _shellUi);

        if (_window != null)
            return;
        // Shell hosts need both the manager page and the desktop dock. A
        // headless Smoke caller can still verify registrar behavior without
        // creating a WPF window on an MTA thread.
        if (Application.Current == null)
            return;
        _window = new DockWindow();
        _window.Show();
        if (MercuryState.Hidden)
            _window.Hide();
    }

    public void DestroyUi()
    {
        _commandSurface?.Dispose();
        _commandSurface = null;
        _managerWindow?.Dispose();
        _managerWindow = null;
        _window?.Close();
        _window = null;
    }

    private sealed class DockWindow : Window
    {
        private readonly WrapPanel _items = new() { Orientation = Orientation.Horizontal };
        private HwndSource? _source;
        private bool _resizing;
        private bool _anchorPending;

        public DockWindow()
        {
            var (width, height) = MercuryState.Size;
            Width = width;
            Height = height;
            MinWidth = DockLayout.MinWidth;
            MaxWidth = DockLayout.MaxWidth;
            MinHeight = DockLayout.MinHeight;
            MaxHeight = DockLayout.MaxHeight;
            SizeToContent = SizeToContent.Manual;
            WindowStyle = WindowStyle.None;
            // 无边框分层窗口没有原生非客户区，调整边框由 WM_NCHITTEST 自行给出。
            ResizeMode = ResizeMode.CanResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = DockTheme.FontFamily;
            FontSize = DockTheme.BodyFontSize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = false;

            var border = new Border
            {
                Background = DockTheme.PanelBackground,
                BorderBrush = DockTheme.PanelBorder,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12),
                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _items,
                },
            };
            Content = border;

            SourceInitialized += OnSourceInitialized;
            Loaded += (_, _) => ApplyAnchor();
            SizeChanged += OnSizeChanged;
            MercuryState.Changed += OnChanged;
            Closed += (_, _) => MercuryState.Changed -= OnChanged;
            RefreshItems();
        }

        private void OnSourceInitialized(object? sender, EventArgs args)
        {
            _source = (HwndSource)PresentationSource.FromVisual(this)!;
            _source.AddHook(WindowProc);
            DesktopLayer.Prepare(_source.Handle);
            ApplyAnchor();
        }

        private void OnSizeChanged(object? sender, SizeChangedEventArgs args)
        {
            // 交互调整期间由系统的尺寸循环负责几何，结束时统一保存并贴合。
            if (_resizing)
                return;
            MercuryState.SaveSize(ActualWidth, ActualHeight);
            // 右下角是锚点：尺寸变了要按新尺寸重新贴合，左上角随之移动。
            ApplyAnchor();
        }

        private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmNcHitTest = 0x0084;
            const int WmDisplayChange = 0x007E;
            const int WmSettingChange = 0x001A;
            const int WmActivateApp = 0x001C;
            const int WmShowWindow = 0x0018;
            const int WmDpiChanged = 0x02E0;
            const int WmWindowPosChanging = 0x0046;
            const int WmEnterSizeMove = 0x0231;
            const int WmExitSizeMove = 0x0232;

            switch (message)
            {
                case WmNcHitTest:
                    {
                        // 重定父之后 WPF 的 Left/Top 不再是屏幕坐标，PointFromScreen 会算错，
                        // 因此一律以窗口真实屏幕矩形为基准，全程用物理像素。
                        if (!DesktopLayer.TryGetWindowRect(hwnd, out var left, out var top, out var width, out var height))
                            return IntPtr.Zero;
                        var screenX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                        var screenY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                        var scale = _source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
                        var code = DockLayout.HitTest(
                            screenX - left,
                            screenY - top,
                            width,
                            height,
                            DockLayout.BorderWidth * scale);
                        if (code == DockLayout.HitNone)
                            return IntPtr.Zero;
                        handled = true;
                        return new IntPtr(code);
                    }

                case WmEnterSizeMove:
                    // 调整期间交给系统的尺寸循环，自己不要再 SetWindowPos，否则会和拖拽打架。
                    _resizing = true;
                    return IntPtr.Zero;

                case WmExitSizeMove:
                    _resizing = false;
                    MercuryState.SaveSize(ActualWidth, ActualHeight);
                    ApplyAnchor();
                    return IntPtr.Zero;

                case WmDisplayChange:
                case WmSettingChange:
                case WmActivateApp:
                case WmShowWindow:
                case WmDpiChanged:
                    QueueAnchor();
                    return IntPtr.Zero;

                case WmWindowPosChanging:
                    // 任何 z-order 变动都改写为插到最底，窗口因此永不遮挡其他页面，
                    // 同时仍是普通顶层窗口，鼠标输入完整。
                    DesktopLayer.PinToBottom(lParam);
                    return IntPtr.Zero;

                default:
                    return IntPtr.Zero;
            }
        }

        /// <summary>把窗口吸附到主显示器工作区右下角。</summary>
        private void ApplyAnchor()
        {
            if (_source != null && DesktopLayer.AnchorToPrimaryBottomRight(_source.Handle, (int)DockLayout.Margin))
                return;

            // 极少数 Win32 查询失败时仍保留 WPF 的旧降级路径，避免窗口失去可见位置。
            var (left, top) = DockLayout.Anchor(SystemParameters.WorkArea, ActualWidth, ActualHeight);
            Left = left;
            Top = top;
        }

        private void QueueAnchor()
        {
            if (_resizing || _anchorPending)
                return;

            _anchorPending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _anchorPending = false;
                if (!_resizing)
                    ApplyAnchor();
            });
        }

        private void OnChanged()
            => Dispatcher.BeginInvoke(() =>
            {
                if (MercuryState.Hidden)
                    Hide();
                else
                {
                    Show();
                    QueueAnchor();
                }
                RefreshItems();
            });

        private void RefreshItems()
        {
            _items.Children.Clear();
            _items.Children.Add(HistoryVulcanButton());

            var projects = MercuryState.Projects;
            // 光圈亮度按当前列表最大权重归一化，保证任何时候都有对比度。
            var maximum = projects.Count == 0 ? 0 : projects.Max(item => item.Weight);
            foreach (var project in projects)
                _items.Children.Add(ProjectButton(project, maximum));

            // 手动加入的指令项常驻项目之后：不参与权重与策略排序，点击即经总线执行。
            foreach (var entry in MercuryState.CommandEntries)
                _items.Children.Add(CommandButton(entry));

            if (projects.Count == 0 && MercuryState.CommandEntries.Count == 0)
            {
                _items.Children.Add(new TextBlock
                {
                    Text = "暂无活动项目",
                    Foreground = DockTheme.Muted,
                    FontFamily = DockTheme.FontFamily,
                    FontSize = DockTheme.BodyFontSize,
                    Margin = new Thickness(12),
                });
            }
        }

        /// <summary>HistoryVulcan 入口：不参与排序、不发光、不可取消。</summary>
        private static Button HistoryVulcanButton()
        {
            var glyph = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(8),
                Background = DockTheme.Accent,
                Child = new TextBlock
                {
                    Text = "HV",
                    FontFamily = DockTheme.FontFamily,
                    FontSize = DockTheme.BodyFontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = DockTheme.TextOnAccent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var stack = new StackPanel { Width = 68 };
            stack.Children.Add(glyph);
            stack.Children.Add(new TextBlock
            {
                Text = "主界面",
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            var button = NakedButton(stack, "打开 HistoryVulcan 主界面");
            button.Click += (_, _) => HistoryVulcanLauncher.Open();
            return button;
        }

        private static Button ProjectButton(DockProject project, double maximum)
        {
            var image = new Image
            {
                Source = ProjectIconGenerator.Create(project.Name),
                Width = 52,
                Height = 52,
                Stretch = Stretch.Uniform,
            };

            var opacity = DockWeight.GlowOpacity(project.Weight, maximum);
            var host = new Border { Child = image };
            if (opacity > 0)
            {
                // ShadowDepth=0 即纯发光而非投影；权重越高越亮。
                host.Effect = new DropShadowEffect
                {
                    Color = DockTheme.Glow,
                    ShadowDepth = 0,
                    Opacity = opacity,
                    BlurRadius = DockWeight.GlowBlur(project.Weight, maximum),
                };
            }

            var label = new TextBlock
            {
                Text = project.Number + (project.Pinned ? "  ·" : ""),
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var stack = new StackPanel { Width = 68 };
            stack.Children.Add(host);
            stack.Children.Add(label);
            var button = NakedButton(stack, project.Name);
            button.Click += (_, _) => OpenProject(project);
            var menu = StyledMenu();
            var pin = new MenuItem { Header = project.Pinned ? "取消置顶" : "置顶" };
            pin.Click += (_, _) => MercuryState.Pin(project.Name, !project.Pinned);
            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await MercuryState.RefreshAsync();
            menu.Items.Add(pin);
            menu.Items.Add(refresh);
            button.ContextMenu = menu;
            return button;
        }

        /// <summary>手动指令项按钮：指令字形无光圈，点击经总线执行，右键可移除。</summary>
        private static Button CommandButton(DockCommandEntry entry)
        {
            var glyph = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(8),
                Background = DockTheme.SurfaceAlt,
                BorderBrush = DockTheme.AccentSoft,
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "❯",
                    FontFamily = DockTheme.FontFamily,
                    FontSize = 22,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = DockTheme.Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var stack = new StackPanel { Width = 68 };
            stack.Children.Add(glyph);
            stack.Children.Add(new TextBlock
            {
                Text = entry.Label,
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            var button = NakedButton(stack, entry.Command);
            button.Click += (_, _) =>
            {
                // 自定义指令只有经总线才有意义；总线缺失（旧宿主/烟测）时静默不动作。
                if (Bus != null)
                    _ = Bus.ExecuteAsync(entry.Command, "UI");
            };
            var menu = StyledMenu();
            var remove = new MenuItem { Header = "移除该指令" };
            remove.Click += (_, _) => MercuryState.RemoveCommand(entry.Command);
            var refresh = new MenuItem { Header = "刷新" };
            refresh.Click += async (_, _) => await MercuryState.RefreshAsync();
            menu.Items.Add(remove);
            menu.Items.Add(refresh);
            button.ContextMenu = menu;
            return button;
        }

        /// <summary>项目条目优先经总线执行别名指令，让打开动作与其他指令项同一条链路。</summary>
        private static void OpenProject(DockProject project)
        {
            if (Bus != null)
            {
                _ = OpenProjectViaBusAsync(project);
                return;
            }

            OpenProjectLocally(project);
        }

        private static async Task OpenProjectViaBusAsync(DockProject project)
        {
            // 服务未就绪或指令失败时回退本地打开，保证桌面坞永远能开目录。
            var result = await Bus!
                .ExecuteAsync(MercuryCommandCatalog.BuildOpenProjectCommand(project.Name), "UI")
                .ConfigureAwait(false);
            if (!result.Success)
                OpenProjectLocally(project);
        }

        private static void OpenProjectLocally(DockProject project)
        {
            try
            {
                MercuryState.RecordOpen(project.Name);
                Process.Start(new ProcessStartInfo(project.Path) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // 目录被删等极端情况下点击不得打穿桌面坞。
            }
        }

        private static ContextMenu StyledMenu()
        {
            var menu = new ContextMenu
            {
                Background = DockTheme.PanelBackground,
                Foreground = DockTheme.Label,
                BorderBrush = DockTheme.PanelBorder,
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.BodyFontSize,
            };
            menu.Resources[SystemColors.MenuBrushKey] = DockTheme.PanelBackground;
            menu.Resources[SystemColors.MenuTextBrushKey] = DockTheme.Label;
            menu.Resources[SystemColors.HighlightBrushKey] = DockTheme.Hover;
            menu.Resources[SystemColors.HighlightTextBrushKey] = DockTheme.Label;
            return menu;
        }

        /// <summary>无边框透明按钮，悬停时叠一层浅色半透明，深底上不会闪出白块。</summary>
        private static Button NakedButton(object content, string tooltip)
        {
            var template = new ControlTemplate(typeof(Button));
            var presenterHost = new FrameworkElementFactory(typeof(Border), "Surface");
            presenterHost.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            presenterHost.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            presenterHost.SetValue(Border.PaddingProperty, new Thickness(4));
            presenterHost.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
            template.VisualTree = presenterHost;
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, DockTheme.Hover, "Surface"));
            template.Triggers.Add(hover);

            return new Button
            {
                Content = content,
                Width = 78,
                Height = 82,
                Margin = new Thickness(3),
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Template = template,
                ToolTip = tooltip,
            };
        }
    }
}
