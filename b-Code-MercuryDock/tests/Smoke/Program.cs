using System.IO;
using System.Reflection;
using System.Text.Json;
using MercuryDock;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;
using BaseVariable;

var assembly = typeof(MercuryDockCommands).Assembly;
var previousExplorerRegistrationSetting = Environment.GetEnvironmentVariable("MERCURYDOCK_DISABLE_EXPLORER_REGISTRATION");
Environment.SetEnvironmentVariable("MERCURYDOCK_DISABLE_EXPLORER_REGISTRATION", "1");
// 状态目录隔离：整个 Smoke 不碰真实 %APPDATA% 的 state.json。
var stateOverride = Path.Combine(Path.GetTempPath(), "mercurydock-state-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("MERCURYDOCK_STATE_DIRECTORY", stateOverride);

var moduleInfos = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
    .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
    .ToList();

Equal(1, moduleInfos.Count, "独立程序集必须只有一个模块入口");
Equal("MercuryDock", moduleInfos[0].ModuleName, "模块域必须使用稳定模块名");
Equal("dock", moduleInfos[0].GetType().GetProperty("CommandPrefix")?.GetValue(moduleInfos[0]),
    "旧命令前缀必须保持兼容");
Equal("3.2.4", moduleInfos[0].Version, "模块版本");
Equal(typeof(MercuryDockCommands), moduleInfos[0].MainClassType, "命令入口类型");

Equal(
    16,
    moduleInfos[0].MainClassType!
        .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Count(method => !method.IsSpecialName),
    "dock 指令数(别名指令走 RegisterCommands，不占反射方法数)");

var uiTypes = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(IUiModule).IsAssignableFrom(type))
    .ToList();
Equal(1, uiTypes.Count, "独立程序集必须只注册一个 UI 生命周期");
Equal(typeof(MercuryDockUiModule), uiTypes[0], "UI 生命周期类型");

// 桌面 Shell 同时注册管理页面和活动坞；无窗口 Smoke 只断言注册器生命周期。
True(
    typeof(IShellUiAware).IsAssignableFrom(typeof(MercuryDockUiModule)),
    "必须实现 IShellUiAware 才能被宿主注入并据此判定归属");
var registrar = new RecordingRegistrar();
var shellHosted = new MercuryDockUiModule();
try
{
    ((IShellUiAware)shellHosted).ShellUi = registrar;
    shellHosted.CreateUi();
    Equal(1, registrar.Registered.Count, "桌面侧必须注册且只注册一次管理页面");
    Equal("dock.manager", registrar.Registered[0], "桌面侧注册的必须是管理页面");
    shellHosted.CreateUi();
    Equal(1, registrar.Registered.Count, "重复 CreateUi 不得重复注册");
    shellHosted.DestroyUi();
    Equal(1, registrar.Disposed, "DestroyUi 必须释放管理页面注册句柄");
}
finally
{
    Environment.SetEnvironmentVariable("MERCURYDOCK_DISABLE_EXPLORER_REGISTRATION", previousExplorerRegistrationSetting);
}

