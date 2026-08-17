using System.IO;

namespace Mercury;

/// <summary>
/// 活动坞扫描的项目库根。当前库是 HistoryClio；配置里若仍指向 HistoryVesta 则改写过来。
/// </summary>
public static class MercuryLibraryRoot
{
    public const string Default = @"C:\OneHistory\HistoryClio";
    public const string LegacyVesta = @"C:\OneHistory\HistoryVesta";

    /// <summary>
    /// 已退役的 Vesta 路径改写到 <see cref="Default"/>（该库存在时）。
    /// 其它现存路径原样返回。
    /// </summary>
    public static string Coerce(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path.Trim())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (full.Equals(LegacyVesta, StringComparison.OrdinalIgnoreCase)
            || Directory.Exists(Path.Combine(full, "HistoryVesta.git")))
        {
            if (Directory.Exists(Default))
                return Default;
        }

        return full;
    }

    /// <summary>
    /// 优先 <c>proj.libraryroot</c>，其次仍存在的 <c>proj.worktreeroot</c>，最后缺省 Clio。
    /// 命中的路径都经过 <see cref="Coerce"/>。
    /// </summary>
    public static string Resolve(string? libraryRoot, string? worktreeRoot)
    {
        if (TryExisting(libraryRoot, out var fromLibrary))
            return Coerce(fromLibrary);
        if (TryExisting(worktreeRoot, out var fromWorktree))
            return Coerce(fromWorktree);
        return Default;
    }

    private static bool TryExisting(string? path, out string full)
    {
        full = string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            return false;
        full = Path.GetFullPath(path.Trim());
        return true;
    }
}
