using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mercury;

public sealed record DockProject(
    string Name,
    string Number,
    string Path,
    DateTimeOffset LastActivity,
    bool Pinned,
    double Weight = 0,
    double Clicks = 0,
    DateTimeOffset? LastOpened = null,
    bool Excluded = false);

/// <summary>手动加入扩展坞的总线指令项：常驻显示，点击即经指令总线执行 Command。</summary>
public sealed record DockCommandEntry(string Command, string Label, DateTimeOffset Added);

internal static partial class MercuryState
{
    private static readonly object Gate = new();
    private static FileSystemWatcher? _watcher;
    /// <summary>
    /// 状态目录可用 MERCURY_STATE_DIRECTORY 整体改向（烟测隔离用），此时旧版迁移自动跳过。
    /// </summary>
    private static readonly string DockRoot =
        Environment.GetEnvironmentVariable("MERCURY_STATE_DIRECTORY") is { Length: > 0 } overrideRoot
            ? overrideRoot
            : MercuryPaths.DataRoot;
    private static readonly string PreviousMercuryDockRoot = MercuryPaths.PreviousDataRoot;
    private static readonly string LegacyMercuryDockRoot = MercuryPaths.LegacyMercuryDockDataRoot;
    private static readonly string LegacyActiveDockRoot = MercuryPaths.LegacyActiveDockDataRoot;
    private static readonly string StatePath = Path.Combine(DockRoot, "state.json");
    private static readonly string SettingsPath = MercuryPaths.SettingsPath;
    private static readonly string LegacySettingsPath = MercuryPaths.LegacySettingsPath;

    /// <summary>项目库缺省根；配置值失效（如整库改名后）时回退到这里。</summary>
    private const string DefaultWorktreeRoot = @"C:\OneHistory\HistoryVesta";

    private static DockPreferences _preferences = LoadPreferences();
    private static IReadOnlyList<DockProject> _projects = [];
    private static IReadOnlyList<DockProject> _allProjects = [];

    public static event Action? Changed;

    /// <summary>桌面坞显示的收录结果：固定项加按策略截取的加权项，不含被排除项。</summary>
    public static IReadOnlyList<DockProject> Projects
    {
        get
        {
            lock (Gate)
                return _projects.ToList();
        }
    }

    /// <summary>管理页面使用的完整扫描结果：含被排除项，排除行沉底。</summary>
    public static IReadOnlyList<DockProject> AllProjects
    {
        get
        {
            lock (Gate)
                return _allProjects.ToList();
        }
    }

    public static bool Hidden
    {
        get
        {
            lock (Gate)
                return _preferences.Hidden;
        }
    }

    /// <summary>用户调整后的尺寸；缺省回退到默认值。位置不再持久化，窗口恒定吸附右下角。</summary>
    public static (double Width, double Height) Size
    {
        get
        {
            lock (Gate)
            {
                return (
                    DockLayout.ClampWidth(_preferences.Width ?? DockLayout.DefaultWidth),
                    DockLayout.ClampHeight(_preferences.Height ?? DockLayout.DefaultHeight));
            }
        }
    }

    public static DockPolicy Policy
    {
        get
        {
            lock (Gate)
                return _preferences.Policy.Normalized();
        }
    }

    internal static string WorktreeRoot => ReadWorktreeRoot();

