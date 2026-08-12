using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using HistoryVulcan.Core.Commands;

namespace Mercury;

/// <summary>Resolves and opens ordinary paths and Windows shortcut files.</summary>
internal static class ShortcutFileService
{
    public static CommandResult Open(string? path)
    {
        if (!TryResolve(path, out var source, out var target, out var error))
            return CommandResult.Fail(error);

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return CommandResult.Ok(source.Equals(target, StringComparison.OrdinalIgnoreCase)
                ? $"已打开 {target}。"
                : $"已通过快捷方式打开 {target}。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return CommandResult.Fail($"无法打开 {target}：{ex.Message}");
        }
    }

    public static bool TryResolve(
        string? path,
        out string source,
        out string target,
        out string error)
    {
        source = "";
        target = "";
        error = "";
        if (string.IsNullOrWhiteSpace(path))
        {
            error = "用法：mercury.shortcut.open <path>。";
            return false;
        }

        try
        {
            source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"快捷文件路径无效：{ex.Message}";
            return false;
        }

        if (!File.Exists(source) && !Directory.Exists(source))
        {
            error = $"快捷文件或路径不存在：{source}";
            return false;
        }

        if (!source.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            target = source;
            return true;
        }

        target = ReadShortcutTarget(source);
        if (target.Length == 0)
        {
            error = $"无法解析快捷方式：{source}";
            return false;
        }
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            error = $"快捷方式目标不存在：{target}";
            return false;
        }
        return true;
    }

    public static string ReadShortcutTarget(string linkPath)
    {
        try
        {
            var shellLink = (IShellLinkW)new ShellLink();
            ((IPersistFile)shellLink).Load(linkPath, 0);
            var target = new StringBuilder(32768);
            shellLink.GetPath(target, target.Capacity, nint.Zero, 0);
            return target.Length == 0 ? string.Empty : Path.GetFullPath(target.ToString());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static void WriteShortcut(
        string linkPath,
        string targetPath,
        string description,
        string? iconPath = null,
        int iconIndex = 0)
    {
        var shellLink = (IShellLinkW)new ShellLink();
        shellLink.SetPath(targetPath);
        shellLink.SetWorkingDirectory(Directory.Exists(targetPath)
            ? targetPath
            : Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
        shellLink.SetDescription(description);
        if (!string.IsNullOrWhiteSpace(iconPath))
            shellLink.SetIconLocation(iconPath, iconIndex);
        ((IPersistFile)shellLink).Save(linkPath, true);
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
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int fileLength, nint findData, uint flags);
        void GetIDList(out nint itemIdList);
        void SetIDList(nint itemIdList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxPath);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        ushort GetHotkey();
        void SetHotkey(ushort hotkey);
        int GetShowCmd();
        void SetShowCmd(int showCommand);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int iconPathLength, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pathRelative, uint reserved);
        void Resolve(nint hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
