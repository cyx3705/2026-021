using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;
using HistoryVulcan.Core.Modules;
using Mercury.Diagnostics;
using Mercury.Ui;

namespace Mercury;

/// <summary>
/// Mercury 的模块入口：桌面坞、快捷键与指令面在这里挂上宿主总线。
/// </summary>
/// <remarks>
/// **桌面坞是 Mercury 自己的界面，不是前端里的一页**：它在 <see cref="Attach"/> 阶段起在
/// 本模块自有的 STA 线程上，只要模块装载就存在，前端在不在、开没开都与它无关。
/// 桌面坞是唤起前端的入口，若它反过来依赖前端，前端一关就再也叫不回来。
///
/// 前端里的页则相反：宿主 5.0 起不再提供界面生命周期（<c>IUiModule</c>、<c>CreateUi</c> /
/// <c>DestroyUi</c> 与整套停靠 SDK 都已删除），本模块**不再构造任何前端控件**，改用
/// <c>mercury.ui.describe</c> / <c>mercury.ui.actions</c> / <c>mercury.ui.data</c>
/// 把页面描述成数据，由 HistoryAurora 拉取并渲染（见 <see cref="Ui.MercuryPages"/>）。
///
/// 于是本类的生命周期只剩两个点：<see cref="Attach"/> 与 <see cref="Dispose"/>。
/// 宿主在拆除阶段按 <see cref="IDisposable"/> 回收实例，桌面坞的线程必须在那时收干净，
/// 否则它会在可回收装载上下文卸载之后继续跑已卸载的类型。
/// </remarks>
public sealed class MercuryModule : IModuleContextAware, IDisposable
{
    private static readonly object DockGate = new();

    /// <summary>桌面坞自有的 STA 线程，坞的 Dispatcher 跑在它上面。</summary>
    private static Thread? _dockThread;

    /// <summary>坞就绪信号；<see cref="StartDesktopDock"/> 等它之后 <see cref="Attach"/> 才返回。</summary>
    private static readonly ManualResetEventSlim DockReady = new(false);

    /// <summary>坞就绪等待上限。超时只告警不阻断——装不上坞也不该连累整个模块装载。</summary>
    private static readonly TimeSpan DockReadyTimeout = TimeSpan.FromSeconds(10);

    private static DockWindow? _window;
    private bool _disposed;

    /// <summary>宿主注入的指令总线；Shell 进程中自带远程转发。宿主不注入时（旧宿主/烟测）为 null。</summary>
    internal static CommandBus? Bus { get; private set; }

    /// <summary>
    /// 桌面坞是否在跑。供烟测断言坞只归 <see cref="Attach"/>——
    /// 一旦它重新变成前端的下游，前端不在时桌面上就什么都没有。
    /// </summary>
    internal static bool IsDesktopDockRunning => _window != null;

    /// <summary>
    /// 模块日志。宿主 5.0 不再注入日志，这里换成 Mercury 自持的实例；
    /// 调用点写法不变，落点从宿主控制台变成模块数据根下的 <c>logs/</c>。
    /// </summary>
    internal static IShellLog Log { get; } = MercuryLog.Shared;

    public void Attach(IModuleContext context)
    {
        Bus = context.Bus;
        context.RegisterCommands(MercuryCommandCatalog.Register);

        // 服务进程也必须跟随 state.json。它执行绝大多数写状态的指令，若只在建界面时才开监视，
        // 服务侧的偏好副本会永远停在进程启动那一刻，下一次保存就用旧值盖掉界面侧的改动，
        // 两侧算出的收录列表也会不一致，进而互相把对方写的快捷方式当成用户增删。
        MercuryState.StartWatching();

        // 页面是派生状态：坞的内容一变，前端那两页就旧了。模块只负责说「我变了」，
        // 由 Aurora 决定怎么重建（协议 §1.3）。前端不在时这条指令不存在，失败即静默。
        PageInvalidation.Start(context.Bus);

        // 全局快捷键完全由 Mercury 自持：服务在此构造并启动，能力经 mercury.hotkey.* 命令暴露。
        // 宿主不再预扫描模块 DLL 去寻找 IGlobalShortcutHost 实现，也不再驱动注册。
        if (OperatingSystem.IsWindows())
        {
            Input.HotkeyService.Start(context.Bus, Log);
            RegisterOwnShortcuts();

            // 桌面坞在这里起。5.0 之前它一度挂在 CreateUi 上，而宿主只有取到界面注册器
            // （即前端已装载）之后才调 CreateUi，于是唤起前端的入口反过来依赖前端本身——
            // 前端一关就再也叫不回来。现在宿主根本没有那条链，坞只认 Attach。
            StartDesktopDock();
        }
    }

