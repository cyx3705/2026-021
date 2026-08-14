using System.IO;

namespace Mercury;

/// <summary>Keeps the Explorer-facing shortcut folder identical to the current dock list.</summary>
public static class DockShortcutFolder
{
    private static readonly object WatchGate = new();
    private static readonly TimeSpan WatchDebounce = TimeSpan.FromMilliseconds(350);
    private static FileSystemWatcher? _watcher;
    private static Timer? _debounceTimer;
    private static Action<ShortcutFolderDelta>? _changed;
    private static IReadOnlyDictionary<string, string> _expected =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public const string FolderName = "\u5feb\u6377\u65b9\u5f0f";
    public const string ModuleSlotName = "HistoryMercury";

    private const string IconSource = "%SystemRoot%\\System32\\shell32.dll";
    private const int IconIndex = 3;

    public static string Path => ResolvePath();

    public static bool IsExplorerRegistrationDisabled
        => string.Equals(
            Environment.GetEnvironmentVariable("MERCURY_DISABLE_EXPLORER_REGISTRATION"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Reconciles only .lnk files owned by the dock. Non-shortcut files are intentionally untouched.
    /// </summary>
    /// <remarks>
    /// 只写真正不一致的快捷方式。这个文件夹是钉在"此电脑"下的命名空间扩展，任何一次写入都会
    /// 让所有打开的资源管理器窗口（SeparateProcess 下是多个进程）刷新该节点，因此"内容没变也
    /// 全量重写"的代价是外部可见的，不只是几次磁盘 IO。
    /// </remarks>
    public static ShortcutSyncResult Synchronize(
        IReadOnlyList<DockProject> projects,
        string? folderOverride = null)
    {
        var folder = folderOverride ?? ResolvePath();
        Directory.CreateDirectory(folder);

        var desired = new Dictionary<string, DockProject>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in projects)
        {
            if (!Directory.Exists(project.Path))
                continue;
            desired[LinkPath(folder, project)] = project;
        }

        // 自己的写入不能再被自己的监视器读成"用户手动增删"，写期间先停掉事件，
        // 写完以现状为新基线再恢复。
        var suspended = folderOverride == null && SuspendWatching();
        try
        {
            var removed = 0;
            foreach (var existing in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
            {
                if (desired.ContainsKey(existing))
                    continue;
                File.Delete(existing);
                removed++;
            }

            var written = 0;
            var unchanged = 0;
            foreach (var (linkPath, project) in desired)
            {
                if (IsShortcutCurrent(linkPath, project))
                {
                    unchanged++;
                    continue;
                }

                WriteShortcut(linkPath, project);
                written++;
            }

            if (folderOverride == null)
                RememberSnapshot(ReadShortcuts(folder));

            return new ShortcutSyncResult(folder, written, removed, unchanged);
        }
        finally
        {
            if (suspended)
                ResumeWatching();
        }
    }

    /// <summary>
    /// Watches user edits to the managed folder. A missing existing link means the
    /// corresponding dock item was removed; a new link is a request to add its target.
    /// </summary>
    public static void StartWatching(Action<ShortcutFolderDelta> changed)
    {
        if (IsExplorerRegistrationDisabled)
            return;

        lock (WatchGate)
        {
            _changed = changed;
            if (_watcher != null)
                return;

            Directory.CreateDirectory(Path);
            _expected = ReadShortcuts(Path);
            _watcher = new FileSystemWatcher(Path, "*.lnk")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            _watcher.Created += (_, _) => ScheduleReconcile();
            _watcher.Deleted += (_, _) => ScheduleReconcile();
            _watcher.Changed += (_, _) => ScheduleReconcile();
            _watcher.Renamed += (_, _) => ScheduleReconcile();
        }
    }

    private static string ResolvePath()
    {
        // HistoryVulcan can shadow-copy a module into a transient load directory. Explorer must
        // always target the durable module slot instead of that transient location.
        return System.IO.Path.Combine(MercuryPaths.ModuleSlotRoot, FolderName);
    }

    private static string LinkPath(string folder, DockProject project)
    {
        var name = System.IO.Path.GetFileName(project.Path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("项目路径没有可用的目录名。");
        return System.IO.Path.Combine(folder, name + ".lnk");
    }

    private static void WriteShortcut(string linkPath, DockProject project)
        => ShortcutFileService.WriteShortcut(
            linkPath,
            project.Path,
            project.Name,
            IconSource,
            IconIndex);

    /// <summary>受管字段逐项相等才算最新；读不出来一律按需要重写处理。</summary>
    private static bool IsShortcutCurrent(string linkPath, DockProject project)
    {
        if (!ShortcutFileService.TryReadShortcut(linkPath, out var content))
            return false;
        return SamePath(content.Target, project.Path)
            && string.Equals(content.Description, project.Name, StringComparison.Ordinal)
            && string.Equals(content.IconPath, IconSource, StringComparison.OrdinalIgnoreCase)
            && content.IconIndex == IconIndex;
    }

    private static bool SamePath(string left, string right)
        => string.Equals(
            left.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            right.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool SuspendWatching()
    {
        lock (WatchGate)
        {
            if (_watcher is not { EnableRaisingEvents: true })
                return false;
            _watcher.EnableRaisingEvents = false;
            return true;
        }
    }

    private static void ResumeWatching()
    {
        lock (WatchGate)
        {
            if (_watcher != null)
                _watcher.EnableRaisingEvents = true;
        }
    }

    private static void ScheduleReconcile()
    {
        lock (WatchGate)
        {
            _debounceTimer ??= new Timer(
                _ => ReconcileExternalChanges(),
                null,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(WatchDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private static void ReconcileExternalChanges()
    {
        ShortcutFolderDelta changes;
        Action<ShortcutFolderDelta>? changed;
        lock (WatchGate)
        {
            var actual = ReadShortcuts(Path);
            changes = Compare(_expected, actual);
            _expected = actual;
            changed = _changed;
        }

        if (!changes.HasChanges || changed == null)
            return;

        try
        {
            changed(changes);
        }
        catch (Exception)
        {
            // A file-system event must never terminate the watcher callback thread.
        }
    }

    private static void RememberSnapshot(IReadOnlyDictionary<string, string> snapshot)
    {
        lock (WatchGate)
            _expected = snapshot;
    }

    internal static ShortcutFolderDelta Compare(
        IReadOnlyDictionary<string, string> expected,
        IReadOnlyDictionary<string, string> actual)
    {
        var removed = expected
            .Where(item => !actual.TryGetValue(item.Key, out var target)
                || !string.Equals(item.Value, target, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .ToList();
        var added = actual
            .Where(item => !expected.TryGetValue(item.Key, out var target)
                || !string.Equals(item.Value, target, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Value)
            .ToList();
        return new ShortcutFolderDelta(removed, added);
    }

    private static IReadOnlyDictionary<string, string> ReadShortcuts(string folder)
    {
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(folder))
            return links;

        foreach (var linkPath in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
            links[linkPath] = ShortcutFileService.ReadShortcutTarget(linkPath);
        return links;
    }

    public readonly record struct ShortcutSyncResult(
        string Folder,
        int Written,
        int Removed,
        int Unchanged = 0)
    {
        /// <summary>本次是否真的动过磁盘。只有动过才值得通知外壳。</summary>
        public bool Changed => Written != 0 || Removed != 0;
    }

    public sealed record ShortcutFolderDelta(
        IReadOnlyList<string> RemovedTargets,
        IReadOnlyList<string> AddedTargets)
    {
        public bool HasChanges => RemovedTargets.Count != 0 || AddedTargets.Count != 0;
    }

}
