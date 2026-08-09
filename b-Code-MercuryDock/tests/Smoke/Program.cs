using System.Reflection;
using System.Text.Json;
using System.IO;
using BaseVariable;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Docking;
using HistoryVulcan.Core.Input;
using HistoryVulcan.Core.Modules;
using Mercury;

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
Equal("4.1.0", moduleInfos[0].Version, "Module version");
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
    shellHosted.CreateUi();
    Equal(1, registrar.Registered.Count, "Repeated CreateUi must not register twice.");
    shellHosted.DestroyUi();
    Equal(1, registrar.Disposed, "DestroyUi must release the manager registration.");
}
finally
{
    Environment.SetEnvironmentVariable("MERCURY_DISABLE_EXPLORER_REGISTRATION", previousExplorerRegistrationSetting);
}

Equal("dock.manager", MercuryManagerView.CreateDescriptor().Id, "Manager window ID");
Equal(DockSide.Center, MercuryManagerView.CreateDescriptor().DefaultSide, "Manager window default side");

var registry = new CommandRegistry();
MercuryCommandCatalog.Register(registry);
var commands = registry.All();
Equal(17, commands.Count, "Command count");
True(commands.All(command => System.Text.RegularExpressions.Regex.IsMatch(command.Name, "^[a-z]+(?:\\.[a-z0-9]+)+$")),
    "Commands must be lowercase dot-separated identifiers.");
True(commands.All(command => command.Name.StartsWith("mercury.", StringComparison.Ordinal)),
    "All module commands must use the mercury domain.");
True(!commands.Any(command => command.Name.StartsWith("dock.", StringComparison.Ordinal)
    || command.Name.StartsWith("mercury.dock.open", StringComparison.Ordinal)),
    "Legacy command names must not remain registered.");
True(registry.TryGet("mercury.status", out var status) && status.Readonly, "Status command must be readonly.");
True(registry.TryGet("mercury.proj.pin", out var pin) && !pin.Readonly, "Project write command must be present.");
True(registry.TryGet("mercury.usage.forget", out var forget) && forget.IsDangerous,
    "Usage reset must require confirmation.");
True(registry.TryGet(MercuryCommandCatalog.ProjectOpenCommandName, out var openProject), "Project open command must be registered.");
Equal("mercury.proj.open 2026-021-HistoryMercury", MercuryCommandCatalog.BuildOpenProjectCommand("2026-021-HistoryMercury"),
    "Plain project name command text");
Equal("mercury.proj.open \"a b\"", MercuryCommandCatalog.BuildOpenProjectCommand("a b"),
    "Whitespace project name command text");

using var catalog = JsonDocument.Parse("""
[{"commandName":"mercury.proj.open","domain":"Mercury","commandClass":"proj","summary":"Open project"},
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

Equal("HistoryVulcan", MercuryPaths.HostName, "Host data root name");
Equal(
    Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HistoryVulcan", "Modules", "HistoryMercury", DockShortcutFolder.FolderName),
    DockShortcutFolder.Path,
    "Shortcut folder must use the module identity slot.");
Equal("HistoryVesta 项目", ExplorerNamespaceRegistration.DisplayName, "Explorer entry name");
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
    var now = DateTimeOffset.UtcNow;
    var first = new DockProject("2026-001-First", "2026-001", firstProject, now, false);
    var second = new DockProject("2026-002-Second", "2026-002", secondProject, now, false);
    var initial = DockShortcutFolder.Synchronize([first, second], shortcutRoot);
    Equal(2, initial.Written, "Each dock project must receive one shortcut.");
    var reconciled = DockShortcutFolder.Synchronize([second], shortcutRoot);
    Equal(1, reconciled.Removed, "Removed dock projects must lose their shortcuts.");
}
finally
{
    Directory.Delete(shortcutRoot, recursive: true);
}

True(typeof(IGlobalShortcutModule).IsAssignableFrom(typeof(MercuryShortcutModule)),
    "Shortcut module must implement IGlobalShortcutModule.");
True(typeof(IGlobalShortcutHost).IsAssignableFrom(typeof(Mercury.Input.GlobalShortcutService)),
    "GlobalShortcutService must implement IGlobalShortcutHost.");

using (var shortcuts = new Mercury.Input.GlobalShortcutService(
           new CommandBus(new CommandRegistry(), new NullShellLog()),
           new NullShellLog()))
{
    using var first = shortcuts.Register(
        new GlobalShortcutDescriptor("long", [new GlobalShortcutStroke(0x41), new GlobalShortcutStroke(0x42)], "vulcan.core.help"),
        "one");
    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("short", [new GlobalShortcutStroke(0x41)], "vulcan.core.help"),
            "two");
        throw new InvalidOperationException("Prefix conflict must be rejected.");
    }
    catch (InvalidOperationException)
    {
    }

    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("same", [new GlobalShortcutStroke(0x41), new GlobalShortcutStroke(0x42)], "vulcan.core.help"),
            "three");
        throw new InvalidOperationException("Exact conflict must be rejected.");
    }
    catch (InvalidOperationException)
    {
    }

    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("empty", [], "vulcan.core.help"),
            "test");
        throw new InvalidOperationException("Empty stroke sequence must be rejected.");
    }
    catch (ArgumentException)
    {
    }

    try
    {
        shortcuts.Register(
            new GlobalShortcutDescriptor("fast", [new GlobalShortcutStroke(1)], "vulcan.core.help", 99),
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
        owner.Register(new GlobalShortcutDescriptor("one", [new GlobalShortcutStroke(0x41)], "vulcan.core.help"));
        owner.Register(new GlobalShortcutDescriptor("two", [new GlobalShortcutStroke(0x42)], "vulcan.core.help"));
        Equal(2, shortcuts.Registrations.Count, "Owner registrar must keep both registrations.");
    }

    Equal(0, shortcuts.Registrations.Count, "Owner registrar dispose must release every registration.");
}

var matcher = new Mercury.Input.GlobalShortcutMatcher();
var slash = new GlobalShortcutRegistrationInfo(
    "slash",
    "test",
    [new GlobalShortcutStroke(0xBF), new GlobalShortcutStroke(0xBF)],
    "vulcan.core.help",
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

Console.WriteLine("HistoryMercury.Smoke: PASS (17 mercury commands, one direct registration source, HistoryMercury identity slot, shell UI, Explorer shortcut folder, global shortcuts).");

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
