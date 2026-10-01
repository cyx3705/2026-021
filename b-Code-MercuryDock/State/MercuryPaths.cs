using System.IO;

namespace Mercury;

/// <summary>HistoryMercury 的运行期位置：全部由宿主在 Attach 时给出。</summary>
internal static class MercuryPaths
{
    public const string HostName = "HistoryVulcan";
    public const string ModuleName = "HistoryMercury";

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

    public static void Configure(string dataDirectory, string? libraryRoot, string? hostExecutable)
    {
        _dataRoot = Path.GetFullPath(dataDirectory);
        LibraryRoot = string.IsNullOrWhiteSpace(libraryRoot) ? null : libraryRoot;
        HostExecutable = string.IsNullOrWhiteSpace(hostExecutable) ? null : hostExecutable;
    }
}
