using System.Reflection;
using System.Text.Json;
using System.IO;
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

var registry = new CommandRegistry();
MercuryCommandCatalog.Register(registry);
var commands = registry.All();
// 31 = 4.8.1 的 26 条 + 页面协议三条（ui.describe / ui.actions / ui.data）+ dock.run + shortcut.pick。
Equal(31, commands.Count, "Command count");
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

// 配置值来自用户设置文件，非字符串值必须按缺失处理，不能让活动坞扫描路径抛异常。
using (var malformedSettings = JsonDocument.Parse("""
{"proj.libraryroot": 42, "proj.worktreeroot": true, "proj.other": null}
"""))
{
    Equal(null, MercuryState.ReadSetting(malformedSettings, "proj.libraryroot"),
        "Non-string library roots must be ignored.");
    Equal(null, MercuryState.ReadSetting(malformedSettings, "proj.worktreeroot"),
        "Non-string worktree roots must be ignored.");
    Equal(null, MercuryState.ReadSetting(malformedSettings, "proj.other"),
        "Null settings must be ignored.");
}

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
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HistoryVulcan", "HistoryMercury", DockShortcutFolder.FolderName),
    DockShortcutFolder.Path,
    "Shortcut folder must use the mutable module data root outside the package slot.");
True(
    !DockShortcutFolder.Path.StartsWith(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HistoryVulcan", "Modules") + Path.DirectorySeparatorChar,
        StringComparison.OrdinalIgnoreCase),
    "Shortcut files must never mutate the manifest-verified runtime package.");
Equal("HistoryClio 项目", ExplorerNamespaceRegistration.DisplayName, "Explorer entry name");
Equal(@"C:\OneHistory\HistoryClio", MercuryLibraryRoot.Default, "Default project library is HistoryClio");
Equal(MercuryLibraryRoot.Default, MercuryLibraryRoot.Coerce(MercuryLibraryRoot.LegacyVesta),
    "Configured HistoryVesta roots coerce to HistoryClio when that library exists.");
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
    var hotkeyRegistry = new CommandRegistry();
    MercuryCommandCatalog.Register(hotkeyRegistry);
    foreach (var name in new[] { "mercury.hotkey.register", "mercury.hotkey.unregister", "mercury.hotkey.list" })
        True(hotkeyRegistry.TryGet(name, out _), $"Hotkey command must be registered: {name}.");

    var registerCommand = hotkeyRegistry.All().Single(c => c.Name == "mercury.hotkey.register");
    foreach (var parameter in new[] { "id", "stroke", "command" })
        True(registerCommand.Parameters.Any(p => p.Name == parameter && p.Required),
            $"mercury.hotkey.register must require parameter {parameter}.");
}

