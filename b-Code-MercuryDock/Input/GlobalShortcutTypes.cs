namespace Mercury.Input;

/// <summary>
/// 全局快捷键的数据模型。
///
/// 这些类型此前住在 HistoryVulcan.Core.Input，是宿主公开面的一部分（46 行 API）。
/// 但快捷键的实现、派发和生命周期从来都在 Mercury 这边，宿主只是持有契约并驱动注册——
/// 结果是「改快捷键必须动宿主」。契约随实现走之后，宿主不再需要知道快捷键这个概念存在，
/// 能力经 mercury.hotkey.* 命令暴露，调用方只依赖命令名与参数名。
///
/// 命名空间刻意用 Mercury.Input 而非沿用 HistoryVulcan.Core.Input：所有权已经转移，
/// 名字应当说实话；顺带也避免与宿主尚未清理干净的旧类型撞名。
/// </summary>
[Flags]
internal enum GlobalShortcutModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>一次击键：虚拟键码 + 修饰键。</summary>
internal sealed record GlobalShortcutStroke(
    int VirtualKey,
    GlobalShortcutModifiers Modifiers = GlobalShortcutModifiers.None);

/// <summary>
/// 一个不吞按键的全局快捷键。击键按顺序求值；重复与前缀冲突的注册会被拒绝。
/// CommandText 即命中后要执行的指令文本——快捷键本质就是「按键序列 → 命令名」。
/// </summary>
internal sealed record GlobalShortcutDescriptor(
    string Id,
    IReadOnlyList<GlobalShortcutStroke> Strokes,
    string CommandText,
    int MaxIntervalMilliseconds = 350);

/// <summary>对外可见的注册快照，供 mercury.hotkey.list 呈现。</summary>
internal sealed record GlobalShortcutRegistrationInfo(
    string Id,
    string Owner,
    IReadOnlyList<GlobalShortcutStroke> Strokes,
    string CommandText,
    int MaxIntervalMilliseconds);

/// <summary>按 owner 隔离的注册面；释放时一并注销该 owner 的全部注册。</summary>
internal interface IGlobalShortcutRegistrar : IDisposable
{
    IDisposable Register(GlobalShortcutDescriptor descriptor);
}
