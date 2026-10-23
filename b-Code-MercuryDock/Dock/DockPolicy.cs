namespace Mercury;

/// <summary>收录策略：项目条数定死，只剩时效半衰期可调。</summary>
/// <remarks>
/// 5.3.0 起桌面坞恒显示 <see cref="ItemCount"/> 个项目（用户定）：固定项在前，余下按权重补满。
/// 旧 state.json 里的 MinItems / MaxItems 反序列化时被忽略，不需要迁移。
/// </remarks>
public sealed class DockPolicy
{
    /// <summary>桌面坞上的项目个数，也是固定项的上限。加上「主界面」与「+」两格，坞上恒为 14 格。</summary>
    public const int ItemCount = 12;

    public const double DefaultHalfLifeDays = 7;

    public const double ShortestHalfLifeDays = 0.5;
    public const double LongestHalfLifeDays = 90;

    /// <summary>使用记录的半衰期，缺省一周。</summary>
    public double HalfLifeDays { get; set; } = DefaultHalfLifeDays;

    /// <summary>把越界的取值收敛到合法范围。</summary>
    public DockPolicy Normalized()
    {
        var halfLife = HalfLifeDays;
        if (double.IsNaN(halfLife) || double.IsInfinity(halfLife) || halfLife <= 0)
            halfLife = DefaultHalfLifeDays;
        halfLife = Math.Min(Math.Max(halfLife, ShortestHalfLifeDays), LongestHalfLifeDays);
        return new DockPolicy { HalfLifeDays = halfLife };
    }
}