using (var shortcuts = new Mercury.Input.GlobalShortcutService(
           new CommandBus(new CommandRegistry(), new NullShellLog()),
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
           new CommandBus(new CommandRegistry(), new NullShellLog()),
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
// 界面上表现为活动坞与命令集页一起消失——这条断言就是为了不再靠肉眼发现它。
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

// 页面注册协议 V1：描述、动作声明与取数三条命令是本模块唯一的前端接口。
// 命令目录里必须有它们，且一律不对远端暴露——5.0 之后远端可见性只看 HiddenReason，
// 宿主不再替模块判断这类"界面内部协议"。
foreach (var name in new[] { "mercury.ui.describe", "mercury.ui.actions", "mercury.ui.data" })
{
    True(registry.TryGet(name, out var page), $"Page-protocol command must be registered: {name}.");
    True(page.Readonly, $"{name} must be readonly.");
    True(!string.IsNullOrWhiteSpace(page.HiddenReason),
        $"{name} is an in-frontend protocol and must declare a HiddenReason.");
}
True(registry.TryGet("mercury.dock.run", out var dockRun)
     && !string.IsNullOrWhiteSpace(dockRun.HiddenReason),
    "mercury.dock.run relays a stored command text and must not be remotely visible.");

// 描述与声明必须是合法 JSON，且满足 Aurora 的校验口径：schemaVersion 与 owner 对得上、
// 页面 id 小写无空格、按钮绑的动作都在声明里。这三条 4.x 时代是编译器保证的，
// 换成文本协议之后没有编译器，只能自己验。

// 描述块与取数块共用：占位符与列名的对齐要等取数真的产出一行才查得了。
var rowActions = new List<string>();
{
    using var describe = JsonDocument.Parse(MercuryPages.DescribeJson());
    var root = describe.RootElement;
    Equal(1, root.GetProperty("schemaVersion").GetInt32(), "Page description schema version");
    Equal("HistoryMercury", root.GetProperty("owner").GetString(), "Page description owner");
    var pages = root.GetProperty("pages").EnumerateArray().ToList();
    True(pages.Count >= 1, "The module must describe at least one page.");
    var ids = pages.Select(page => page.GetProperty("id").GetString() ?? "").ToList();
    True(ids.Contains(MercuryPages.ManagerPageId), "The dock manager page must stay declared.");
    True(ids.All(id => id.Length > 0 && id == id.ToLowerInvariant() && !id.Contains(' ')),
        "Page ids must be lowercase without spaces or Aurora discards the whole description.");
    Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Page ids must be unique.");
    foreach (var page in pages)
    {
        True(!string.IsNullOrWhiteSpace(page.GetProperty("title").GetString()), "Every page needs a title.");
        True(page.TryGetProperty("content", out _), "Every page needs content.");
    }

    using var declared = JsonDocument.Parse(MercuryPages.ActionsJson());
    Equal(1, declared.RootElement.GetProperty("schemaVersion").GetInt32(), "Action schema version");
    Equal("HistoryMercury", declared.RootElement.GetProperty("owner").GetString(), "Action owner");
    var actions = declared.RootElement.GetProperty("actions").EnumerateArray().ToList();
    var actionIds = actions.Select(action => action.GetProperty("id").GetString() ?? "").ToList();
    True(actionIds.All(id => id.Length > 0 && id == id.ToLowerInvariant() && !id.Contains(' ')),
        "Action ids must be lowercase without spaces.");
    Equal(actionIds.Count, actionIds.Distinct(StringComparer.OrdinalIgnoreCase).Count(),
        "Action ids must be unique.");

    // 动作指向的每一条指令都必须真的注册着。"改了指令名、按钮静默变哑"正是动作声明要消灭的形态，
    // 而声明本身写错名字会得到一模一样的症状。
    foreach (var action in actions)
    {
        var command = action.GetProperty("command").GetString() ?? "";
        if (!command.StartsWith("mercury.", StringComparison.Ordinal))
            continue;
        True(registry.TryGet(command, out _), $"Declared action targets an unregistered command: {command}.");
    }

    // 页面里出现的每个动作 id 都必须已声明：Aurora 解析不到就把按钮渲染成显式占位。
    var used = new List<string>();
    CollectActions(root.GetProperty("pages"), used);
    foreach (var id in used.Distinct(StringComparer.OrdinalIgnoreCase))
        True(actionIds.Contains(id, StringComparer.OrdinalIgnoreCase), $"Page references an undeclared action: {id}.");
    True(used.Count > 0, "The manager page must bind at least one action.");

    // 描述必须跟着 Aurora 的规则走。下面三条各对应一次已经踩过的静默失效——
    // 三种都不会让页面崩，只会让页面**看起来还在，点下去没有反应**。

    // 一、退役的页面节点（Aurora 1.8.14）。button / input / select 只能出现在控制面板里，
    //     写在页面这一层会渲染成一块写着退役原因的牌子。5.0.0 的管理页在表格下面排了
    //     7 个 type:"button"，整排行操作因此全哑——而模块这边一个错误都没有。
    var nodeTypes = new List<string>();
    CollectNodeTypes(root.GetProperty("pages"), nodeTypes);
    foreach (var retired in new[] { "button", "input", "select" })
        True(!nodeTypes.Contains(retired, StringComparer.OrdinalIgnoreCase),
            $"Retired page node type still in the description: {retired}. "
            + "Small controls belong in a panel or popup (Aurora 1.8.14).");

    // 二、表格的 view 是空转声明（Aurora REQ-UI-054）。解析得了、全仓没人读它，
    //     留着只会让人以为这张表能筛能排。
    True(!DeclaresTableViewOptions(root.GetProperty("pages")),
        "Tables must not declare view: filterable / sortable / selection have no implementation "
        + "in Aurora and are accounted for as a debt (REQ-UI-054).");

    // 三、行操作必须真的声明出来。5.0.0 用的是表格下面一排页面按钮，1.8.14 之后那条路没了；
    //     这条钉住的是"别再换回去"。占位符与取数列的对齐在下面的取数块里查。
    CollectRowActions(root.GetProperty("pages"), rowActions);
    True(rowActions.Count > 0,
        "The dock manager table must declare rowActions: page-level buttons were retired in Aurora 1.8.14.");

    // 四、5.0.2 的版面取舍（REQ-MERC-502）。三样东西一起从管理页上去掉，各有一条门禁：
    //     Aurora 只在**有** inline 行操作时才建最右那个「操作」列（AuroraTable），
    //     所以"那一列还在不在"在描述这一侧就是"还有没有一条 inline 行操作"。
    var manager = pages.Single(page => page.GetProperty("id").GetString() == MercuryPages.ManagerPageId);

    //     三条查的都是"某样东西不在了"，因此每条先证明自己认得出那样东西——
    //     一个永远为真的门禁与没有门禁是一回事，而它还会让人以为这里有人看着。
    using (var probe = JsonDocument.Parse("""{"rowActions":[{"action":"a","title":"t"}]}"""))
        True(DeclaresInlineRowAction(probe.RootElement),
            "The inline-row-action gate must recognise an inline action (inline defaults to true when omitted).");
    True(DeclaresColumn(manager, "name"), "The column gate must find a column the manager table does declare.");

    True(!DeclaresInlineRowAction(manager),
        "Every row action must declare inline:false: the dock manager keeps no in-row button column (REQ-MERC-502).");
    var managerTypes = new List<string>();
    CollectNodeTypes(manager, managerTypes);
    True(managerTypes.Contains("table", StringComparer.OrdinalIgnoreCase),
        "The node-type gate must see the manager page's own nodes before it can prove a type is absent.");
    True(!managerTypes.Contains("text", StringComparer.OrdinalIgnoreCase),
        "The dock manager page must carry no caption text: the page does not scroll, and that line costs a table row "
        + "(REQ-MERC-502).");
    True(!DeclaresColumn(manager, "lastopened"),
        "The dock manager table must not show the lastopened column; the entries view still produces it for "
        + "row-action placeholders (REQ-MERC-502).");
}

// 取数：扩展坞条目视图的列必须与描述里声明的列对齐，否则表格会整列空着而不报错。
{
    True(MercuryState.AddCommand("mercury.proj.list", "取数烟测"), "Seeding a dock entry must succeed.");
    var rows = MercuryUiData.Entries();
    True(rows.Count > 0, "The entries view must return the seeded dock entry.");
    foreach (var column in new[] { "key", "name", "type", "state", "command", "weight", "clicks", "lastopened" })
        True(rows.All(row => row.ContainsKey(column)), $"Entry rows must carry the declared column: {column}.");

    // 行操作的占位符按**被点那一行的同名列**取值（Aurora REQ-UI-011）。对不上不会报错，
    // 只会把一个空值发上总线——症状是"点了一下，什么都没发生"，而两边都没有一条日志。
    var entryColumns = rows.SelectMany(row => row.Keys).ToHashSet(StringComparer.OrdinalIgnoreCase);
    using var declaredActions = JsonDocument.Parse(MercuryPages.ActionsJson());
    foreach (var id in rowActions.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        var action = declaredActions.RootElement.GetProperty("actions").EnumerateArray().Single(candidate =>
            string.Equals(candidate.GetProperty("id").GetString(), id, StringComparison.OrdinalIgnoreCase));
        if (!action.TryGetProperty("args", out var actionArgs) || actionArgs.ValueKind != JsonValueKind.Object)
            continue;
        foreach (var argument in actionArgs.EnumerateObject())
        {
            var value = argument.Value.GetString() ?? "";
            if (value.Length < 3 || value[0] != '{' || value[^1] != '}')
                continue;
            True(entryColumns.Contains(value[1..^1]),
                $"Row action {id} takes {value} from the clicked row, but the entries view has no such column.");
        }
    }
    var seeded = rows.Single(row => row["command"] == "mercury.proj.list");
    Equal("cmd:mercury.proj.list", seeded["key"], "Command entries use the cmd: row key.");

    var missing = MercuryUiData.RunEntryAsync("cmd:mercury.does.not.exist", null).GetAwaiter().GetResult();
    True(!missing.Success, "Running a key that is not in the dock must fail rather than execute it.");
    var malformed = MercuryUiData.RunEntryAsync("mercury.proj.list", null).GetAwaiter().GetResult();
    True(!malformed.Success, "Raw command text must not be accepted as a row key.");
    True(MercuryState.RemoveCommand("mercury.proj.list"), "Entry-view seed cleanup");
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

Console.WriteLine($"HistoryMercury.Smoke: PASS ({commands.Count} mercury commands, one direct registration source, immutable runtime package boundary, described pages, Explorer shortcut folder, global shortcuts, domain focus).");

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

/// <summary>递归收集页面描述里出现的全部节点 <c>type</c>。</summary>
static void CollectNodeTypes(JsonElement node, List<string> found)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("type") && property.Value.ValueKind == JsonValueKind.String)
                {
                    found.Add(property.Value.GetString() ?? "");
                    continue;
                }

                CollectNodeTypes(property.Value, found);
            }

            break;

        case JsonValueKind.Array:
            foreach (var item in node.EnumerateArray())
                CollectNodeTypes(item, found);
            break;
    }
}

