using System.Reflection;
using System.Text.Json;
using System.IO;
using System.Xml.Linq;
using BaseVariable;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Modules;
using Mercury;
using Mercury.Input;
using Mercury.Ui;

var previousExplorerRegistrationSetting = Environment.GetEnvironmentVariable("MERCURY_DISABLE_EXPLORER_REGISTRATION");
Environment.SetEnvironmentVariable("MERCURY_DISABLE_EXPLORER_REGISTRATION", "1");
var stateOverride = Path.Combine(Path.GetTempPath(), "mercury-state-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("MERCURY_STATE_DIRECTORY", stateOverride);
// 宿主 5.9.0 统一契约：数据目录由宿主在 Attach 时给。烟测没有宿主，照 Attach 的第一步先配置一次。
var smokeDataRoot = Path.Combine(stateOverride, "ModuleData", "HistoryMercury");
MercuryPaths.Configure(smokeDataRoot, null, null);

var assembly = typeof(MercuryCommands).Assembly;
var moduleInfos = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(ModuleInfoBase).IsAssignableFrom(type))
    .Select(type => (ModuleInfoBase)Activator.CreateInstance(type)!)
    .ToList();

Equal(1, moduleInfos.Count, "Exactly one module entry point is required.");
Equal("HistoryMercury", moduleInfos[0].ModuleName, "Module name");
Equal("mercury", moduleInfos[0].GetType().GetProperty("CommandPrefix")?.GetValue(moduleInfos[0]), "Command prefix");
// 版本不写字面量：下面会直接与 module.manifest.json 比对，避免第三份副本再次漂移。
True(!string.IsNullOrWhiteSpace(moduleInfos[0].Version), "Module version must be reported.");
Equal(null, moduleInfos[0].MainClassType, "Commands must not use reflection projection.");

// 宿主 5.0 删除了 IUiModule / IShellUiAware / CreateUi / DestroyUi 与整套停靠 SDK。
// 模块入口现在只剩两个点：IModuleContextAware.Attach 与 IDisposable.Dispose，
// 两者都由 ModuleHost 调用，因此都是与宿主的约定，而不是模块的私事。
var entryTypes = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(IModuleContextAware).IsAssignableFrom(type))
    .ToList();
Equal(1, entryTypes.Count, "Exactly one module entry point is required.");
Equal(typeof(MercuryModule), entryTypes[0], "Module entry type");
True(typeof(IDisposable).IsAssignableFrom(typeof(MercuryModule)),
    "The module must be disposable: the host reclaims instances before unloading the collectible context.");
True(!assembly.GetTypes().Any(type => type.GetInterfaces()
        .Any(contract => contract.Name is "IUiModule" or "IShellUiAware" or "IShellCommandWorkbenchAware")),
    "No type may implement the host UI lifecycle contracts deleted in HistoryVulcan 5.0.");

// 桌面坞只归 Attach。构造实例本身不得把坞带上桌面，Dispose 必须是可重入的空操作。
{
    var module = new MercuryModule();
    True(!MercuryModule.IsDesktopDockRunning, "Constructing the module must not start the desktop dock.");
    module.Dispose();
    module.Dispose();
    True(!MercuryModule.IsDesktopDockRunning, "Dispose must leave no dock behind.");
}

// 模块不得自建前端组件（DEC-005 / 页面注册协议）。4.x 的三个页面是本模块编译进来的 XAML，
// 曾因本地合并 Aurora 样式字典而在注册时抛 StaticResourceExtension 把整页带走；
// 描述化之后模块里根本不该再有编译后的 BAML，这条断言比"BAML 里不含 Aurora pack URI"更强。
AssertNoCompiledXaml(assembly);

Equal("Mercury", ProjectIconGenerator.ShortLabel("2026-021-HistoryMercury"),
    "Project tile short label removes number and History prefix.");
True(ProjectIconGenerator.FitFontSize("A very long project suffix", 44) >= 6,
    "Project tile font remains a complete, non-ellipsis rendering path.");

var registry = new TestRegistrar();
MercuryCommandCatalog.Register(registry);
var commands = registry.All();
// 27 = 4.8.1 的 26 条 + shortcut.pick。5.3.0 删了页面协议三条与 dock.run（不再往 Aurora 注册页面）。
Equal(27, commands.Count, "Command count");
True(commands.All(command => System.Text.RegularExpressions.Regex.IsMatch(command.Name, "^[a-z]+(?:\\.[a-z0-9]+)+$")),
    "Commands must be lowercase dot-separated identifiers.");
True(commands.All(command => command.Name.StartsWith("mercury.", StringComparison.Ordinal)),
    "All module commands must use the mercury domain.");
