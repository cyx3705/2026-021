using System.Reflection;
using System.Text.Json;
using System.IO;
using BaseVariable;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Modules;
using Mercury;
using Mercury.CommandSurface;
using Mercury.Input;

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

var uiTypes = assembly.GetTypes()
    .Where(type => type.IsPublic && !type.IsAbstract && typeof(IUiModule).IsAssignableFrom(type))
    .ToList();
Equal(1, uiTypes.Count, "Exactly one UI lifecycle module is required.");
Equal(typeof(MercuryUiModule), uiTypes[0], "UI lifecycle type");
True(typeof(IShellUiAware).IsAssignableFrom(typeof(MercuryUiModule)), "UI module must accept shell UI registration.");
True(typeof(IModuleContextAware).IsAssignableFrom(typeof(MercuryUiModule)), "UI module must receive the host command context.");

var registrar = new RecordingRegistrar();
var shellHosted = new MercuryUiModule();
try
{
    ((IShellUiAware)shellHosted).ShellUi = registrar;
    shellHosted.CreateUi();
    Equal(1, registrar.Registered.Count, "The manager page must register once.");
    Equal("dock.manager", registrar.Registered[0], "Manager page ID");
    True(MercuryState.IsWatching, "CreateUi must start the state watcher.");
    shellHosted.CreateUi();
    Equal(1, registrar.Registered.Count, "Repeated CreateUi must not register twice.");
    shellHosted.DestroyUi();
    Equal(1, registrar.Disposed, "DestroyUi must release the manager registration.");
    True(!MercuryState.IsWatching, "DestroyUi must release the state watcher.");
    shellHosted.CreateUi();
    True(MercuryState.IsWatching, "The state watcher must restart after a UI reload.");
    shellHosted.DestroyUi();
    Equal(2, registrar.Disposed, "A reloaded UI must release its manager registration again.");
}
finally
{
    Environment.SetEnvironmentVariable("MERCURY_DISABLE_EXPLORER_REGISTRATION", previousExplorerRegistrationSetting);
}

Equal("dock.manager", MercuryManagerView.CreateDescriptor().Id, "Manager window ID");
Equal("Mercury", ProjectIconGenerator.ShortLabel("2026-021-HistoryMercury"),
    "Project tile short label removes number and History prefix.");
True(ProjectIconGenerator.FitFontSize("A very long project suffix", 44) >= 6,
    "Project tile font remains a complete, non-ellipsis rendering path.");
Equal(DockSide.Center, MercuryManagerView.CreateDescriptor().DefaultSide, "Manager window default side");

var registry = new CommandRegistry();
MercuryCommandCatalog.Register(registry);
var commands = registry.All();
// 26 = 原 24 条 + mercury.dock.add/remove。
Equal(26, commands.Count, "Command count");
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
True(go.RequiresUiThread, "mercury.go must execute on the UI thread.");
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
True(registry.TryGet("mercury.usage.forget", out var forget) && forget.IsDangerous,
    "Usage reset must require confirmation.");
True(registry.TryGet(MercuryCommandCatalog.ProjectOpenCommandName, out var openProject), "Project open command must be registered.");
Equal("mercury.proj.open 2026-021-HistoryMercury", MercuryCommandCatalog.BuildOpenProjectCommand("2026-021-HistoryMercury"),
    "Plain project name command text");
Equal("mercury.proj.open \"a b\"", MercuryCommandCatalog.BuildOpenProjectCommand("a b"),
    "Whitespace project name command text");