/// <summary>递归收集 <c>rowActions</c> 里出现的动作 id。</summary>
static void CollectRowActions(JsonElement node, List<string> found)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("rowActions"))
                {
                    foreach (var entry in property.Value.EnumerateArray())
                        if (entry.TryGetProperty("action", out var id) && id.ValueKind == JsonValueKind.String)
                            found.Add(id.GetString() ?? "");
                    continue;
                }

                CollectRowActions(property.Value, found);
            }

            break;

        case JsonValueKind.Array:
            foreach (var item in node.EnumerateArray())
                CollectRowActions(item, found);
            break;
    }
}

/// <summary>描述里是否还有一条行操作要在行内放按钮。</summary>
/// <remarks>
/// Aurora 只在存在 inline 行操作时才建最右那个「操作」列，因此这一条查的就是「那一列还在不在」。
/// <c>inline</c> 不写等于 true（默认值在 Aurora 那一侧，不归本仓控制），所以缺省也算违规。
/// </remarks>
static bool DeclaresInlineRowAction(JsonElement node)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("rowActions"))
                {
                    foreach (var entry in property.Value.EnumerateArray())
                        if (!entry.TryGetProperty("inline", out var inline)
                            || inline.ValueKind != JsonValueKind.False)
                            return true;
                    continue;
                }

                if (DeclaresInlineRowAction(property.Value))
                    return true;
            }

            return false;

        case JsonValueKind.Array:
            foreach (var item in node.EnumerateArray())
                if (DeclaresInlineRowAction(item))
                    return true;
            return false;

        default:
            return false;
    }
}