True(!commands.Any(command => command.Name.StartsWith("dock.", StringComparison.Ordinal)
    || command.Name.StartsWith("mercury.dock.open", StringComparison.Ordinal)),
    "Legacy command names must not remain registered.");
True(registry.TryGet("mercury.app.status", out var status) && status.Readonly, "Status command must be readonly.");
True(status.CommandClass == "app", "Status must declare the app class.");

// DEC-025：合法形态恰有两种——三段业务指令，或两段无类直接方法。
True(commands.All(command => command.Name.Split('.').Length is 2 or 3),
    "Every mercury command must be <domain>.<class>.<method> or <domain>.<method>.");
// 三段指令必须声明类；两段直接方法必须不声明类，否则两种形态会互相污染。
True(commands.Where(command => command.Name.Split('.').Length == 3)
        .All(command => !string.IsNullOrWhiteSpace(command.CommandClass)),
    "Three-segment commands must declare a class.");
True(commands.Where(command => command.Name.Split('.').Length == 2)
        .All(command => string.IsNullOrWhiteSpace(command.CommandClass)),
    "Two-segment direct methods must stay classless.");

// mercury.go 是域聚焦的唯一入口，且必须是无类直接方法：
// 它切换的是控制台状态，不隶属任何业务类。
True(registry.TryGet("mercury.go", out var go), "Domain-focus command must be registered.");
True(string.IsNullOrWhiteSpace(go.CommandClass), "mercury.go must be a classless direct method.");
// 5.0 起 mercury.go 只是把域筛选转发给前端的 aurora.log.source，自己不碰界面对象；
// 需要 UI 线程的是那一条。这里再声明一次只会多编组一次。
True(!go.RequiresUiThread, "mercury.go only relays to the frontend command and must not claim the UI thread.");
True(go.Parameters.Any(parameter => parameter.Name == "domain" && !parameter.Required),
    "mercury.go must take an optional domain parameter so the bare form exits focus.");
Equal("registry.domains", go.Annotations["completion.values.domain"],
    "mercury.go domain candidates must be declared by registration metadata.");
True(registry.TryGet("mercury.shortcut.wakeconsole", out _), "Wake-console orchestration command must be registered.");
True(registry.TryGet("mercury.shortcut.open", out var openShortcut),
    "Shortcut-open command must be registered.");
True(registry.TryGet("mercury.shortcut.add", out var addShortcut),
    "Shortcut-add command must be registered.");
True(registry.TryGet("mercury.dock.add", out var dockAdd),
    "Dock-add command must be registered.");
True(registry.TryGet("mercury.dock.remove", out var dockRemove),
    "Dock-remove command must be registered.");
True(dockAdd.Parameters.Single(parameter => parameter.Name == "command") is { Required: true, Position: 0 },
    "Dock-add command text must be the unique positional parameter.");
True(dockRemove.Parameters.Single(parameter => parameter.Name == "command") is { Required: true, Position: 0 },
    "Dock-remove command text must be the unique positional parameter.");
Equal("mercury.dock.commands", dockRemove.Annotations["completion.values.command"],
    "Dock-remove candidates must come from the Mercury-owned provider.");
True(openShortcut.Parameters.Single() is { Name: "path", Required: true, Position: 0 },
    "Shortcut-open path must be the unique positional parameter.");
True(addShortcut.Parameters.Single() is { Name: "path", Required: true, Position: 0 },
    "Shortcut-add path must be the unique positional parameter.");
True(registry.TryGet("mercury.proj.pin", out var pin) && !pin.Readonly, "Project write command must be present.");
True(registry.TryGet("mercury.usage.forget", out var forget) && forget.Level == CommandLevel.Ask,
    "Usage reset must require confirmation.");
True(registry.TryGet(MercuryCommandCatalog.ProjectOpenCommandName, out var openProject), "Project open command must be registered.");
Equal("mercury.proj.open 2026-021-HistoryMercury", MercuryCommandCatalog.BuildOpenProjectCommand("2026-021-HistoryMercury"),
    "Plain project name command text");
Equal("mercury.proj.open \"a b\"", MercuryCommandCatalog.BuildOpenProjectCommand("a b"),
    "Whitespace project name command text");

// 命令目录只有一个来源：CreateDescriptors。此前还有一份供界面补全用的"回退目录"，
// 它是同一份数据的第二个副本，随描述化一起去掉了。
True(MercuryCommandCatalog.CreateDescriptors().Select(command => command.Name)
    .OrderBy(name => name, StringComparer.Ordinal)
    .SequenceEqual(commands.Select(command => command.Name).OrderBy(name => name, StringComparer.Ordinal)),
    "The registry must contain exactly the declared descriptors.");

