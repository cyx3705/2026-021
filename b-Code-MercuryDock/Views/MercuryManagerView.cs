using System.Windows.Controls;
using HistoryVulcan.Core.Docking;

namespace Mercury;

/// <summary>扩展坞管理页面注册入口。</summary>
public static class MercuryManagerView
{
    public static ToolWindowDescriptor CreateDescriptor() => new()
    {
        Id = "dock.manager",
        Title = "扩展坞管理",
        DefaultSide = DockSide.Center,
        DefaultRatio = 1,
        IsSingleton = true,
        ContentFactory = static () => new MercuryManagerPage(),
    };
}
