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
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            ((IPersistFile)shellLink).Load(linkPath, 0);
            var target = new StringBuilder(32768);
            ((IShellLinkW)shellLink).GetPath(target, target.Capacity, nint.Zero, 0);
            return target.Length == 0 ? string.Empty : Path.GetFullPath(target.ToString());
        }
        catch (Exception)
        {
            return string.Empty;
        }
        finally
        {
            Release(shellLink);
        }
    }

    /// <summary>
    /// 读回快捷方式的全部受管字段，供写入前比对。取原始路径（不解析、不探测网络），
    /// 因为这里只需要判断"和我们要写的内容是否一致"。
    /// </summary>
    public static bool TryReadShortcut(string linkPath, out ShortcutContent content)
    {
        const uint RawPath = 0x4;
        content = default;
        if (!File.Exists(linkPath))
            return false;

        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            ((IPersistFile)shellLink).Load(linkPath, 0);
            var link = (IShellLinkW)shellLink;
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, nint.Zero, RawPath);
            if (target.Length == 0)
                return false;
            var description = new StringBuilder(1024);
            link.GetDescription(description, description.Capacity);
            var icon = new StringBuilder(32768);
            link.GetIconLocation(icon, icon.Capacity, out var iconIndex);
            content = new ShortcutContent(
                target.ToString(), description.ToString(), icon.ToString(), iconIndex);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Release(shellLink);
        }
    }

    public static void WriteShortcut(
        string linkPath,
        string targetPath,
        string description,
        string? iconPath = null,
        int iconIndex = 0)
    {
        object? shellLink = null;
        try
        {
            shellLink = new ShellLink();
            var link = (IShellLinkW)shellLink;
            link.SetPath(targetPath);
            link.SetWorkingDirectory(Directory.Exists(targetPath)
                ? targetPath
                : Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
            link.SetDescription(description);
            if (!string.IsNullOrWhiteSpace(iconPath))
                link.SetIconLocation(iconPath, iconIndex);
            ((IPersistFile)shellLink).Save(linkPath, true);
        }
        finally
        {
            Release(shellLink);
        }
    }

    /// <summary>
    /// 每次刷新都会创建若干 ShellLink，交给 GC 终结器回收会让 COM 引用在进程里堆积，
    /// 因此用完立刻释放 RCW。
    /// </summary>
    private static void Release(object? comObject)
    {
        if (comObject != null && Marshal.IsComObject(comObject))
            Marshal.FinalReleaseComObject(comObject);
    }

    /// <summary>快捷方式里由活动坞管理的字段。</summary>
    public readonly record struct ShortcutContent(
        string Target,
        string Description,
        string IconPath,
        int IconIndex);

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
