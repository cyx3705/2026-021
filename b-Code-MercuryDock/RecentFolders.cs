using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace MercuryDock;

/// <summary>
/// 从 Windows 最近使用项读取"项目主文件夹最近一次在资源管理器打开"的时间。
/// </summary>
/// <remarks>
/// Windows 不提供文件夹打开次数：系统"频繁访问"文件夹与快速访问跳转列表在实测中为空或格式未公开，
/// UserAssist 面向可执行程序。唯一可用的是 Recent 目录下的 .lnk 时间戳，只有时间没有次数。
///
/// 只统计项目主文件夹：先按文件名粗筛（含 "xxx (2).lnk" 这类重名变体），再解析 .lnk 的目标路径，
/// 要求与项目根目录完全相等，任何子文件夹一律忽略。
/// 解析失败时整体降级为"无此信号"，不得阻断活动坞刷新。
/// </remarks>
public static partial class RecentFolders
{
    private const uint RawPath = 0x4;

    private static readonly string RecentRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Microsoft", "Windows", "Recent");

    /// <summary>返回项目名到最近打开时间的映射；无法取得时返回空表。</summary>
    public static IReadOnlyDictionary<string, DateTimeOffset> Scan(IReadOnlyDictionary<string, string> projectPaths)
    {
        var result = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        if (projectPaths.Count == 0 || !Directory.Exists(RecentRoot))
            return result;

        IEnumerable<string> links;
        try
        {
            links = Directory.EnumerateFiles(RecentRoot, "*.lnk");
        }
        catch (Exception)
        {
            return result;
        }

        foreach (var link in links)
        {
            string candidate;
            try
            {
                candidate = StripCopySuffix(Path.GetFileNameWithoutExtension(link));
            }
            catch (Exception)
            {
                continue;
            }

            // 粗筛：文件名必须先命中某个项目名，避免对 200+ 个 .lnk 全量做 COM 解析。
            if (!projectPaths.TryGetValue(candidate, out var projectPath))
                continue;

            var target = ResolveTarget(link);
            if (target == null || !SamePath(target, projectPath))
                continue;

            DateTimeOffset opened;
            try
            {
                opened = File.GetLastWriteTimeUtc(link);
            }
            catch (Exception)
            {
                continue;
            }

            if (!result.TryGetValue(candidate, out var existing) || opened > existing)
                result[candidate] = opened;
        }

        return result;
    }

    /// <summary>去掉资源管理器为重名快捷方式追加的 " (2)" 后缀。</summary>
    public static string StripCopySuffix(string name)
        => CopySuffix().Replace(name, string.Empty).TrimEnd();

    public static bool SamePath(string left, string right)
        => string.Equals(
            left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? ResolveTarget(string linkPath)
    {
        try
        {
            var link = (IShellLinkW)new ShellLink();
            ((IPersistFile)link).Load(linkPath, 0);
            var buffer = new StringBuilder(260);
            // RawPath：只取记录的原始路径，不触发解析、不探测网络与失效目标。
            link.GetPath(buffer, buffer.Capacity, IntPtr.Zero, RawPath);
            var path = buffer.ToString();
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [GeneratedRegex(@"\s*\(\d+\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CopySuffix();

    // 不能标 sealed：密封类到无关接口的转换会在编译期被拒，COM 转换需要运行时 QueryInterface。
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    /// <summary>只声明首个槽位 GetPath；本模块不调用其余方法。</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(
            [Out][MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int fileLength,
            IntPtr findData,
            uint flags);
    }
}
