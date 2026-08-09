using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

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

    public static string Path => ResolvePath();

    public static bool IsExplorerRegistrationDisabled
        => string.Equals(
            Environment.GetEnvironmentVariable("MERCURY_DISABLE_EXPLORER_REGISTRATION"),
            "1",
            StringComparison.Ordinal);

    /// <summary>
    /// Reconciles only .lnk files owned by the dock. Non-shortcut files are intentionally untouched.
    /// </summary>
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

        var removed = 0;
        foreach (var existing in Directory.EnumerateFiles(folder, "*.lnk", SearchOption.TopDirectoryOnly))
        {
            if (desired.ContainsKey(existing))
                continue;
            File.Delete(existing);
            removed++;
        }

        var written = 0;
        foreach (var (linkPath, project) in desired)
        {
            WriteShortcut(linkPath, project);
            written++;
        }

        if (folderOverride == null)
            RememberSnapshot(ReadShortcuts(folder));

        return new ShortcutSyncResult(folder, written, removed);
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
            throw new InvalidOperationException("Project path has no directory name.");
        return System.IO.Path.Combine(folder, name + ".lnk");
    }

    private static void WriteShortcut(string linkPath, DockProject project)
    {
        var shellLink = (IShellLinkW)new ShellLink();
        shellLink.SetPath(project.Path);
        shellLink.SetWorkingDirectory(project.Path);
        shellLink.SetDescription(project.Name);
        shellLink.SetIconLocation("%SystemRoot%\\System32\\shell32.dll", 3);
        ((IPersistFile)shellLink).Save(linkPath, true);
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
            links[linkPath] = ReadShortcutTarget(linkPath);
        return links;
    }

    private static string ReadShortcutTarget(string linkPath)
    {
        try
        {
            var shellLink = (IShellLinkW)new ShellLink();
            ((IPersistFile)shellLink).Load(linkPath, 0);
            var target = new StringBuilder(32768);
            shellLink.GetPath(target, target.Capacity, nint.Zero, 0);
            return target.Length == 0 ? string.Empty : System.IO.Path.GetFullPath(target.ToString());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public readonly record struct ShortcutSyncResult(string Folder, int Written, int Removed);

    public sealed record ShortcutFolderDelta(
        IReadOnlyList<string> RemovedTargets,
        IReadOnlyList<string> AddedTargets)
    {
        public bool HasChanges => RemovedTargets.Count != 0 || AddedTargets.Count != 0;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int fileLength, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int maxPath);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        ushort GetHotkey();
        void SetHotkey(ushort hotkey);
        int GetShowCmd();
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRelative, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
