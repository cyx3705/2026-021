using System.IO;
using HistoryVulcan.Core.Logging;

namespace Mercury.Diagnostics;

/// <summary>
/// Mercury 自持的日志汇聚点。
/// </summary>
/// <remarks>
/// 宿主 5.0 起 <see cref="HistoryVulcan.Core.Modules.IModuleContext"/> 只剩 <c>Bus</c> 与
/// <c>RegisterCommands</c>，不再注入日志、设置或数据根，因此「模块出了什么事」必须由模块
/// 自己记下来。总线上也没有写日志的指令（<c>vulcan.log.*</c> 全是控制台的显示过滤），
/// 所以这里落到自己的数据根，而不是试图把条目塞回宿主控制台。
///
/// 5.2.0 起实现宿主 6.0.0 契约里的只写日志 <see cref="IModuleLog"/>：调用点写法不变，
/// 缓冲只供本类落盘，不再对外提供快照与新纪录事件（没有人读）。
/// </remarks>
internal sealed class MercuryLog : IModuleLog
{
    /// <summary>内存环形缓冲上限。坞是长期驻留进程，条目不设上限就是慢性泄漏。</summary>
    private const int Capacity = 512;

    private static readonly object Gate = new();

    private readonly Queue<Entry> _entries = new();
    private readonly string? _file;

    public MercuryLog()
        : this(DefaultFile())
    {
    }

    /// <param name="file">落盘路径；null 表示只留内存（烟测用）。</param>
    public MercuryLog(string? file) => _file = file;

    /// <summary>模块共用的实例。装载期任何一段代码都可能先于 <c>Attach</c> 需要记一行。</summary>
    public static MercuryLog Shared { get; } = new();

    public void Log(ShellLogLevel level, string category, string message)
    {
        var entry = new Entry(DateTime.Now, level, category, message);
        lock (Gate)
        {
            _entries.Enqueue(entry);
            while (_entries.Count > Capacity)
                _entries.Dequeue();
        }

        Append(entry);
        System.Diagnostics.Debug.WriteLine($"[mercury/{category}] {message}");
    }

    /// <summary>
    /// 落盘失败一律吞掉：记不下日志是小事，为记日志把桌面坞或模块装载带崩不是。
    /// </summary>
    private void Append(Entry entry)
    {
        if (_file == null)
            return;

        try
        {
            var line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level}] {entry.Category}: {entry.Message}";
            lock (Gate)
                File.AppendAllLines(_file, [line]);
        }
        catch (Exception)
        {
        }
    }

    private static string? DefaultFile()
    {
        try
        {
            var directory = Path.Combine(MercuryPaths.DataRoot, "logs");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, $"mercury-{DateTime.Now:yyyyMMdd}.log");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private sealed record Entry(DateTime Time, ShellLogLevel Level, string Category, string Message);
}