    /// <summary>
    /// Mercury 自有的快捷键同样经命令层注册，而不是走内部特殊路径——它和任何第三方模块
    /// 用的是同一条通道，能力缺失时也会以同样的方式暴露出来。
    /// </summary>
    private static void RegisterOwnShortcuts()
    {
        try
        {
            Input.HotkeyCommands.Register(
                "focus-console",
                "Slash,Slash",
                "vulcan.app.focusconsole",
                "HistoryMercury",
                null);
        }
        catch (Exception ex)
        {
            Log.Warn("hotkey", $"注册 Mercury 自有快捷键失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 起桌面坞：自建 STA 线程 + Dispatcher，坞在这条线程上自持。
    /// </summary>
    /// <remarks>
    /// 不能借前端的界面线程。旧实现以 <c>Application.Current == null</c> 作为"此进程没有界面"
    /// 的判据，而 DEC-008 之后前端在宿主进程内开窗时**刻意不建 WPF Application**，
    /// 于是这个判据恒真、坞永远不建——症状是桌面上什么都没有，日志里一行错误也没有。
    /// 判据换成线程本身：坞有没有地方跑，只取决于本模块能不能开出一条 STA 线程。
    ///
    /// 快捷方式文件夹与资源管理器入口也一并挪到这里。它们是坞的组成部分（坞的收录来源），
    /// 不是前端的；留在 CreateUi 里同样会随前端缺席而消失。
    /// </remarks>
    private static void StartDesktopDock()
    {
        lock (DockGate)
        {
            if (_dockThread != null)
                return;

            _dockThread = new Thread(RunDock)
            {
                Name = "HistoryMercury.Dock",
                // 后台线程：宿主的服务循环拥有进程寿命，坞不该反过来把宿主钉住不退。
                IsBackground = true,
            };
            _dockThread.SetApartmentState(ApartmentState.STA);
            _dockThread.Start();
        }

        // 等坞真的立在桌面上，Attach 才返回。等待放在锁外：StopDesktopDock 要拿同一把锁。
        //
        // 不等会漏掉一个坞。宿主热重载是「先拆旧、再装新」，下一轮的拆除随时可能到来；
        // 若那时建窗还没完成，StopDesktopDock 看到的 _window 是 null，它什么都不关，
        // 而随后建出来的窗口已经没有任何人持有它的引用——桌面上从此多一个关不掉的坞。
        // 等到就绪信号，拆除才一定拆得到东西。
        if (!DockReady.Wait(DockReadyTimeout))
            Log.Warn("dock", $"桌面坞未在 {DockReadyTimeout.TotalSeconds:0.#}s 内就绪，下一次重载可能漏关它");
    }

    private static void RunDock()
    {
        try
        {
            // 先建窗，收录来源留到最后。
            //
            // 建窗只碰 WPF，而资源管理器注册要写注册表并通知外壳，是这里最慢的一步。
            // 反过来排会让桌面白等它：实测重载时那段无坞的空白从约 0.3s 涨到约 1.8s。
            // 坞的内容取自已载入的 state.json，不必等 RefreshAsync 就能画出来。
            var window = new DockWindow();
            _window = window;
            window.Show();
            if (MercuryState.Hidden)
                window.Hide();

            // 未处理异常落日志而不弹框：坞常驻桌面，没有人在屏幕前等着点"确定"。
            Dispatcher.CurrentDispatcher.UnhandledException += (_, args) =>
            {
                Log.Warn("dock", $"桌面坞未处理异常: {args.Exception.Message}");
                args.Handled = true;
            };

            // 坞已在屏幕上，Attach 可以放行了。收录来源还没接，但那不影响桌面上有没有坞。
            Log.Info("dock", "桌面坞已就绪");
            DockReady.Set();

            // 收录来源最后接上。它要写注册表并通知外壳，是三步里最慢的，
            // 排在最后就不会让桌面等它。
            if (!DockShortcutFolder.IsExplorerRegistrationDisabled)
            {
                DockShortcutFolder.StartWatching(MercuryState.ApplyShortcutFolderChanges);
                _ = ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path);
            }

            _ = MercuryState.RefreshAsync();

            Dispatcher.Run();
        }
        catch (Exception ex)
        {
            _window = null;
            Log.Warn("dock", $"桌面坞创建失败: {ex.Message}");
        }
        finally
        {
            // 失败路径也要放行，否则 Attach 会白等满 10s 才继续装载。
            DockReady.Set();
        }
    }

    /// <summary>关坞并收线程。模块卸载前必须完成，否则可回收上下文卸载时坞还在跑它的类型。</summary>
    private static void StopDesktopDock()
    {
        Thread? thread;
        DockWindow? window;
        lock (DockGate)
        {
            thread = _dockThread;
            window = _window;
            _dockThread = null;
            _window = null;
        }

        if (window == null)
            return;

        try
        {
            var dispatcher = window.Dispatcher;
            dispatcher.Invoke(window.Close);
            dispatcher.InvokeShutdown();
            // join 有上限：收不回来也不能把宿主的重载卡死。
            thread?.Join(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            Log.Warn("dock", $"桌面坞关闭失败: {ex.Message}");
        }

    }

    /// <summary>
    /// 宿主拆除模块时回收。必须关窗、停 Dispatcher 并收线程（join 上限 5s）：
    /// 模块跑在可回收的装载上下文里，线程不收就会在卸载后继续跑已卸载的类型。
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        PageInvalidation.Stop();
        DockShortcutFolder.StopWatching();
        MercuryState.StopWatching();
        if (OperatingSystem.IsWindows())
            Input.HotkeyService.Stop();
        StopDesktopDock();
        Bus = null;
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

        /// <summary>HistoryClio 入口：不参与排序、不发光、不可取消。唤起的仍是 HistoryVulcan 宿主窗口。</summary>
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
                    Text = "HC",
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
            var button = NakedButton(stack, "打开 HistoryClio 主界面");
            button.Click += async (_, _) => await MercuryCommands.ShowHostAsync();
            return button;
        }

        private static Button ProjectButton(DockProject project, double maximum)
        {
            // 磁贴当场绘制而不用缓存的 PNG：底色随使用频率变化，按名字缓存的位图表达不了。
            var host = new Border
            {
                Width = 52,
                Height = 52,
                CornerRadius = new CornerRadius(8),
                Background = DockTheme.TileBackground(DockWeight.TileTint(project.Weight, maximum)),

                // 置顶标记：磁贴上的一圈亮黄描边。5.0.1 之前是在标签里追加 "  ·"，
                // 那个点不解释自己是什么——用户看见的是"编号后面莫名多了个点"，
                // 而坞上没有任何地方说它代表置顶。
                //
                // 描边**恒占 2px，只换颜色**。只在置顶时加边框的话，磁贴内容区会在
                // 置顶前后差 4px，而字号是按内容区宽度算出来的：同一个项目一置顶字就变小，
                // 且只有长名字的磁贴才看得出来。
                BorderThickness = new Thickness(2),
                BorderBrush = project.Pinned ? DockTheme.PinnedRing : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = ProjectIconGenerator.ShortLabel(project.Name),
                    FontFamily = DockTheme.FontFamily,
                    // 40 = 52 - 描边 2×2 - 文字左右边距 4×2。
                    FontSize = ProjectIconGenerator.FitFontSize(ProjectIconGenerator.ShortLabel(project.Name), 40, 15),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = DockTheme.TileText,
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.None,
                    TextWrapping = TextWrapping.NoWrap,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                },
            };

            var label = new TextBlock
            {
                Text = project.Number,
                FontFamily = DockTheme.FontFamily,
                FontSize = DockTheme.SmallFontSize,
                Foreground = DockTheme.Label,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.NoWrap,
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
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.NoWrap,
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