using var catalog = JsonDocument.Parse("""
[{"commandName":"mercury.proj.open","domain":"Mercury","commandClass":"proj","summary":"打开项目"},
 {"commandName":"mercury.usage.list","domain":"Mercury","commandClass":"usage"},
 {"other":1}]
""");
var catalogItems = MercuryCommandCatalog.ParseCommandCatalog(catalog.RootElement);
Equal(2, catalogItems.Count, "Command catalog JSON parsing");
Equal("Mercury", catalogItems[0].Domain, "Command catalog domain");
True(MercuryCommandCatalog.FallbackCommandCatalog().Select(item => item.Name)
    .OrderBy(name => name, StringComparer.Ordinal)
    .SequenceEqual(commands.Select(command => command.Name).OrderBy(name => name, StringComparer.Ordinal)),
    "Fallback catalog must come from the registration source.");

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

var tree = CommandOptionTree.Build(MercuryCommandCatalog.FallbackCommandCatalog());
var roots = tree.ChildrenOf("");
Equal(1, roots.Count, "Only mercury may appear as a module root.");
Equal("mercury.", roots[0].Text, "Root branch");
var projectBranch = tree.ChildrenOf("mercury.proj.");
True(projectBranch.Any(option => option.Text == "mercury.proj.open"), "Project open leaf");
True(projectBranch.Any(option => option.Text == "mercury.proj.pin"), "Project pin leaf");

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

// 分段补全：域 → 类 → 方法 → 参数。域清单从传入定义现算，不硬编码。
var completion = new CommandCompletionEngine();
var catalogue = new List<CommandCompletionDefinition>
{
    new("janus.proj.list", "列出项目", []),
    new("janus.proj.commit", "提交", [new ParameterSpec { Name = "name", Description = "项目名" }]),
    new("janus.gitrule.scan", "扫描规则", []),
    new("mercury.go", "域聚焦", [new ParameterSpec { Name = "domain", Description = "域", Position = 0 }],
        DynamicValueProviders: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["domain"] = "registry.domains",
        }),
    new("vulcan.ui.reset", "重置布局", []),
};

static string[] Inserts(HistoryVulcan.Extensibility.CommandSurface.ConsoleCompletionResult result)
    => result.Candidates.Select(candidate => candidate.InsertText).ToArray();

// 第一段选域，落点带点号。
var domains = completion.Complete("", 0, catalogue);
True(Inserts(domains).SequenceEqual(["janus.", "mercury.", "vulcan."]),
    "Empty input offers every registered domain.");
True(domains.Candidates.All(candidate => candidate.Kind == HistoryVulcan.Extensibility.CommandSurface.ConsoleCompletionKind.Domain),
    "Domain stage yields domain candidates.");
True(Inserts(completion.Complete("erc", 3, catalogue)).SequenceEqual(["mercury."]),
    "Domain filtering supports case-insensitive contains matching.");

// 第二段选类，同时给出该域的无类直接方法。
var janusClasses = completion.Complete("janus.", 6, catalogue);
True(Inserts(janusClasses).SequenceEqual(["janus.gitrule.", "janus.proj."]),
    "Second stage offers the classes of the typed domain.");
True(Inserts(completion.Complete("janus.rule", 10, catalogue)).SequenceEqual(["janus.gitrule."]),
    "Class filtering supports contains matching.");
var mercuryStage = completion.Complete("mercury.", 8, catalogue);
True(Inserts(mercuryStage).Contains("mercury.go "),
    "A domain's classless direct methods appear at the class stage with a trailing space.");

// 第三段选方法，落点带尾随空格以直接进入参数段。
var methods = completion.Complete("janus.proj.", 11, catalogue);
True(Inserts(methods).SequenceEqual(["janus.proj.commit ", "janus.proj.list "]),
    "Third stage offers methods and lands on the parameter stage.");

// 参数候选来自注册的 ParameterSpec，不是猜的。
var parameters = completion.Complete("janus.proj.commit ", 18, catalogue);
True(Inserts(parameters).SequenceEqual(["name="]),
    "Parameter candidates come from the registered ParameterSpec.");