True(MercuryState.AddCommand("mercury.proj.list"), "Adding a dock command must succeed.");
True(MercuryState.AddCommand("mercury.proj.list"), "Adding the same dock command is idempotent.");
Equal(1, MercuryState.CommandEntries.Count, "Dock command entries must deduplicate.");
True(MercuryState.RemoveCommand("MERCURY.PROJ.LIST"), "Dock command removal must ignore case.");
True(MercuryCommands.AddDockCommand(" mercury.proj.list ").Success,
    "The dock-add bus handler must normalize and persist commands.");
True(MercuryCommands.RemoveDockCommand("MERCURY.PROJ.LIST").Success,
    "The dock-remove bus handler must remove commands case-insensitively.");
True(!MercuryCommands.RemoveDockCommand("mercury.proj.list").Success,
    "Removing a missing dock command must return a clear failure.");
Equal(
    "mercury.shortcut.open \"C:\\A  B.lnk\"",
    MercuryState.NormalizeCommand("  mercury.shortcut.open   \"C:\\A  B.lnk\"  "),
    "Command normalization must preserve consecutive whitespace inside quoted paths.");
Equal(
    "mercury.shortcut.open \"C:\\A\\\"  B.lnk\"",
    MercuryState.NormalizeCommand("mercury.shortcut.open  \"C:\\A\\\"  B.lnk\""),
    "An escaped quote must not end the quoted argument or collapse its spaces.");
Equal(
    "mercury.shortcut.open \"C:\\A\\\\\" B.lnk",
    MercuryState.NormalizeCommand("mercury.shortcut.open  \"C:\\A\\\\\"  B.lnk"),
    "An even backslash run must leave the following quote unescaped.");

var shortcutTestRoot = Path.Combine(Path.GetTempPath(), "mercury-shortcut-command-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(shortcutTestRoot);
try
{
    var target = Path.Combine(shortcutTestRoot, "target file.txt");
    File.WriteAllText(target, "test");
    True(ShortcutFileService.TryResolve(target, out var directSource, out var directTarget, out _),
        "Direct files must resolve.");
    Equal(Path.GetFullPath(target), directSource, "Direct source normalization");
    Equal(Path.GetFullPath(target), directTarget, "Direct target normalization");

    var link = Path.Combine(shortcutTestRoot, "Common Tool.lnk");
    ShortcutFileService.WriteShortcut(link, target, "Smoke shortcut");
    True(ShortcutFileService.TryResolve(link, out var linkSource, out var linkTarget, out var linkError),
        $"Windows shortcut must resolve: {linkError}");
    Equal(Path.GetFullPath(link), linkSource, "Shortcut source normalization");
    Equal(Path.GetFullPath(target), linkTarget, "Shortcut target resolution");

    var added = MercuryCommands.AddShortcut(link);
    True(added.Success, added.Message);
    var shortcutEntry = MercuryState.CommandEntries.Single();
    Equal("Common Tool", shortcutEntry.Label, "Shortcut dock label");
    Equal(MercuryCommandCatalog.BuildOpenShortcutCommand(link), shortcutEntry.Command,
        "Shortcut dock command quotes the normalized source path.");
    True(MercuryCommands.AddShortcut(link).Success, "Adding the same shortcut remains successful.");
    Equal(1, MercuryState.CommandEntries.Count, "Shortcut dock entries must deduplicate.");
    True(MercuryState.RemoveCommand(shortcutEntry.Command), "Shortcut test entry cleanup");

    True(!ShortcutFileService.TryResolve(Path.Combine(shortcutTestRoot, "missing.txt"),
            out _, out _, out var missingError)
         && missingError.Contains("不存在", StringComparison.Ordinal),
        "Missing shortcut sources return an explicit error.");

    var missingTargetLink = Path.Combine(shortcutTestRoot, "Missing Target.lnk");
    ShortcutFileService.WriteShortcut(
        missingTargetLink,
        Path.Combine(shortcutTestRoot, "missing-target.txt"),
        "Missing target");
    True(!ShortcutFileService.TryResolve(missingTargetLink, out _, out _, out var targetError)
         && targetError.Contains("目标不存在", StringComparison.Ordinal),
        "Shortcuts with missing targets return an explicit error.");

    var previousVulcanExecutable = Environment.GetEnvironmentVariable("MERCURY_VULCAN_EXECUTABLE");
    var fakeVulcan = Path.Combine(shortcutTestRoot, "HistoryVulcan.exe");
    File.WriteAllBytes(fakeVulcan, []);
    try
    {
        Environment.SetEnvironmentVariable("MERCURY_VULCAN_EXECUTABLE", fakeVulcan);
        Equal(Path.GetFullPath(fakeVulcan), HistoryVulcanLauncher.ResolveExecutable(),
            "HV cold-start fallback accepts only an explicit HistoryVulcan.exe.");
    }
    finally
    {
        Environment.SetEnvironmentVariable("MERCURY_VULCAN_EXECUTABLE", previousVulcanExecutable);
    }
}
finally
{
    Directory.Delete(shortcutTestRoot, recursive: true);
}

Equal("HistoryVulcan", MercuryPaths.HostName, "Host data root name");
Equal(
    Path.Combine(Path.GetFullPath(smokeDataRoot), DockShortcutFolder.FolderName),
    DockShortcutFolder.Path,
    "Shortcut folder must live in the data directory the host hands out (outside the package slot).");
True(
    !DockShortcutFolder.Path.Contains(
        Path.DirectorySeparatorChar + "Modules" + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase),
    "Shortcut files must never mutate the manifest-verified runtime package.");
Equal("HistoryClio 项目", ExplorerNamespaceRegistration.DisplayName, "Explorer entry name");
Equal(@"C:\OneHistory\HistoryClio", MercuryLibraryRoot.Default, "Default project library is HistoryClio");
Equal(MercuryLibraryRoot.Default, MercuryLibraryRoot.Resolve(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))),
    "A configured library root that no longer exists falls back to HistoryClio.");