/// <summary>描述里是否有表格列声明了这个取值键。取数产出该列不算——查的是"上不上表"。</summary>
static bool DeclaresColumn(JsonElement node, string key)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("columns"))
                {
                    foreach (var column in property.Value.EnumerateArray())
                        if (column.TryGetProperty("key", out var name)
                            && string.Equals(name.GetString(), key, StringComparison.OrdinalIgnoreCase))
                            return true;
                    continue;
                }

                if (DeclaresColumn(property.Value, key))
                    return true;
            }

            return false;

        case JsonValueKind.Array:
            foreach (var item in node.EnumerateArray())
                if (DeclaresColumn(item, key))
                    return true;
            return false;

        default:
            return false;
    }
}

/// <summary>
/// 描述里是否还有表格声明了 <c>view</c>（Aurora REQ-UI-054 的空转声明）。
/// </summary>
/// <remarks>
/// 只认**表格节点自己的** <c>view</c>。不能按属性名一路搜下去：取数参数里的
/// <c>dataSource.args.view</c> 是本模块自己的视图名（entries / commands），
/// 与它同名却毫无关系——按名搜会把一条正常声明报成违规，而误报的门禁最后一定被删掉。
/// </remarks>
static bool DeclaresTableViewOptions(JsonElement node)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            if (node.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "table", StringComparison.OrdinalIgnoreCase)
                && node.TryGetProperty("view", out _))
                return true;

            foreach (var property in node.EnumerateObject())
                if (DeclaresTableViewOptions(property.Value))
                    return true;

            return false;

        case JsonValueKind.Array:
            foreach (var item in node.EnumerateArray())
                if (DeclaresTableViewOptions(item))
                    return true;
            return false;

        default:
            return false;
    }
}

/// <summary>递归收集页面描述里出现的全部动作 id。</summary>
static void CollectActions(JsonElement node, List<string> found)
{
    switch (node.ValueKind)
    {
        case JsonValueKind.Object:
            foreach (var property in node.EnumerateObject())
            {
                if (property.NameEquals("action") && property.Value.ValueKind == JsonValueKind.String)
                {
                    found.Add(property.Value.GetString() ?? "");
                    continue;
                }

                CollectActions(property.Value, found);
            }

            break;

        case JsonValueKind.Array:
            foreach (var item in node.EnumerateArray())
                CollectActions(item, found);
            break;
    }
}

file sealed class NullShellLog : HistoryVulcan.Core.Logging.IShellLog
{
    public void Log(HistoryVulcan.Core.Logging.ShellLogLevel level, string category, string message) { }

    public event EventHandler<HistoryVulcan.Core.Logging.ShellLogEntry>? EntryAdded
    {
        add { }
        remove { }
    }

    public IReadOnlyList<HistoryVulcan.Core.Logging.ShellLogEntry> Snapshot() => [];
}
