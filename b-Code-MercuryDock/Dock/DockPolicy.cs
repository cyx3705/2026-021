namespace Mercury;

/// <summary>收录策略：显示条数区间与时效半衰期。可在扩展坞管理页面调整。</summary>
public sealed class DockPolicy
{
    public const int DefaultMinItems = 6;
    public const int DefaultMaxItems = 12;
    public const double DefaultHalfLifeDays = 7;

    public const int LowestItems = 1;
    public const int HighestItems = 24;
    public const double ShortestHalfLifeDays = 0.5;
    public const double LongestHalfLifeDays = 90;

    /// <summary>候选不足时也要补足到这个条数，避免长期不用后活动坞变空。</summary>
    public int MinItems { get; set; } = DefaultMinItems;

    /// <summary>最多显示这么多条。</summary>
    public int MaxItems { get; set; } = DefaultMaxItems;

    /// <summary>使用记录的半衰期，缺省一周。</summary>
    public double HalfLifeDays { get; set; } = DefaultHalfLifeDays;

    /// <summary>把越界或互相矛盾的取值收敛到合法范围。</summary>
    public DockPolicy Normalized()
    {
        var max = Clamp(MaxItems, LowestItems, HighestItems, DefaultMaxItems);
        var min = Clamp(MinItems, LowestItems, HighestItems, DefaultMinItems);
        if (min > max)
            min = max;
        var halfLife = HalfLifeDays;
        if (double.IsNaN(halfLife) || double.IsInfinity(halfLife) || halfLife <= 0)
            halfLife = DefaultHalfLifeDays;
        halfLife = Math.Min(Math.Max(halfLife, ShortestHalfLifeDays), LongestHalfLifeDays);
        return new DockPolicy { MinItems = min, MaxItems = max, HalfLifeDays = halfLife };
    }

    private static int Clamp(int value, int min, int max, int fallback)
    {
        if (value <= 0)
            return fallback;
        return Math.Min(Math.Max(value, min), max);
    }
}