True(Guid.TryParse(ExplorerNamespaceRegistration.EntryClsid, out _), "Explorer CLSID must be valid.");

var work = new System.Windows.Rect(0, 0, 1920, 1040);
var (left, top) = DockLayout.Anchor(work, 360, 200);
Equal(1920 - 360 - DockLayout.Margin, left, "Dock right alignment");
Equal(1040 - 200 - DockLayout.Margin, top, "Dock bottom alignment");

var shortcutRoot = Path.Combine(Path.GetTempPath(), "mercury-shortcuts-" + Guid.NewGuid().ToString("N"));
var firstProject = Path.Combine(shortcutRoot, "2026-001-First");
var secondProject = Path.Combine(shortcutRoot, "2026-002-Second");
Directory.CreateDirectory(firstProject);
Directory.CreateDirectory(secondProject);
try
{
    DockShortcutFolder.StartWatching(_ => { }, shortcutRoot);
    True(DockShortcutFolder.IsWatching, "The shortcut watcher must start for a module lifecycle.");
    DockShortcutFolder.StopWatching();
    True(!DockShortcutFolder.IsWatching, "Stopping must release the shortcut watcher handle.");
    DockShortcutFolder.StartWatching(_ => { }, shortcutRoot);
    True(DockShortcutFolder.IsWatching, "The shortcut watcher must restart after an unload.");
    DockShortcutFolder.StopWatching();

    var now = DateTimeOffset.UtcNow;
    var first = new DockProject("2026-001-First", "2026-001", firstProject, now, false);
    var second = new DockProject("2026-002-Second", "2026-002", secondProject, now, false);
    var initial = DockShortcutFolder.Synchronize([first, second], shortcutRoot);
    Equal(2, initial.Written, "Each dock project must receive one shortcut.");
    True(initial.Changed, "Creating shortcuts must report a change.");
    var reconciled = DockShortcutFolder.Synchronize([second], shortcutRoot);
    Equal(1, reconciled.Removed, "Removed dock projects must lose their shortcuts.");

    // 内容一致时必须一个字节都不写：这个文件夹是"此电脑"下的命名空间扩展，每次写入都会让
    // 所有资源管理器窗口进程刷新该节点，过去每次刷新都全量重写才是外壳变卡的直接来源。
    var idempotent = DockShortcutFolder.Synchronize([second], shortcutRoot);
    Equal(0, idempotent.Written, "Unchanged shortcuts must not be rewritten.");
    Equal(0, idempotent.Removed, "Unchanged sync must not delete shortcuts.");
    Equal(1, idempotent.Unchanged, "Matching shortcuts must be reported as unchanged.");
    True(!idempotent.Changed, "Unchanged sync must not report a change.");

    // 受管字段变了仍必须重写，幂等不能退化成"永不更新"。
    var rewritten = DockShortcutFolder.Synchronize(
        [second with { Name = "2026-002-Renamed" }], shortcutRoot);
    Equal(1, rewritten.Written, "A changed managed field must rewrite the shortcut.");
    Equal(0, rewritten.Removed, "Rewriting in place must not delete the shortcut.");
}
finally
{
    Directory.Delete(shortcutRoot, recursive: true);
}