var positionalCatalogue = new List<CommandCompletionDefinition>
{
    new("janus.app.mode", "模式", [new ParameterSpec
    {
        Name = "mode",
        Description = "模式",
        Position = 0,
        AllowedValues = ["alpha", "beta"],
    }]),
};
var positional = completion.Complete("janus.app.mode ", 16, positionalCatalogue);
True(Inserts(positional).SequenceEqual(["alpha", "beta"]),
    "Position=0 parameters use bare values without name=.");
var projectDescriptors = MercuryCommandCatalog.CreateDescriptors()
    .Where(command => command.Name.StartsWith("mercury.proj.", StringComparison.Ordinal)
                      && command.Parameters.Any(parameter => parameter.Name == "name"))
    .ToList();
True(projectDescriptors.Count > 0
     && projectDescriptors.All(command =>
         command.Annotations.TryGetValue("completion.values.name", out var provider)
         && provider == "mercury.projects"),
    "Mercury project commands declare their common values through the mercury.projects provider.");
var projectDefinition = CommandCatalogSession.CreateCompletionDefinition(openProject);
True(projectDefinition.DynamicValues?.TryGetValue("name", out var projectValues) == true
     && projectValues.SequenceEqual(MercuryState.ListWorktreeProjects()),
    "The catalog session resolves mercury.projects into runtime worktree values.");
var goDomains = completion.Complete("mercury.go ", 11, catalogue);
True(Inserts(goDomains).SequenceEqual(["janus", "mercury", "vulcan"]),
    "mercury.go dynamically offers registered domains as bare positional values.");
var undeclaredDynamicValues = completion.Complete(
    "sample.go ",
    10,
    [new CommandCompletionDefinition("sample.go", "聚焦", [new ParameterSpec
    {
        Name = "domain",
        Description = "域",
        Position = 0,
    }])]);
True(undeclaredDynamicValues.Candidates.Count == 1
     && undeclaredDynamicValues.Candidates[0].InsertText.Length == 0,
    "Dynamic domains must not be inferred from a command name without provider metadata.");
var freeTextPositional = completion.Complete(
    "janus.proj.open ",
    16,
    [
        new CommandCompletionDefinition("janus.proj.open", "打开项目", [new ParameterSpec
        {
            Name = "project",
            Description = "项目",
            Position = 0,
        }]),
    ]);
True(freeTextPositional.Candidates.Count == 1
     && freeTextPositional.Candidates[0].DisplayText == "project"
     && freeTextPositional.Candidates[0].InsertText.Length == 0
     && freeTextPositional.Candidates[0].Kind == HistoryVulcan.Extensibility.CommandSurface.ConsoleCompletionKind.Parameter,
    "Free-form Position=0 parameters expose only a structure candidate and never insert name=.");

// 域聚焦：省略域前缀直接补类，同时其他域的绝对名仍然补得出来（脱固入口不能消失）。
var focused = completion.Complete("", 0, catalogue, "janus");
True(Inserts(focused).Take(2).SequenceEqual(["gitrule.", "proj."]),
    "Focused domain classes come first and drop the domain prefix.");
True(Inserts(focused).Contains("mercury."),
    "Other registered domains stay reachable while focused, so focus can always be left.");

var focusedMethods = completion.Complete("proj.", 5, catalogue, "janus");
True(Inserts(focusedMethods).SequenceEqual(["proj.commit ", "proj.list "]),
    "Focused class input advances to methods without duplicating the domain prefix.");

// 聚焦时省略域前缀后，参数段仍要能解析到正确的命令。
var focusedParameters = completion.Complete("proj.commit ", 12, catalogue, "janus");
True(Inserts(focusedParameters).SequenceEqual(["name="]),
    "Focused input resolves to the full command before reading its parameters.");
Equal("mercury.go", CommandCompletionEngine.ResolveAgainstFocus("go", catalogue, "mercury"),
    "Focused shorthand resolves to its full command before detail lookup.");

