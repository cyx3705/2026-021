using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace Mercury;

/// <summary>Registers the dock-generated shortcut folder in the current user's Explorer namespace.</summary>
public static class ExplorerNamespaceRegistration
{
    public const string DisplayName = "HistoryClio \u9879\u76ee";
    public const string EntryClsid = "{B5E3B5AA-5F92-4A2A-9D4E-6A0B6A8E5C21}";

    private const string FolderShortcutClsid = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";
    private const string ClassesClsid = @"Software\Classes\CLSID\";
    private const string MyComputerNamespace =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\";
    private const string ManagedValue = "HistoryMercury.Managed";
    private const string PreviousManagedValue = "MercuryDock.Managed";
    private const string LegacyManagedValue = "ActiveDock.Managed";
    private const int ShellFolderAttributes = unchecked((int)0xF080004D);
    private const string IconValue = "%SystemRoot%\\System32\\shell32.dll,-4";
    private const string InProcServerValue = "%SystemRoot%\\System32\\shell32.dll";
    private const string ThreadingModelValue = "Apartment";
    private const int PinnedValue = 1;
    private const int SortOrderIndexValue = 0x42;
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcneUpdateDir = 0x00001000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfPathW = 0x0005;
    private static readonly string BackupPath = Path.Combine(
        MercuryPaths.DataRoot, "explorer-registration-backup.json");
    private static readonly string PreviousBackupPath = Path.Combine(
        MercuryPaths.PreviousDataRoot, "explorer-registration-backup.json");
    private static readonly string LegacyMercuryDockBackupPath = Path.Combine(
        MercuryPaths.LegacyMercuryDockDataRoot, "explorer-registration-backup.json");
    private static readonly string LegacyActiveDockBackupPath = Path.Combine(
        MercuryPaths.LegacyActiveDockDataRoot, "explorer-registration-backup.json");

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ClassesClsid + EntryClsid + @"\Instance\InitPropertyBag");
            return key?.GetValue("TargetFolderPath") is string path && Directory.Exists(path);
        }
        catch (SecurityException)
        {
            return false;
        }
    }

    public static RegistrationResult RegisterOrUpdate(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return RegistrationResult.Failed("项目根路径为空。");

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(fullPath))
                return RegistrationResult.Failed($"项目根路径不存在：{fullPath}");

            // 注册内容与目标完全一致时直接返回：不写注册表，也不发外壳通知。
            // 这一条是关键——刷新一次活动坞过去都会重写十几个注册表值并广播一次
            // SHCNE_ASSOCCHANGED，而绝大多数刷新其实什么都没变。
            if (IsCurrent(fullPath))
                return RegistrationResult.Unchanged(fullPath);

            using var root = Registry.CurrentUser.CreateSubKey(
                ClassesClsid + EntryClsid,
                RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (root == null)
                return RegistrationResult.Failed("无法打开每用户 CLSID 注册表项。");

            MigrateLegacyBackup();
            CapturePreviousRegistration(root);
            root.SetValue(null, DisplayName, RegistryValueKind.String);
            root.SetValue("System.IsPinnedToNameSpaceTree", PinnedValue, RegistryValueKind.DWord);
            root.SetValue("SortOrderIndex", SortOrderIndexValue, RegistryValueKind.DWord);
            root.SetValue(ManagedValue, 1, RegistryValueKind.DWord);

            using (var icon = root.CreateSubKey("DefaultIcon"))
                icon?.SetValue(null, IconValue, RegistryValueKind.ExpandString);

            using (var inproc = root.CreateSubKey("InProcServer32"))
            {
                inproc?.SetValue(null, InProcServerValue, RegistryValueKind.ExpandString);
                inproc?.SetValue("ThreadingModel", ThreadingModelValue, RegistryValueKind.String);
            }

            using (var instance = root.CreateSubKey("Instance"))
            {
                instance?.SetValue("CLSID", FolderShortcutClsid, RegistryValueKind.String);
                using var props = instance?.CreateSubKey("InitPropertyBag");
                props?.SetValue("TargetFolderPath", fullPath, RegistryValueKind.String);
            }

            using (var shellFolder = root.CreateSubKey("ShellFolder"))
                shellFolder?.SetValue("Attributes", ShellFolderAttributes, RegistryValueKind.DWord);

            using var namespaceKey = Registry.CurrentUser.CreateSubKey(MyComputerNamespace + EntryClsid);
            namespaceKey?.SetValue(null, DisplayName, RegistryValueKind.String);

            RefreshExplorerNamespace();
            return RegistrationResult.Succeeded(fullPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or ArgumentException or NotSupportedException)
        {
            return RegistrationResult.Failed($"资源管理器注册失败：{ex.Message}");
        }
    }

    public static RegistrationResult RemoveRegistration()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(ClassesClsid + EntryClsid, throwOnMissingSubKey: false);
            Registry.CurrentUser.DeleteSubKeyTree(MyComputerNamespace + EntryClsid, throwOnMissingSubKey: false);
            RestorePreviousRegistration();
            RefreshExplorerNamespace();
            return RegistrationResult.Succeeded(null);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            return RegistrationResult.Failed($"移除资源管理器注册失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 命名空间项本身增删改后的全局通知。
    /// </summary>
    /// <remarks>
    /// SHCNE_ASSOCCHANGED 会让每一个外壳进程丢弃图标缓存并重读文件关联；开启
    /// "在单独的进程中打开文件夹窗口"时，代价按打开的窗口进程数翻倍。它只在导航窗格需要
    /// 重新发现这个命名空间子项时才有必要，因此仅由 <see cref="RegisterOrUpdate"/> 与
    /// <see cref="RemoveRegistration"/> 在注册项真的变化时调用。目录内容变化请用
    /// <see cref="NotifyFolderChanged"/>。
    /// </remarks>
    public static void RefreshExplorerNamespace()
        => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, nint.Zero, nint.Zero);

    /// <summary>只通知某一个目录的内容变了，不碰图标缓存与文件关联。</summary>
    public static void NotifyFolderChanged(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;

        var buffer = Marshal.StringToCoTaskMemUni(folder);
        try
        {
            SHChangeNotify(ShcneUpdateDir, ShcnfPathW, buffer, nint.Zero);
        }
        finally
        {
            Marshal.FreeCoTaskMem(buffer);
        }
    }

    /// <summary>
    /// 逐项核对本模块写入的每一个注册值。任一项缺失或不符就返回 false，
    /// 这样半写坏的注册仍会被下一次调用修复。
    /// </summary>
    private static bool IsCurrent(string fullPath)
    {
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(ClassesClsid + EntryClsid);
            if (root == null
                || root.GetValue(null) as string != DisplayName
                || root.GetValue("System.IsPinnedToNameSpaceTree") is not int pinned || pinned != PinnedValue
                || root.GetValue("SortOrderIndex") is not int sortOrder || sortOrder != SortOrderIndexValue
                || root.GetValue(ManagedValue) is not int managed || managed != 1)
            {
                return false;
            }

            using var icon = root.OpenSubKey("DefaultIcon");
            if (!string.Equals(Raw(icon, null), IconValue, StringComparison.OrdinalIgnoreCase))
                return false;

            using var inproc = root.OpenSubKey("InProcServer32");
            if (!string.Equals(Raw(inproc, null), InProcServerValue, StringComparison.OrdinalIgnoreCase)
                || inproc?.GetValue("ThreadingModel") as string != ThreadingModelValue)
            {
                return false;
            }

            // GUID 与路径按大小写无关比较：否则历史写入的另一种大小写会让这里永远判不相等，
            // 反而变成"每次刷新都重写一遍"。
            using var instance = root.OpenSubKey("Instance");
            if (instance == null
                || !string.Equals(
                    instance.GetValue("CLSID") as string, FolderShortcutClsid, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var props = instance.OpenSubKey("InitPropertyBag");
            if (!string.Equals(
                    props?.GetValue("TargetFolderPath") as string, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using var shellFolder = root.OpenSubKey("ShellFolder");
            if (shellFolder?.GetValue("Attributes") is not int attributes
                || attributes != ShellFolderAttributes)
            {
                return false;
            }

            using var namespaceKey = Registry.CurrentUser.OpenSubKey(MyComputerNamespace + EntryClsid);
            return namespaceKey?.GetValue(null) as string == DisplayName;
        }
        catch (SecurityException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>取未展开的原始字符串，否则 ExpandString 会被展开成实际路径而永远比不相等。</summary>
    private static string? Raw(RegistryKey? key, string? name)
        => key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

    private static void CapturePreviousRegistration(RegistryKey root)
    {
        if ((root.GetValue(ManagedValue) is int managed && managed == 1)
            || (root.GetValue(PreviousManagedValue) is int previousManaged && previousManaged == 1)
            || (root.GetValue(LegacyManagedValue) is int legacyManaged && legacyManaged == 1))
            return;
        if (File.Exists(BackupPath))
            return;

        using var props = root.OpenSubKey("Instance\\InitPropertyBag");
        var previousPath = props?.GetValue("TargetFolderPath") as string;
        var previousName = root.GetValue(null) as string;
        Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
        File.WriteAllText(BackupPath, JsonSerializer.Serialize(new RegistrationBackup(previousName, previousPath)));
    }

    private static void MigrateLegacyBackup()
    {
        try
        {
            if (File.Exists(BackupPath))
                return;
            var source = new[] { PreviousBackupPath, LegacyMercuryDockBackupPath, LegacyActiveDockBackupPath }
                .FirstOrDefault(File.Exists);
            if (source == null || !File.Exists(source))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(BackupPath)!);
            File.Copy(source, BackupPath);
        }
        catch (Exception)
        {
            // A missing historical backup must not prevent the current registration.
        }
    }

    private static void RestorePreviousRegistration()
    {
        if (!File.Exists(BackupPath))
            return;

        try
        {
            var backup = JsonSerializer.Deserialize<RegistrationBackup>(File.ReadAllText(BackupPath));
            if (backup?.Path is { Length: > 0 } previousPath && Directory.Exists(previousPath))
            {
                using var root = Registry.CurrentUser.CreateSubKey(ClassesClsid + EntryClsid);
                root?.SetValue(null, backup.Name ?? DisplayName, RegistryValueKind.String);
                root?.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
                using var instance = root?.CreateSubKey("Instance");
                instance?.SetValue("CLSID", FolderShortcutClsid, RegistryValueKind.String);
                using var props = instance?.CreateSubKey("InitPropertyBag");
                props?.SetValue("TargetFolderPath", previousPath, RegistryValueKind.String);

                using var namespaceKey = Registry.CurrentUser.CreateSubKey(MyComputerNamespace + EntryClsid);
                namespaceKey?.SetValue(null, backup.Name ?? DisplayName, RegistryValueKind.String);
            }
        }
        finally
        {
            File.Delete(BackupPath);
        }
    }

    /// <summary>
    /// <paramref name="Changed"/> 区分"写入并通知过"和"本来就是这样"，调用方据此决定是否
    /// 还需要额外的外壳通知。
    /// </summary>
    public readonly record struct RegistrationResult(
        bool Success,
        string Message,
        string? Path,
        bool Changed = false)
    {
        public static RegistrationResult Succeeded(string? path)
            => new(true, path == null ? $"已移除 {DisplayName}入口。" : $"{DisplayName}入口指向 {path}。", path, true);

        public static RegistrationResult Unchanged(string path)
            => new(true, $"{DisplayName}入口已指向 {path}，无需改动。", path, false);

        public static RegistrationResult Failed(string message)
            => new(false, message, null);
    }

    private sealed record RegistrationBackup(string? Name, string? Path);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