// 快捷方式增删 → 收录意图。这个文件夹会被界面进程、服务进程和用户三方写入，把对方的写入
// 读成用户操作过去会让同一个项目同时进入 Pins 和 Excluded，并触发刷新自激。
{
    static IReadOnlySet<string> Names(params string[] names)
        => new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    var none = Names();

    var rewrite = MercuryState.ResolveShortcutIntent(Names("2026-001-A"), Names("2026-001-A"), none, none);
    Equal(0, rewrite.Exclude.Count, "A link rewritten in one round must not be read as removal.");
    Equal(0, rewrite.Include.Count, "A link rewritten in one round must not be read as an addition.");

    var removal = MercuryState.ResolveShortcutIntent(Names("2026-001-A"), none, none, Names("2026-001-A"));
    Equal("2026-001-A", removal.Exclude.Single(), "Deleting a link must exclude the project.");
    Equal(0, removal.Include.Count, "Deleting a link must not also include the project.");

    var alreadyExcluded = MercuryState.ResolveShortcutIntent(
        Names("2026-001-A"), none, Names("2026-001-A"), none);
    Equal(0, alreadyExcluded.Exclude.Count, "An already excluded project must not be excluded again.");

    var addition = MercuryState.ResolveShortcutIntent(none, Names("2026-002-B"), Names("2026-002-B"), none);
    Equal("2026-002-B", addition.Include.Single(), "Adding a link must include the project.");
    Equal(0, addition.Exclude.Count, "Adding a link must not also exclude the project.");

    var alreadyPinned = MercuryState.ResolveShortcutIntent(none, Names("2026-002-B"), none, Names("2026-002-B"));
    Equal(0, alreadyPinned.Include.Count, "An already pinned project must not be pinned again.");

    var sanitized = MercuryState.SanitizePins(
        ["2026-001-A", "2026-002-B"], Names("2026-001-A"));
    Equal(1, sanitized.Count, "Excluded projects must not remain pinned.");
    True(sanitized.Contains("2026-002-B"), "Sanitising pins must keep unrelated pins.");
}

// 快捷键不再经由宿主的 IGlobalShortcutModule 扫描注册，而是由 Mercury 自持并以
// mercury.hotkey.* 命令暴露。这里改为断言命令面存在且按键文法可往返，
// 因为「命令名 + 参数名」现在就是这项能力的全部对外契约。
{
    var hotkeyRegistry = new TestRegistrar();
    MercuryCommandCatalog.Register(hotkeyRegistry);
    foreach (var name in new[] { "mercury.hotkey.register", "mercury.hotkey.unregister", "mercury.hotkey.list" })
        True(hotkeyRegistry.TryGet(name, out _), $"Hotkey command must be registered: {name}.");

    var registerCommand = hotkeyRegistry.All().Single(c => c.Name == "mercury.hotkey.register");
    foreach (var parameter in new[] { "id", "stroke", "command" })
        True(registerCommand.Parameters.Any(p => p.Name == parameter && p.Required),
            $"mercury.hotkey.register must require parameter {parameter}.");
}

using (var shortcuts = new Mercury.Input.GlobalShortcutService(
           new TestCommandBus(new TestRegistrar()),
           new NullShellLog()))
{
    using var first = shortcuts.Register(
        new GlobalShortcutDescriptor("long", [new GlobalShortcutStroke(0x41), new GlobalShortcutStroke(0x42)], "vulcan.command.help"),
        "one");
    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("short", [new GlobalShortcutStroke(0x41)], "vulcan.command.help"),
            "two");
        throw new InvalidOperationException("Prefix conflict must be rejected.");
    }
    catch (InvalidOperationException)
    {
    }

    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("same", [new GlobalShortcutStroke(0x41), new GlobalShortcutStroke(0x42)], "vulcan.command.help"),
            "three");
        throw new InvalidOperationException("Exact conflict must be rejected.");
    }
    catch (InvalidOperationException)
    {
    }

    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("empty", [], "vulcan.command.help"),
            "test");
        throw new InvalidOperationException("Empty stroke sequence must be rejected.");
    }
    catch (ArgumentException)
    {
    }

    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("fast", [new GlobalShortcutStroke(1)], "vulcan.command.help", 99),
            "test");
        throw new InvalidOperationException("Interval below 100ms must be rejected.");
    }
    catch (ArgumentOutOfRangeException)
    {
    }
}