    /// <summary>
    /// 监视 state.json。活动坞在服务进程、管理页面在桌面 Shell 进程，两者不共享内存，
    /// 靠这个文件完成跨进程同步：任一侧写入后，另一侧重载偏好并刷新界面。
    /// </summary>
    public static void StartWatching()
    {
        lock (Gate)
        {
            if (_watcher != null)
                return;
            try
            {
                Directory.CreateDirectory(DockRoot);
                _watcher = new FileSystemWatcher(DockRoot, "state.json")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                    EnableRaisingEvents = true,
                };
                _watcher.Changed += (_, _) => Reload();
                _watcher.Created += (_, _) => Reload();
            }
            catch (Exception)
            {
                _watcher = null;
            }
        }
    }

    /// <summary>重新读入偏好并通知界面。写入方自己触发的事件同样走这里，重载幂等，不会形成回环。</summary>
    private static void Reload()
    {
        try
        {
            var loaded = LoadPreferences();
            lock (Gate)
                _preferences = loaded;
        }
        catch (Exception)
        {
            return;
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    /// <summary>记一次通过活动坞的打开：先衰减到当前时刻，再累加一次点击。</summary>
    public static void RecordOpen(string name)
    {
        lock (Gate)
        {
            var halfLife = _preferences.Policy.Normalized().HalfLifeDays;
            var now = DateTimeOffset.UtcNow;
            if (!_preferences.Usage.TryGetValue(name, out var entry))
                entry = new UsageEntry();
            entry.Score = DockWeight.Accumulate(
                entry.Score, entry.Updated, now, DockWeight.ClickWeight, halfLife);
            entry.Updated = now;
            _preferences.Usage[name] = entry;
            SavePreferences();
        }
    }

    /// <summary>手动排除或恢复收录。</summary>
    public static void Exclude(string name, bool excluded)
    {
        lock (Gate)
        {
            if (excluded)
                _preferences.Excluded.Add(name);
            else
                _preferences.Excluded.Remove(name);
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    /// <summary>清除使用记录；name 为空表示全部。</summary>
    public static void Forget(string? name)
    {
        lock (Gate)
        {
            if (string.IsNullOrWhiteSpace(name))
                _preferences.Usage.Clear();
            else
                _preferences.Usage.Remove(name);
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    public static void SetPolicy(int? minItems, int? maxItems, double? halfLifeDays)
    {
        lock (Gate)
        {
            var policy = _preferences.Policy;
            policy.MinItems = minItems ?? policy.MinItems;
            policy.MaxItems = maxItems ?? policy.MaxItems;
            policy.HalfLifeDays = halfLifeDays ?? policy.HalfLifeDays;
            _preferences.Policy = policy.Normalized();
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
    }

    public static async Task<IReadOnlyList<DockProject>> RefreshAsync()
    {
        var scan = await Task.Run(ScanProjects).ConfigureAwait(false);
        lock (Gate)
        {
            _projects = scan.Selected;
            _allProjects = scan.All;
        }
        if (!DockShortcutFolder.IsExplorerRegistrationDisabled)
        {
            DockShortcutFolder.Synchronize(scan.Selected);
            _ = ExplorerNamespaceRegistration.RegisterOrUpdate(DockShortcutFolder.Path);
        }
        Changed?.Invoke();
        return scan.Selected;
    }

    internal static void ApplyShortcutFolderChanges(DockShortcutFolder.ShortcutFolderDelta changes)
    {
        var preferencesChanged = false;
        lock (Gate)
        {
            foreach (var target in changes.RemovedTargets)
            {
                if (!TryGetWorktreeProjectName(target, out var name))
                    continue;
                preferencesChanged |= _preferences.Excluded.Add(name);
                preferencesChanged |= _preferences.Pins.Remove(name);
            }

            foreach (var target in changes.AddedTargets)
            {
                if (!TryGetWorktreeProjectName(target, out var name))
                    continue;
                preferencesChanged |= _preferences.Excluded.Remove(name);
                preferencesChanged |= _preferences.Pins.Add(name);
            }

            if (preferencesChanged)
                SavePreferences();
        }

        // Invalid, stale, or renamed links must also be reconciled away even when they
        // did not produce a preference change.
        _ = RefreshAsync();
    }

    public static bool Pin(string name, bool pinned)
    {
        lock (Gate)
        {
            var project = _allProjects.FirstOrDefault(item =>
                item.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
                || item.Number.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (project == null)
                return false;
            if (pinned)
                _preferences.Pins.Add(project.Name);
            else
                _preferences.Pins.Remove(project.Name);
            SavePreferences();
            ApplyPinState(project.Name);
        }

        if (!DockShortcutFolder.IsExplorerRegistrationDisabled)
            DockShortcutFolder.Synchronize(Projects);
        Changed?.Invoke();
        return true;
    }

    /// <summary>
    /// 手动把项目加入扩展坞：解除排除并固定。接受工作树根下真实存在的项目名或编号。
    /// </summary>
    public static bool AddToDock(string name)
    {
        if (!TryResolveWorktreeProject(name, out var actual, out _))
            return false;

        lock (Gate)
        {
            _preferences.Excluded.Remove(actual);
            _preferences.Pins.Add(actual);
            SavePreferences();
        }

        Changed?.Invoke();
        _ = RefreshAsync();
        return true;
    }

    /// <summary>按名称或编号在工作树根解析项目目录；编号匹配取目录名中的数字段。</summary>
    internal static bool TryResolveWorktreeProject(string nameOrNumber, out string name, out string path)
    {
        name = string.Empty;
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(nameOrNumber))
            return false;

        try
        {
            var wanted = nameOrNumber.Trim();
            var root = ReadWorktreeRoot();
            if (!Directory.Exists(root))
                return false;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                var dirName = Path.GetFileName(directory);
                if (string.IsNullOrEmpty(dirName) || !ProjectNumber().IsMatch(dirName))
                    continue;
                if (dirName.Equals(wanted, StringComparison.OrdinalIgnoreCase)
                    || ProjectNumber().Match(dirName).Value.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    name = dirName;
                    path = directory;
                    return true;
                }
            }
        }
        catch (Exception)
        {
        }

        return false;
    }

    /// <summary>列出工作树根下全部编号合规的项目目录名，供"加入扩展坞"候选。</summary>
    public static IReadOnlyList<string> ListWorktreeProjects()
    {
        try
        {
            var root = ReadWorktreeRoot();
            if (!Directory.Exists(root))
                return [];
            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(dir => dir != null && ProjectNumber().IsMatch(dir))
                .OrderBy(dir => dir, StringComparer.OrdinalIgnoreCase)
                .ToList()!;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>手动加入的指令项，按加入时间升序。常驻显示，不参与权重与策略排序。</summary>
    public static IReadOnlyList<DockCommandEntry> CommandEntries
    {
        get
        {
            lock (Gate)
                return _preferences.Commands.ToList();
        }
    }

    /// <summary>
    /// 把一条总线指令加入扩展坞：折叠多余空白后按指令文本去重，重复加入视为成功。
    /// </summary>
    public static bool AddCommand(string command)
    {
        var normalized = NormalizeCommand(command);
        if (normalized.Length == 0)
            return false;

        lock (Gate)
        {
            if (_preferences.Commands.Any(entry =>
                    entry.Command.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            _preferences.Commands.Add(new DockCommandEntry(normalized, normalized, DateTimeOffset.UtcNow));
            SavePreferences();
        }

        Changed?.Invoke();
        return true;
    }

    /// <summary>按指令文本移除指令项；不存在时返回 false。</summary>
    public static bool RemoveCommand(string command)
    {
        var normalized = NormalizeCommand(command);
        var removed = false;
        lock (Gate)
        {
            removed = _preferences.Commands.RemoveAll(entry =>
                entry.Command.Equals(normalized, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                SavePreferences();
        }

        if (removed)
            Changed?.Invoke();
        return removed;
    }

    /// <summary>折叠连续空白，保证同一指令的不同写法只存一份。</summary>
    internal static string NormalizeCommand(string? command)
        => string.IsNullOrWhiteSpace(command)
            ? string.Empty
            : string.Join(' ', command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static void SetHidden(bool hidden)
    {
        lock (Gate)
        {
            _preferences.Hidden = hidden;
            SavePreferences();
        }
        Changed?.Invoke();
    }

    public static void SaveSize(double width, double height)
    {
        lock (Gate)
        {
            _preferences.Width = DockLayout.ClampWidth(width);
            _preferences.Height = DockLayout.ClampHeight(height);
            SavePreferences();
        }
    }

    /// <summary>调用方须已持有 <see cref="Gate"/>。把 Pin 结果同步进两个缓存列表并保持既有排序。</summary>
    private static void ApplyPinState(string changedName)
    {
        _projects = OrderSelected(_projects
            .Select(item => item with { Pinned = _preferences.Pins.Contains(item.Name) })
            .ToList());
        _allProjects = OrderAll(_allProjects
            .Select(item => item with { Pinned = _preferences.Pins.Contains(item.Name) })
            .ToList());
    }

    private static List<DockProject> OrderSelected(IEnumerable<DockProject> projects)
        => projects
            .OrderByDescending(item => item.Pinned)
            .ThenByDescending(item => item.Weight)
            .ThenByDescending(item => item.LastActivity)
            .ToList();

    private static List<DockProject> OrderAll(IEnumerable<DockProject> projects)
        => projects
            .OrderBy(item => item.Excluded)
            .ThenByDescending(item => item.Pinned)
            .ThenByDescending(item => item.Weight)
            .ThenByDescending(item => item.LastActivity)
            .ToList();

    private static ScanResult ScanProjects()
    {
        var root = ReadWorktreeRoot();
        if (!Directory.Exists(root))
            return new ScanResult([], []);

        HashSet<string> pins;
        HashSet<string> excluded;
        Dictionary<string, UsageEntry> usage;
        DockPolicy policy;
        lock (Gate)
        {
            pins = new HashSet<string>(_preferences.Pins, StringComparer.OrdinalIgnoreCase);
            excluded = new HashSet<string>(_preferences.Excluded, StringComparer.OrdinalIgnoreCase);
            usage = new Dictionary<string, UsageEntry>(_preferences.Usage, StringComparer.OrdinalIgnoreCase);
            policy = _preferences.Policy.Normalized();
        }

        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (ProjectNumber().IsMatch(name))
                directories[name] = directory;
        }

        // 只统计项目主文件夹；解析失败时该信号整体缺席，不影响其余排序。
        IReadOnlyDictionary<string, DateTimeOffset> explorerOpens;
        try
        {
            explorerOpens = RecentFolders.Scan(directories);
        }
        catch (Exception)
        {
            explorerOpens = new Dictionary<string, DateTimeOffset>();
        }

        var now = DateTimeOffset.UtcNow;
        var all = new List<DockProject>();
        foreach (var (name, directory) in directories)
        {
            var match = ProjectNumber().Match(name);
            usage.TryGetValue(name, out var entry);
            entry ??= new UsageEntry();
            explorerOpens.TryGetValue(name, out var opened);
            DateTimeOffset? lastOpened = opened == default ? null : opened;
            all.Add(new DockProject(
                name,
                match.Value,
                directory,
                ReadLastActivity(directory),
                pins.Contains(name),
                DockWeight.Combine(entry.Score, entry.Updated, lastOpened, now, policy.HalfLifeDays),
                DockWeight.Decay(entry.Score, entry.Updated, now, policy.HalfLifeDays),
                lastOpened,
                excluded.Contains(name)));
        }

        // 固定项无视权重恒在最前；其余按加权频率降序，同分回退到 git 活动时间。
        var pinned = all.Where(item => item.Pinned && !item.Excluded)
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.LastActivity)
            .ToList();
        var rest = all.Where(item => !item.Pinned && !item.Excluded)
            .OrderByDescending(item => item.Weight)
            .ThenByDescending(item => item.LastActivity)
            .ToList();

        // 条数：先按上限截断，再用排序结果补足到下限，避免长期不用后列表变空。
        var quota = Math.Max(policy.MaxItems - pinned.Count, 0);
        var selected = pinned.Concat(rest.Take(quota)).ToList();
        if (selected.Count < policy.MinItems)
        {
            selected = pinned
                .Concat(rest.Take(Math.Max(policy.MinItems - pinned.Count, 0)))
                .ToList();
        }

        return new ScanResult(selected, OrderAll(all));
    }

    private static string ReadWorktreeRoot()
    {
        foreach (var settingsPath in new[] { SettingsPath, LegacySettingsPath })
        {
            try
            {
                if (!File.Exists(settingsPath))
                    continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                // 整库改名后配置值可能指向已不存在的目录，必须校验存在性再采用，
                // 否则扫描结果恒空、活动坞只剩"暂无活动项目"。
                if (doc.RootElement.TryGetProperty("proj.worktreeroot", out var value)
                    && value.GetString() is { Length: > 0 } configured
                    && Directory.Exists(configured))
                {
                    return configured;
                }
            }
            catch (JsonException)
            {
            }
        }

        return DefaultWorktreeRoot;
    }

    private static bool TryGetWorktreeProjectName(string target, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(target))
            return false;

        try
        {
            var projectPath = Path.GetFullPath(target.Trim());
            if (!Directory.Exists(projectPath))
                return false;
            var parent = Path.GetDirectoryName(projectPath);
            var candidate = Path.GetFileName(projectPath.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(parent)
                || string.IsNullOrWhiteSpace(candidate)
                || !RecentFolders.SamePath(parent, ReadWorktreeRoot())
                || !ProjectNumber().IsMatch(candidate))
            {
                return false;
            }

            name = candidate;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static DateTimeOffset ReadLastActivity(string directory)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git")
            {
                ArgumentList = { "-C", directory, "log", "-1", "--format=%ct" },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (process == null)
                return Directory.GetLastWriteTimeUtc(directory);
            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit(1500);
            if (process.ExitCode == 0 && long.TryParse(text.Trim(), out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception)
        {
        }

        return Directory.GetLastWriteTimeUtc(directory);
    }

    private static DockPreferences LoadPreferences()
    {
        Directory.CreateDirectory(DockRoot);
        MigrateLegacyState();
        try
        {
            if (File.Exists(StatePath))
            {
                var state = JsonSerializer.Deserialize<DockPreferences>(File.ReadAllText(StatePath));
                if (state != null)
                {
                    state.Pins = new HashSet<string>(state.Pins, StringComparer.OrdinalIgnoreCase);
                    state.Excluded = new HashSet<string>(state.Excluded, StringComparer.OrdinalIgnoreCase);
                    state.Usage = new Dictionary<string, UsageEntry>(state.Usage, StringComparer.OrdinalIgnoreCase);
                    state.Policy = state.Policy.Normalized();
                    return state;
                }
            }
        }
        catch (Exception)
        {
        }

        return new DockPreferences();
    }

    /// <summary>
    /// HistoryVulcan 数据根首次启用时，优先迁移旧 MercuryDock 偏好，再兼容更早的 ActiveDock 偏好。
    /// 保留固定项、排除名单、使用记录与策略。旧文件保留不删，便于回退对照。
    /// </summary>
    private static void MigrateLegacyState()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("MERCURY_STATE_DIRECTORY") is { Length: > 0 })
                return;
            if (File.Exists(StatePath))
                return;
            foreach (var root in new[] { PreviousMercuryDockRoot, LegacyMercuryDockRoot, LegacyActiveDockRoot })
            {
                var legacy = Path.Combine(root, "state.json");
                if (!File.Exists(legacy))
                    continue;
                File.Copy(legacy, StatePath);
                return;
            }
        }
        catch (Exception)
        {
            // 迁移失败时按全新偏好启动，不得影响模块加载。
        }
    }

    /// <summary>
    /// 先写临时文件再替换。另一侧进程被 FileSystemWatcher 唤醒时读到的一定是完整内容，
    /// 不会撞上写到一半的 JSON。
    /// </summary>
    private static void SavePreferences()
    {
        Directory.CreateDirectory(DockRoot);
        var payload = JsonSerializer.Serialize(_preferences, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
        var temporary = StatePath + ".tmp";
        try
        {
            File.WriteAllText(temporary, payload);
            File.Move(temporary, StatePath, overwrite: true);
        }
        catch (Exception)
        {
            try
            {
                File.WriteAllText(StatePath, payload);
            }
            catch (Exception)
            {
                // 偏好写入失败不得影响界面。
            }
        }
    }

    [GeneratedRegex(@"\d{4}-\d{3}", RegexOptions.CultureInvariant)]
    private static partial Regex ProjectNumber();

    private sealed record ScanResult(IReadOnlyList<DockProject> Selected, IReadOnlyList<DockProject> All);

    /// <summary>
    /// 1.0.0 的 Left/Top 字段已作废：窗口恒定吸附右下角，不再持久化位置。
    /// 反序列化默认忽略未知字段，旧 state.json 可直接读取，固定项不丢。
    /// </summary>
    private sealed class DockPreferences
    {
        public HashSet<string> Pins { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Hidden { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public Dictionary<string, UsageEntry> Usage { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Excluded { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public DockPolicy Policy { get; set; } = new();
        public List<DockCommandEntry> Commands { get; set; } = [];
    }

    /// <summary>已衰减到 <see cref="Updated"/> 时刻的点击分数，而不是原始次数。</summary>
    internal sealed class UsageEntry
    {
        public double Score { get; set; }
        public DateTimeOffset Updated { get; set; } = DateTimeOffset.UnixEpoch;
    }
}