var cache = Path.Combine(Path.GetTempPath(), "activedock-icons-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(cache);
try
{
    var bitmap = ProjectIconGenerator.Create("2026-020-中文项目", cache);
    Equal(64, bitmap.PixelWidth, "活动坞图标宽度");
    Equal(64, bitmap.PixelHeight, "活动坞图标高度");
}
finally
{
    Directory.Delete(cache, recursive: true);
}

// V1.1.2 布局规则：右下角锚定、尺寸钳制、只开左/上/左上的调整命中区。
var work = new System.Windows.Rect(0, 0, 1920, 1040);
var (anchorLeft, anchorTop) = DockLayout.Anchor(work, 360, 200);
Equal(1920 - 360 - DockLayout.Margin, anchorLeft, "锚点左边界");
Equal(1040 - 200 - DockLayout.Margin, anchorTop, "锚点上边界");

// 尺寸变大后右下角必须不动：右下角 = Left+Width、Top+Height 应保持不变。
var (wideLeft, tallTop) = DockLayout.Anchor(work, 500, 300);
Equal(anchorLeft + 360, wideLeft + 500, "加宽后右边界不动");
Equal(anchorTop + 200, tallTop + 300, "加高后下边界不动");

// 150% 缩放时必须以原生工作区而非 WPF DIP 工作区为基准，否则会停在屏幕中部。
var nativeWork = new System.Windows.Rect(0, 0, 2560, 1400);
var (nativeLeft, nativeTop) = DockLayout.Anchor(nativeWork, 696, 198);
Equal(1848.0, nativeLeft, "原生工作区右边界");
Equal(1186.0, nativeTop, "原生工作区下边界");

Equal(DockLayout.MinWidth, DockLayout.ClampWidth(10), "宽度下限");
Equal(DockLayout.MaxWidth, DockLayout.ClampWidth(9999), "宽度上限");
Equal(DockLayout.MinHeight, DockLayout.ClampHeight(1), "高度下限");
Equal(DockLayout.MaxHeight, DockLayout.ClampHeight(9999), "高度上限");
Equal(DockLayout.DefaultWidth, DockLayout.ClampWidth(double.NaN), "宽度 NaN 回退缺省");
Equal(DockLayout.DefaultHeight, DockLayout.ClampHeight(0), "高度非正回退缺省");

Equal(DockLayout.HitTopLeft, DockLayout.HitTest(2, 2, 360, 200), "左上角可调");
Equal(DockLayout.HitLeft, DockLayout.HitTest(2, 100, 360, 200), "左边框可调");
Equal(DockLayout.HitTop, DockLayout.HitTest(100, 2, 360, 200), "上边框可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(358, 198, 360, 200), "右下角不可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(358, 100, 360, 200), "右边框不可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(100, 198, 360, 200), "下边框不可调");
Equal(DockLayout.HitNone, DockLayout.HitTest(100, 100, 360, 200), "窗口体不可调");

// V2.0.0 权重与光圈。
var now = DateTimeOffset.UtcNow;
Equal(1.0, DockWeight.Decay(1.0, now, now, 7), "未经过时间不衰减");
True(Math.Abs(DockWeight.Decay(1.0, now.AddDays(-7), now, 7) - 0.5) < 1e-6, "一个半衰期衰减到一半");
True(DockWeight.Decay(1.0, now.AddDays(-14), now, 7) < 0.26, "两个半衰期继续衰减");
True(DockWeight.Accumulate(1.0, now.AddDays(-7), now, DockWeight.ClickWeight, 7) > 1.4, "衰减后再累加一次点击");
Equal(0.0, DockWeight.GlowOpacity(0, 0), "无权重不发光");
Equal(DockWeight.MaxGlowOpacity, DockWeight.GlowOpacity(5, 5), "最高权重光圈最亮");
True(DockWeight.GlowOpacity(1, 5) < DockWeight.GlowOpacity(4, 5), "权重越高光圈越亮");
Equal(DockWeight.MaxGlowBlur, DockWeight.GlowBlur(5, 5), "最高权重光圈最大");

// 只统计项目主文件夹：子目录不得计入。
True(RecentFolders.SamePath(@"C:\A\B", @"c:\a\b\"), "路径比较忽略大小写与尾斜杠");
True(!RecentFolders.SamePath(@"C:\A\B", @"C:\A\B\sub"), "子文件夹不得视为项目主文件夹");
Equal("2026-022-WBall", RecentFolders.StripCopySuffix("2026-022-WBall (2)"), "去掉重名副本后缀");

var policy = new DockPolicy { MinItems = 0, MaxItems = 999, HalfLifeDays = 0 }.Normalized();
True(policy.MinItems >= DockPolicy.LowestItems, "最少显示数下限");
True(policy.MaxItems <= DockPolicy.HighestItems, "最多显示数上限");
True(policy.HalfLifeDays >= DockPolicy.ShortestHalfLifeDays, "半衰期下限");
Equal("dock.manager", MercuryDockManagerView.CreateDescriptor().Id, "管理页面窗口 ID");
Equal(DockSide.Center, MercuryDockManagerView.CreateDescriptor().DefaultSide, "管理页默认注册到中央主文档区");

// 3.1.0 指令总线：别名指令经 RegisterCommands 注册，dock.* 反射指令不受影响。
True(
    typeof(IModuleContextAware).IsAssignableFrom(typeof(MercuryDockUiModule)),
    "必须实现 IModuleContextAware 才能接入宿主指令总线");
var registry = new CommandRegistry();
MercuryDockAliasCommands.Register(registry);
True(registry.TryGet(MercuryDockAliasCommands.OpenCommandName, out var alias), "必须注册 mercury.dock.open 别名指令");
Equal("app", alias!.CommandClass, "别名指令归类");
Equal(
    "mercury.dock.open 2026-021-HistoryMercury",
    MercuryDockAliasCommands.BuildOpenCommandText("2026-021-HistoryMercury"),
    "普通项目名不加引号");
Equal(
    "mercury.dock.open \"a b\"",
    MercuryDockAliasCommands.BuildOpenCommandText("a b"),
    "含空白参数必须加引号");

using var catalog = JsonDocument.Parse("""
[{"commandName":"mercury.dock.open","domain":"MercuryDock","commandClass":"app","summary":"打开项目"},
 {"commandName":"dock.list","domain":"MercuryDock","commandClass":"projects"},
 {"other":1}]
""");
var catalogItems = MercuryDockAliasCommands.ParseCommandCatalog(catalog.RootElement);
Equal(2, catalogItems.Count, "解析 command.list 的 JsonElement");
Equal("MercuryDock", catalogItems[0].Domain, "指令目录域字段");
Equal("projects", catalogItems[1].CommandClass, "指令目录类字段");
True(
    MercuryDockAliasCommands.FallbackCommandCatalog().Any(item => item.Name == MercuryDockAliasCommands.OpenCommandName),
    "保底清单必须含常驻默认指令");

// 3.2.0 级联候选树：域→类→方法逐级下钻，分支确认后进入下一级。
var tree = CommandOptionTree.Build(
[
    new CommandCatalogItem("mercury.dock.open", "MercuryDock", "app", "打开项目目录"),
    new CommandCatalogItem("mercury.dock.close", "MercuryDock", "app", ""),
    new CommandCatalogItem("dock.pin", "MercuryDock", "projects", ""),
    new CommandCatalogItem("help", "", "", "帮助"),
]);
var roots = tree.ChildrenOf("");
Equal(3, roots.Count, "根级候选数");
True(roots.Single(option => option.Display.StartsWith("mercury")).IsBranch, "mercury 是分支");
True(!roots.Single(option => option.Display == "help").IsBranch, "help 是叶子");
Equal("help", roots.Single(option => option.Display == "help").Text, "叶子确认文本即指令全文");
var secondLevel = tree.ChildrenOf("mercury.");
Equal(1, secondLevel.Count, "mercury 下只有 dock");
Equal("mercury.dock.", secondLevel[0].Text, "分支确认后进入下一级");
var methods = tree.ChildrenOf("mercury.dock.");
Equal(2, methods.Count, "方法级两条");
Equal("mercury.dock.open", methods.Single(option => option.Display == "open").Text, "叶子全文");
Equal("打开项目目录", methods.Single(option => option.Display == "open").Detail, "叶子带摘要");
Equal(1, tree.ChildrenOf("mercury.d").Count, "过滤段按前缀收缩候选");
Equal(1, tree.ChildrenOf("mercury.dock.o").Count, "方法级过滤只剩 open");
Equal(0, tree.ChildrenOf("nope.").Count, "未知路径无候选");

// 3.1.0 指令项状态：加入去重、移除不区分大小写；全程在隔离状态目录。
True(MercuryDockState.AddCommand("demo.alpha  1"), "加入指令项必须成功");
True(MercuryDockState.AddCommand("demo.alpha 1"), "重复加入视为成功");
Equal(1, MercuryDockState.CommandEntries.Count, "折叠空白后按指令文本去重");
Equal("demo.alpha 1", MercuryDockState.CommandEntries[0].Command, "指令文本折叠连续空白");
True(!MercuryDockState.AddCommand("   "), "空白指令不得入坞");
True(MercuryDockState.RemoveCommand("DEMO.ALPHA 1"), "移除指令不区分大小写");
Equal(0, MercuryDockState.CommandEntries.Count, "移除后指令项清空");
True(!MercuryDockState.RemoveCommand("demo.alpha 1"), "移除不存在项返回 false");

// 3.0.0 工作树根回退：即使配置值失效，也要能从默认根 HistoryVesta 列出项目。
True(MercuryDockState.ListWorktreeProjects().Count > 0, "工作树扫描必须经默认根列出项目");
Equal("OHS 项目", ExplorerNamespaceRegistration.DisplayName, "Explorer 入口名称");
True(Guid.TryParse(ExplorerNamespaceRegistration.EntryClsid, out _), "Explorer 入口 CLSID 必须有效");
True(!ExplorerNamespaceRegistration.RegisterOrUpdate(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))).Success,
    "不存在的工作树不得写入 Explorer 注册项");

var shortcuts = Path.Combine(Path.GetTempPath(), "activedock-shortcuts-" + Guid.NewGuid().ToString("N"));
var projectRoot = Path.Combine(shortcuts, "source");
var firstProject = Path.Combine(projectRoot, "2026-001-First");
var secondProject = Path.Combine(projectRoot, "2026-002-Second");
Directory.CreateDirectory(firstProject);
Directory.CreateDirectory(secondProject);
try
{
    var first = new DockProject("2026-001-First", "2026-001", firstProject, now, false);
    var second = new DockProject("2026-002-Second", "2026-002", secondProject, now, false);
    var initial = DockShortcutFolder.Synchronize([first, second], shortcuts);
    Equal(2, initial.Written, "活动坞项目必须各生成一条快捷方式");
    Equal(2, Directory.EnumerateFiles(shortcuts, "*.lnk").Count(), "快捷方式文件数与活动坞项目一致");
    var reconciled = DockShortcutFolder.Synchronize([second], shortcuts);
    Equal(1, reconciled.Removed, "退出活动坞的项目快捷方式必须移除");
    Equal(1, Directory.EnumerateFiles(shortcuts, "*.lnk").Count(), "同步后快捷方式必须与活动坞一致");

    var beforeUserEdit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine(shortcuts, "2026-001-First.lnk")] = firstProject,
    };
    var afterUserEdit = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [Path.Combine(shortcuts, "2026-002-Second.lnk")] = secondProject,
    };
    var userChanges = DockShortcutFolder.Compare(beforeUserEdit, afterUserEdit);
    Equal(firstProject, userChanges.RemovedTargets.Single(), "用户删除快捷方式必须映射回原项目");
    Equal(secondProject, userChanges.AddedTargets.Single(), "用户新增快捷方式必须映射回目标项目");
}
finally
{
    Directory.Delete(shortcuts, recursive: true);
}