using (var shortcuts = new Mercury.Input.GlobalShortcutService(
           new TestCommandBus(new TestRegistrar()),
           new NullShellLog()))
{
    using (var owner = shortcuts.CreateOwnerRegistrar("module:test"))
    {
        owner.Register(new GlobalShortcutDescriptor("one", [new GlobalShortcutStroke(0x41)], "vulcan.command.help"));
        owner.Register(new GlobalShortcutDescriptor("two", [new GlobalShortcutStroke(0x42)], "vulcan.command.help"));
        Equal(2, shortcuts.Registrations.Count, "Owner registrar must keep both registrations.");
    }

    Equal(0, shortcuts.Registrations.Count, "Owner registrar dispose must release every registration.");

    // Mercury 自有的 focus-console 现在也走命令层注册，与第三方模块同一条通道。
    using (var owner = shortcuts.CreateOwnerRegistrar("HistoryMercury"))
    {
        owner.Register(new GlobalShortcutDescriptor(
            "focus-console",
            [new GlobalShortcutStroke(0xBF), new GlobalShortcutStroke(0xBF)],
            "vulcan.app.focusconsole"));
        var focusConsole = shortcuts.Registrations.Single();
        Equal("focus-console", focusConsole.Id, "Mercury shortcut registration id.");
        Equal("vulcan.app.focusconsole", focusConsole.CommandText, "Mercury shortcut command target.");
    }

    Equal(0, shortcuts.Registrations.Count,
        "Disposing the Mercury owner must release focus-console before reload.");
}

var matcher = new Mercury.Input.GlobalShortcutMatcher();
var slash = new GlobalShortcutRegistrationInfo(
    "slash",
    "test",
    [new GlobalShortcutStroke(0xBF), new GlobalShortcutStroke(0xBF)],
    "vulcan.command.help",
    350);
var start = System.Diagnostics.Stopwatch.GetTimestamp();
Equal(0, matcher.Process([slash], new GlobalShortcutStroke(0xBF), start).Count, "First slash must not fire.");
Equal(1, matcher.Process(
        [slash],
        new GlobalShortcutStroke(0xBF),
        start + (long)(System.Diagnostics.Stopwatch.Frequency * 0.2)).Count,
    "Second slash within interval must fire.");
Equal(0, matcher.Process([slash], new GlobalShortcutStroke(0xBF), start).Count, "Timeout restart first stroke.");
Equal(0, matcher.Process(
        [slash],
        new GlobalShortcutStroke(0xBF),
        start + (long)(System.Diagnostics.Stopwatch.Frequency * 0.5)).Count,
    "Timeout must discard incomplete sequence.");

// 模块身份：manifest 与 ModuleInfo 的版本必须逐字符相等。
// 宿主在两者不一致时会静默跳过整个模块（只在服务进程日志留一行 module.discovery 警告），
// 界面上表现为活动坞与扩展坞管理页一起消失——这条断言就是为了不再靠肉眼发现它。
var moduleInfo = new ModuleInfo();
var manifestPath = Path.Combine(
    AppContext.BaseDirectory,
    "..", "..", "..", "..", "..",
    "b-Code-MercuryDock", "module.manifest.json");
True(File.Exists(manifestPath), $"module.manifest.json must be locatable at {Path.GetFullPath(manifestPath)}.");
using (var manifestDoc = JsonDocument.Parse(File.ReadAllText(manifestPath)))
{
    var manifestRoot = manifestDoc.RootElement;
    Equal(
        manifestRoot.GetProperty("version").GetString(),
        moduleInfo.Version,
        "manifest version must equal ModuleInfo.Version (mismatch makes the host skip the module)");
    Equal(
        manifestRoot.GetProperty("name").GetString(),
        moduleInfo.ModuleName,
        "manifest name must equal ModuleInfo.ModuleName");
}

// 第三处版本在仓库根 project.manifest.json。csproj 的 Version 经程序集进 ModuleInfo，
// 所以这里再对上根清单，三处就锁在同一个字上。漏改根清单不会让宿主跳过模块，
// 但会让发布登记和源码各说一个版本。
var projectManifestPath = Path.GetFullPath(Path.Combine(
    Path.GetDirectoryName(manifestPath)!, "..", "project.manifest.json"));
True(File.Exists(projectManifestPath), $"project.manifest.json must be locatable at {projectManifestPath}.");
using (var projectDoc = JsonDocument.Parse(File.ReadAllText(projectManifestPath)))
{
    Equal(
        projectDoc.RootElement.GetProperty("project").GetProperty("version").GetString(),
        moduleInfo.Version,
        "project.manifest.json version must equal ModuleInfo.Version");
}

