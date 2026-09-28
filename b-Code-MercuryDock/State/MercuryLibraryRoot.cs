using System.IO;

namespace Mercury;

/// <summary>
/// 活动坞扫描的项目库根，缺省 HistoryClio。
/// </summary>
public static class MercuryLibraryRoot
{
    public const string Default = @"C:\OneHistory\HistoryClio";

    /// <summary>
    /// 优先 <c>proj.libraryroot</c>，其次仍存在的 <c>proj.worktreeroot</c>，最后缺省 Clio。
    /// 只接受磁盘上存在的目录，返回去掉末尾分隔符的完整路径。
    /// </summary>
    public static string Resolve(string? libraryRoot, string? worktreeRoot)
    {
        if (TryExisting(libraryRoot, out var fromLibrary))
            return fromLibrary;
        if (TryExisting(worktreeRoot, out var fromWorktree))
            return fromWorktree;
        return Default;
    }

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