try
{
    if (Directory.Exists(stateOverride))
        Directory.Delete(stateOverride, recursive: true);
}
catch (Exception)
{
    // 文件监视器可能短暂持有目录句柄，隔离目录留在临时目录无害。
}

Console.WriteLine(
    "MercuryDock.Smoke: PASS (1 module, 16 commands + mercury.dock.open alias, 1 UI module, shell-hosted manager, Explorer entry, bottom-right layout, weight+glow, command entries, cascading command tree, worktree-root fallback)");

static void True(bool condition, string message)
{
    if (!condition)
        throw new InvalidOperationException(message);
}

static void Equal<T>(T expected, T actual, string message)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{message}: expected={expected}, actual={actual}");
}

/// <summary>记录注册行为，供宿主判据断言。</summary>
file sealed class RecordingRegistrar : IShellUiRegistrar
{
    public List<string> Registered { get; } = [];

    public int Disposed { get; private set; }

    public bool IsUiThread => true;

    public void Invoke(Action action) => action();

    public IDisposable RegisterToolWindow(HistoryVulcan.Core.Docking.ToolWindowDescriptor descriptor, string owner)
    {
        Registered.Add(descriptor.Id);
        return new Handle(() => Disposed++);
    }

    public void UnregisterToolWindow(string id)
    {
    }

    public void UnregisterOwner(string owner)
    {
    }

    private sealed class Handle(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