// 5.3.0：不再往 Aurora 注册页面或场景（用户定）。页面协议三条与只给管理页用的 dock.run 一起删掉；
// 任何一条留着，Aurora 都会照旧拉出一页「扩展坞管理」、建出 HistoryMercury 场景。
foreach (var name in new[] { "mercury.ui.describe", "mercury.ui.actions", "mercury.ui.data", "mercury.dock.run" })
    True(!registry.TryGet(name, out _), $"Mercury must not register Aurora page commands any more: {name}.");

// 坞上项目定死 12 个（加「主界面」与「+」两格共 14 格）：固定项在前，余下按权重补满；固定项超出时只取 12 个。
{
    static DockProject Project(int index, bool pinned, double weight = 0, bool excluded = false)
        => new($"2026-{index:000}-P{index}", $"2026-{index:000}", $"C:/x/{index}", DateTimeOffset.UnixEpoch, pinned, weight,
            Excluded: excluded);

    Equal(12, DockPolicy.ItemCount, "The dock shows exactly 12 projects.");
    var mixed = Enumerable.Range(1, 20).Select(index => Project(index, pinned: index <= 3, weight: index)).ToList();
    var visible = MercuryState.SelectVisible(mixed);
    Equal(12, visible.Count, "Twenty candidates fill exactly 12 slots.");
    True(visible.Take(3).All(item => item.Pinned), "Pinned projects come first.");
    Equal("2026-020-P20", visible[3].Name, "Unpinned slots are filled by weight.");

    var crowded = Enumerable.Range(1, 15).Select(index => Project(index, pinned: true)).ToList();
    Equal(12, MercuryState.SelectVisible(crowded).Count, "More than 12 pins still show only 12.");

    var withExcluded = Enumerable.Range(1, 13).Select(index => Project(index, false, excluded: index == 1)).ToList();
    True(MercuryState.SelectVisible(withExcluded).All(item => !item.Excluded), "Excluded projects never take a slot.");
    Equal(5, MercuryState.SelectVisible(mixed.Take(5).ToList()).Count, "A small library shows what it has.");

    // 「+」列表的搜索：空格切词、每个词都要命中、不分大小写；编号在目录名里，所以也能搜。
    var library = new[] { Project(21, false) with { Name = "2026-021-HistoryMercury" },
        Project(20, false) with { Name = "2026-020-HistoryJanus" } };
    Equal(2, ProjectPickerWindow.Filter(library, "  ").Count, "An empty query lists every project.");
    Equal("2026-021-HistoryMercury", ProjectPickerWindow.Filter(library, "")[0].Name, "Newest number first.");
    Equal(1, ProjectPickerWindow.Filter(library, "mercury 021").Count, "All terms must match.");
    Equal(0, ProjectPickerWindow.Filter(library, "mercury 020").Count, "A term that misses filters the row out.");
    Equal(1, ProjectPickerWindow.Filter(library, "JANUS").Count, "Search ignores case.");
}

// 坞与「+」列表的控件样式是运行时解析的 XAML 文本：写错一个字只会在真机上弹窗时炸。这里在 STA 线程上真解析一次，
// 并钉住 OneHistory 风格的两个关键值：选中 = 墨色块反白，搜索框聚焦 = 边线变墨色。
{
    Exception? styleError = null;
    var styleThread = new Thread(() =>
    {
        try
        {
            var styles = DockStyles.Create();
            True(styles.Contains(DockStyles.SearchBoxKey), "Dock styles must define the search box.");
            True(styles.Contains(DockStyles.ItemKey), "Dock styles must define the list item.");
            True(styles.Contains(typeof(System.Windows.Controls.Primitives.ScrollBar)), "Dock styles must restyle scroll bars.");
            var item = (System.Windows.Style)styles[DockStyles.ItemKey];
            var template = (System.Windows.Controls.ControlTemplate)item.Setters.OfType<System.Windows.Setter>()
                .Single(setter => setter.Property == System.Windows.Controls.Control.TemplateProperty).Value;
            var selected = template.Triggers.OfType<System.Windows.Trigger>()
                .Single(trigger => trigger.Property == System.Windows.Controls.ListBoxItem.IsSelectedProperty);
            var background = selected.Setters.OfType<System.Windows.Setter>()
                .Single(setter => setter.Property == System.Windows.Controls.Border.BackgroundProperty).Value;
            True(background is System.Windows.Media.SolidColorBrush ink && ink.Color == DockTheme.Ink.Color,
                "A selected row must be an ink block (OneHistoryID §2.4).");
        }
        catch (Exception ex)
        {
            styleError = ex;
        }
    });
    styleThread.SetApartmentState(ApartmentState.STA);
    styleThread.Start();
    styleThread.Join();
    True(styleError == null, "Dock styles must parse: " + styleError);
}

