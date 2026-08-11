using System.Runtime.Versioning;
using HistoryVulcan.Core.Commands;
using HistoryVulcan.Core.Logging;

namespace Mercury.Input;

/// <summary>
/// HistoryMercury 自持的全局快捷键服务。
///
/// 此前的装配路径是:Vulcan 的 App 在模块系统之外用 <c>Assembly.LoadFrom</c> 全盘预扫描模块
/// DLL,按约定构造签名反射构造出 <c>IGlobalShortcutHost</c> 实现,再交给 ModuleHost;ModuleHost
/// 又扫描各模块的 <c>IGlobalShortcutModule</c> 驱动注册。为一个「按键 → 命令名」的功能,宿主
/// 付出了 46 行公开类型契约 + 一次不可卸载的预扫描 + 一套扫描驱动逻辑。
///
/// 现在改为:快捷键完全是 Mercury 的私事。Mercury 在自己的模块生命周期里构造并启动服务,
/// 并以 <c>mercury.hotkey.*</c> 命令对外提供能力。任何模块(或人、或 AI)要注册快捷键,
/// 走命令总线即可,不需要引用任何类型。宿主不再需要知道「快捷键」这个概念存在。
///
/// 注册项本身就是「按键序列 → 命令文本」,所以命令化没有任何信息损失——
/// <see cref="GlobalShortcutDescriptor"/> 从一开始就带着 CommandText。
/// </summary>
[SupportedOSPlatform("windows")]
internal static class HotkeyService
{
    private static readonly object Sync = new();
    private static GlobalShortcutService? _service;

    // 底层服务按 owner 批量注销，没有按 id 注销的入口；命令层需要 id 粒度，
    // 因此注册句柄在这里按 id 留存。
    private static readonly Dictionary<string, IDisposable> Handles =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>当前实例;未启动时为 null。命令层据此给出明确的失败信息而不是空引用。</summary>
    internal static GlobalShortcutService? Current
    {
        get { lock (Sync) { return _service; } }
    }

    /// <summary>
    /// 在模块上下文接入时构造并启动。重复调用是幂等的:热重载会重新 Attach,
    /// 但键盘钩子必须唯一,否则同一组按键会触发两次。
    /// </summary>
    internal static void Start(CommandBus bus, IShellLog log)
    {
        if (!OperatingSystem.IsWindows())
            return;

        lock (Sync)
        {
            if (_service != null)
                return;

            try
            {
                var service = new GlobalShortcutService(bus, log);
                service.Start();
                _service = service;
                log.Info("hotkey", "HistoryMercury 全局快捷键服务已启动。");
            }
            catch (Exception ex)
            {
                // 快捷键不可用不得阻断模块装载(MD-06 同义)。
                log.Warn("hotkey", $"启动全局快捷键服务失败: {ex.Message}");
            }
        }
    }

    /// <summary>注册一个快捷键；同 id 重复注册会先注销旧的，使热重载可重入。</summary>
    internal static void Register(GlobalShortcutDescriptor descriptor, string owner)
    {
        lock (Sync)
        {
            var service = _service
                ?? throw new InvalidOperationException("全局快捷键服务未启动。");

            if (Handles.Remove(descriptor.Id, out var previous))
                previous.Dispose();

            Handles[descriptor.Id] = service.Register(descriptor, owner);
        }
    }

    /// <summary>按 id 注销；返回是否确有该注册。</summary>
    internal static bool Unregister(string id)
    {
        lock (Sync)
        {
            if (!Handles.Remove(id, out var handle))
                return false;
            handle.Dispose();
            return true;
        }
    }

    /// <summary>模块卸载时停止并释放钩子。</summary>
    internal static void Stop()
    {
        lock (Sync)
        {
            foreach (var handle in Handles.Values)
                handle.Dispose();
            Handles.Clear();
            _service?.Dispose();
            _service = null;
        }
    }
}