// 远程目录回归：Mercury 聚焦状态下输入 go + 空格，详情查询必须是 mercury.go，
// 不能请求不存在的 name=go；返回的目录注解继续驱动动态域候选。
var remoteRows = new List<HistoryVulcan.Services.Mcp.CommandCatalogRow>
{
    CatalogRow("mercury.go", "mercury", "域聚焦", ""),
    CatalogRow("vulcan.app.show", "vulcan", "显示前端", "app"),
};
var remoteCalls = new List<string>();
var remoteBus = new CommandBus(new CommandRegistry(), new NullShellLog())
{
    RemoteExecutor = (text, _, _) =>
    {
        remoteCalls.Add(text);
        if (text == "vulcan.command.list")
            return Task.FromResult(CommandResult.Ok(data: remoteRows));
        if (text == "vulcan.command.domains")
        {
            IReadOnlyList<HistoryVulcan.Services.Mcp.CommandDomainInfo> remoteDomains =
            [
                new("mercury", 1),
                new("vulcan", 1),
            ];
            return Task.FromResult(CommandResult.Ok(data: remoteDomains));
        }
        if (text == "vulcan.command.show name=mercury.go")
        {
            var detail = new HistoryVulcan.Services.Mcp.CommandCatalogDetail(
                remoteRows[0],
                [new HistoryVulcan.Services.Mcp.CommandParameterInfo(
                    "domain", "string", false, null, 0, [], "域")],
                null)
            {
                Annotations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["completion.values.domain"] = "registry.domains",
                },
            };
            return Task.FromResult(CommandResult.Ok(data: detail));
        }
        return Task.FromResult(CommandResult.Fail("unexpected: " + text));
    },
};
using (var remoteSession = new CommandCatalogSession(remoteBus, new CommandSelectionState()))
{
    True(remoteSession.RefreshAsync().GetAwaiter().GetResult(), "Remote catalog snapshot must load.");
    True(remoteSession.TrySetDomain("mercury", out _), "Remote Mercury focus must be accepted.");
    var remoteGo = remoteSession.CompleteAsync("go ", 3).GetAwaiter().GetResult();
    True(Inserts(remoteGo).SequenceEqual(["mercury", "vulcan"]),
        "Remote command annotations drive mercury.go domain values.");
}
True(remoteCalls.Contains("vulcan.command.show name=mercury.go"),
    "Focused go detail lookup must resolve to mercury.go.");
True(!remoteCalls.Any(call => call.Contains("name=go", StringComparison.Ordinal)),
    "Focused go completion must never request a nonexistent short command.");

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

Console.WriteLine($"HistoryMercury.Smoke: PASS ({commands.Count} mercury commands, one direct registration source, immutable runtime package boundary, shell UI, Explorer shortcut folder, global shortcuts, domain focus).");

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

static HistoryVulcan.Services.Mcp.CommandCatalogRow CatalogRow(
    string name,
    string domain,
    string summary,
    string commandClass)
    => new(
        name,
        domain,
        summary,
        null,
        0,
        "test",
        null,
        false,
        false,
        null,
        "hidden",
        false,
        false,
        null,
        0,
        0,
        null)
    {
        CommandClass = commandClass,
        Method = name[(name.LastIndexOf('.') + 1)..],
    };

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

file sealed class RecordingRegistrar : IShellUiRegistrar
{
    public List<string> Registered { get; } = [];
    public int Disposed { get; private set; }
    public bool IsUiThread => true;
    public void Invoke(Action action) => action();
    public IDisposable RegisterToolWindow(ToolWindowDescriptor descriptor, string owner)
    {
        Registered.Add(descriptor.Id);
        if (!string.Equals("HistoryMercury", owner, StringComparison.Ordinal))
            throw new InvalidOperationException($"UI owner: expected=HistoryMercury, actual={owner}");
        return new Handle(() => Disposed++);
    }

    public void UnregisterToolWindow(string id)
    {
    }

    public void UnregisterOwner(string owner)
    {
    }

    private sealed class Handle(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