// 活动坞磁贴：底色随使用频率由纯白线性趋近柔和强调色，不再用光晕表达频率。
Equal(0d, DockWeight.TileTint(0, 10), "Unused project keeps a plain white tile.");
Equal(1d, DockWeight.TileTint(10, 10), "The most-used project reaches the fully tinted end.");
Equal(0.5d, DockWeight.TileTint(5, 10), "Tint is linear in the normalised weight.");
Equal(0d, DockWeight.TileTint(5, 0), "A zero maximum must not divide by zero.");

var coldTile = DockTheme.TileBackground(0);
var hotTile = DockTheme.TileBackground(1);
True(coldTile.Color == DockTheme.TileBase, "Zero-frequency tile is #FFFFFF.");
True(hotTile.Color == DockTheme.TileTintTarget, "Maximum-frequency tile is #FAF0D8.");
True(DockTheme.TileBackground(0.5).Color.B < coldTile.Color.B
     && DockTheme.TileBackground(0.5).Color.B > hotTile.Color.B,
    "Mid frequency sits between the two ends (blue channel drops as it yellows).");
True(DockTheme.TileText.Color == System.Windows.Media.Color.FromRgb(0xA8, 0x7A, 0x12),
    "Tile text uses the documented deep-yellow accent.");

// 代码面的说明书是随包的 HistoryMercury.xml，不是手写文档。
// 每个公开类型都必须有 summary——加一个类型不写注释，这条就失败。
AssertPublicTypeSummaries(assembly);

Console.WriteLine($"HistoryMercury.Smoke: PASS ({commands.Count} mercury commands, one direct registration source, immutable runtime package boundary, 12-slot dock with + picker, no Aurora pages, Explorer shortcut folder, global shortcuts, domain focus).");

/// <summary>
/// 随包 XML 必须给每个公开类型一句 summary。属性逐个补不强求，类型级说清楚它是什么、谁用它就够。
/// </summary>
static void AssertPublicTypeSummaries(Assembly assembly)
{
    var beside = Path.ChangeExtension(assembly.Location, ".xml");
    // ProjectReference 不把 XML 复制到 Smoke 输出。管线取的是模块自己的输出目录，就验那一份。
    var xmlPath = File.Exists(beside)
        ? beside
        : beside.Replace(
            $"{Path.DirectorySeparatorChar}b-Code-Tests{Path.DirectorySeparatorChar}HistoryMercury.Smoke{Path.DirectorySeparatorChar}",
            $"{Path.DirectorySeparatorChar}b-Code-MercuryDock{Path.DirectorySeparatorChar}",
            StringComparison.OrdinalIgnoreCase);
    True(File.Exists(xmlPath), "HistoryMercury.xml must be generated beside the module assembly: " + xmlPath);
    var documented = XDocument.Load(xmlPath)
        .Descendants("member")
        .Where(member => member.Element("summary") is { } summary && !string.IsNullOrWhiteSpace(summary.Value))
        .Select(member => (string?)member.Attribute("name"))
        .ToHashSet(StringComparer.Ordinal);
    var missing = assembly.GetExportedTypes()
        .Select(type => "T:" + type.FullName!.Replace('+', '.'))
        .Where(id => !documented.Contains(id))
        .OrderBy(id => id, StringComparer.Ordinal)
        .ToList();
    True(missing.Count == 0, "Public types missing summary: " + string.Join(", ", missing));
}

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

/// <summary>
/// 确认模块里没有任何编译后的 XAML。
/// </summary>
/// <remarks>
/// 描述化之后模块不再构造前端控件，因此不该有 <c>*.g.resources</c>。这条比 4.x 的
/// "BAML 里不含 HistoryAurora pack URI" 更强：那时只能防住本地合并样式字典这一种失败形态，
/// 现在连"模块自己画了一个控件"都编译不进来。
///
/// 桌面坞不受影响——它是代码构造的 WPF 窗口，属于本模块自己的界面，不经 XAML。
/// </remarks>
static void AssertNoCompiledXaml(System.Reflection.Assembly assembly)
{
    using var stream = assembly.GetManifestResourceStream("HistoryMercury.g.resources");
    True(stream == null,
        "The module must not ship compiled XAML: pages are described as data and rendered by HistoryAurora.");
}

file sealed class NullShellLog : HistoryVulcan.Core.Logging.IModuleLog
{
    public void Log(HistoryVulcan.Core.Logging.ShellLogLevel level, string category, string message) { }
}
