using HistoryVulcan.Core.Commands;

namespace Mercury.Ui;

/// <summary>
/// 状态变了就请前端重拉本模块的页面（协议 §1.3 的上行通知）。
/// </summary>
/// <remarks>
/// 描述化之后页面是**派生状态**：表格数据在建页时取一次，策略面板的初值写在描述里。
/// 因此坞的内容一变，页面就旧了——4.x 的管理页是自己订 <see cref="MercuryState.Changed"/>
/// 重画的，那条路随视图一起没了。协议为此留了 <c>aurora.ui.invalidate</c>：
/// 模块只说「我变了」，由 Aurora 决定怎么重建，宿主仍然不解释任何界面语义。
///
/// 两点刻意为之：
/// <list type="bullet">
///   <item><b>合并</b>。<c>state.json</c> 的监视一次保存可能连发数次，逐次重拉就是
///         逐次建撤页面。这里压成一个延时窗口，窗口内再多次变化也只发一条；</item>
///   <item><b>失败不管</b>。前端没装时这条指令根本不存在，总线立刻返回失败而不是等超时。
///         那不是异常状况，是「现在没有界面要更新」，不该在日志里刷成告警。</item>
/// </list>
/// </remarks>
internal static class PageInvalidation
{
    /// <summary>合并窗口。取值与管理页旧实现的状态防抖同量级（80~120ms）再放宽一档。</summary>
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    private const string Command = "aurora.ui.invalidate owner=HistoryMercury";

    private static readonly object Gate = new();

    private static Timer? _timer;

    private static CommandBus? _bus;

    public static void Start(CommandBus bus)
    {
        lock (Gate)
        {
            if (_bus != null)
                return;
            _bus = bus;
            _timer = new Timer(_ => Send(), null, Timeout.Infinite, Timeout.Infinite);
            MercuryState.Changed += OnChanged;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_bus == null)
                return;
            MercuryState.Changed -= OnChanged;
            _timer?.Dispose();
            _timer = null;
            _bus = null;
        }
    }

    private static void OnChanged()
    {
        lock (Gate)
            _timer?.Change(Delay, Timeout.InfiniteTimeSpan);
    }

    private static void Send()
    {
        CommandBus? bus;
        lock (Gate)
            bus = _bus;
        if (bus == null)
            return;

        try
        {
            _ = bus.ExecuteAsync(Command, "Mercury");
        }
        catch (Exception)
        {
            // 前端不在就是不在。
        }
    }
}
