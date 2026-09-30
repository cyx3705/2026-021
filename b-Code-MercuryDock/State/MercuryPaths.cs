using System.IO;

namespace Mercury;

/// <summary>HistoryMercury runtime locations and read-only migration sources.</summary>
internal static class MercuryPaths
{
    public const string HostName = "HistoryVulcan";
    public const string LegacyHostName = "OneHistoryStudio";
    public const string ModuleName = "HistoryMercury";
    private const string PreviousModuleName = "MercuryDock";

    // 5.1.0（宿主 5.9.0 统一契约）：数据目录、项目库根与宿主可执行文件都由宿主在 Attach 时给出，
    // 不再自己拼 %AppData% 路径、读宿主设置文件或按固定目录猜宿主。Configure 必须是 Attach 的第一步：
    // MercuryState 的静态字段在类型初始化时就取 DataRoot。
    private static string? _dataRoot;

    /// <summary>宿主给的数据目录（<c>ModuleData/HistoryMercury</c>）。烟测等未经宿主装载的场合退回临时目录。</summary>
    public static string DataRoot => _dataRoot ?? Path.Combine(Path.GetTempPath(), ModuleName);

    /// <summary>宿主报告的项目库根（<c>vulcan.host.info</c> 的 libraryRoot）；取不到时为 null。</summary>
    public static string? LibraryRoot { get; private set; }

    /// <summary>宿主报告的正式服务程序（<c>vulcan.host.info</c> 的 hostExecutable）；取不到时为 null。</summary>
    public static string? HostExecutable { get; private set; }

    // 旧布局只用于一次性搬迁：它们都在宿主数据根（数据目录的上两级）下。宿主 6.0.0 后删除。
    private static string HostDataRoot => Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(DataRoot)))!;
    private static string LegacyAppRoot => Path.Combine(Path.GetDirectoryName(HostDataRoot)!, LegacyHostName);
    public static string PreviousHistoryVulcanDataRoot => Path.Combine(HostDataRoot, ModuleName);
    public static string PreviousDataRoot => Path.Combine(HostDataRoot, PreviousModuleName);
    public static string LegacyMercuryDockDataRoot => Path.Combine(LegacyAppRoot, PreviousModuleName);
    public static string LegacyActiveDockDataRoot => Path.Combine(LegacyAppRoot, "ActiveDock");

    public static void Configure(string dataDirectory, string? libraryRoot, string? hostExecutable)
    {
        _dataRoot = Path.GetFullPath(dataDirectory);
        LibraryRoot = string.IsNullOrWhiteSpace(libraryRoot) ? null : libraryRoot;
        HostExecutable = string.IsNullOrWhiteSpace(hostExecutable) ? null : hostExecutable;
    }
}
