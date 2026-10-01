using System.IO;

namespace Mercury;

/// <summary>
/// 活动坞扫描的项目库根，缺省 HistoryClio。
/// </summary>
public static class MercuryLibraryRoot
{
    public const string Default = @"C:\OneHistory\HistoryClio";

    /// <summary>
    /// 取宿主报告的项目库根；只接受磁盘上存在的目录，返回去掉末尾分隔符的完整路径，否则缺省 Clio。
    /// </summary>
    public static string Resolve(string? libraryRoot)
        => TryExisting(libraryRoot, out var full) ? full : Default;

    private static bool TryExisting(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        full = Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return true;
    }
}
