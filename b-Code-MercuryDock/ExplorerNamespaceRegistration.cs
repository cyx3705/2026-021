using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.Json;

namespace Mercury;

/// <summary>Registers the dock-generated shortcut folder in the current user's Explorer namespace.</summary>
public static class ExplorerNamespaceRegistration
{
    public const string DisplayName = "HistoryVesta \u9879\u76ee";
    public const string EntryClsid = "{B5E3B5AA-5F92-4A2A-9D4E-6A0B6A8E5C21}";

    private const string FolderShortcutClsid = "{0E5AAE11-A475-4c5b-AB00-C66DE400274E}";
    private const string ClassesClsid = @"Software\Classes\CLSID\";
    private const string MyComputerNamespace =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\MyComputer\NameSpace\";
    private const string ManagedValue = "HistoryMercury.Managed";
    private const string PreviousManagedValue = "MercuryDock.Managed";
    private const string LegacyManagedValue = "ActiveDock.Managed";
    private const int ShellFolderAttributes = unchecked((int)0xF080004D);
    private const uint ShcneAssocChanged = 0x08000000;
    private const uint ShcnfIdList = 0x0000;
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
            return RegistrationResult.Failed("Project root is empty.");

        try
        {
            var fullPath = Path.GetFullPath(path.Trim());
            if (!Directory.Exists(fullPath))
                return RegistrationResult.Failed($"Project root does not exist: {fullPath}");

            using var root = Registry.CurrentUser.CreateSubKey(
                ClassesClsid + EntryClsid,
                RegistryKeyPermissionCheck.ReadWriteSubTree);
            if (root == null)
                return RegistrationResult.Failed("Cannot open the per-user CLSID registry key.");

            MigrateLegacyBackup();
            CapturePreviousRegistration(root);
            root.SetValue(null, DisplayName, RegistryValueKind.String);
            root.SetValue("System.IsPinnedToNameSpaceTree", 1, RegistryValueKind.DWord);
            root.SetValue("SortOrderIndex", 0x42, RegistryValueKind.DWord);
            root.SetValue(ManagedValue, 1, RegistryValueKind.DWord);

            using (var icon = root.CreateSubKey("DefaultIcon"))
                icon?.SetValue(null, "%SystemRoot%\\System32\\shell32.dll,-4", RegistryValueKind.ExpandString);

            using (var inproc = root.CreateSubKey("InProcServer32"))
            {
                inproc?.SetValue(null, "%SystemRoot%\\System32\\shell32.dll", RegistryValueKind.ExpandString);
                inproc?.SetValue("ThreadingModel", "Apartment", RegistryValueKind.String);
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
            return RegistrationResult.Failed($"Explorer registration failed: {ex.Message}");
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
            return RegistrationResult.Failed($"Explorer registration removal failed: {ex.Message}");
        }
    }

    public static void RefreshExplorerNamespace()
        => SHChangeNotify(ShcneAssocChanged, ShcnfIdList, nint.Zero, nint.Zero);

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

    public readonly record struct RegistrationResult(bool Success, string Message, string? Path)
    {
        public static RegistrationResult Succeeded(string? path)
            => new(true, path == null ? "HistoryVesta project entry removed." : $"HistoryVesta project entry points to {path}.", path);

        public static RegistrationResult Failed(string message)
            => new(false, message, null);
    }

    private sealed record RegistrationBackup(string? Name, string? Path);

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(uint eventId, uint flags, nint item1, nint item2);
}
