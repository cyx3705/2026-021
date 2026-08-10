namespace Mercury;

/// <summary>
/// 收录权重的纯计算：指数衰减计数、信号合并与磁贴底色偏黄程度映射。
/// 不触碰文件与界面，便于 Smoke 直接断言。
/// </summary>
/// <remarks>
/// 计数不保存每次点击的时间戳，而是保存一个"已衰减到某时刻的分数"。
/// 读取时按经过时间继续衰减，写入时先衰减到当前再加增量，等价于对每次点击单独衰减后求和。
/// </remarks>
public static class DockWeight
{
    /// <summary>活动坞内点击一次的计权。</summary>
    public const double ClickWeight = 1.0;

    /// <summary>资源管理器打开一次的计权。</summary>
    public const double RecentWeight = 0.5;


    /// <summary>把分数从 <paramref name="updated"/> 衰减到 <paramref name="now"/>。</summary>
    public static double Decay(double score, DateTimeOffset updated, DateTimeOffset now, double halfLifeDays)
    {
        if (score <= 0)
            return 0;
        if (halfLifeDays <= 0)
            return score;
        var days = (now - updated).TotalDays;
        if (days <= 0)
            return score;
        return score * Math.Pow(0.5, days / halfLifeDays);
    }

    /// <summary>先衰减到当前时刻，再累加一次新的使用。</summary>
    public static double Accumulate(
        double score, DateTimeOffset updated, DateTimeOffset now, double increment, double halfLifeDays)
        => Decay(score, updated, now, halfLifeDays) + increment;

    /// <summary>
    /// 合并两路信号：模块自己累计的点击分数，以及资源管理器最近一次打开。
    /// git 活动时间不参与加权，只在全为零时作为排序回退。
    /// </summary>
    public static double Combine(
        double clickScore,
        DateTimeOffset clickUpdated,
        DateTimeOffset? explorerOpened,
        DateTimeOffset now,
        double halfLifeDays)
    {
        var total = Decay(clickScore, clickUpdated, now, halfLifeDays);
        if (explorerOpened is { } opened)
            total += Decay(RecentWeight, opened, now, halfLifeDays);
        return total;
    }

    /// <summary>权重归一化到 [0,1]，基准取当前列表最大值。</summary>
    public static double Normalize(double weight, double maximum)
    {
        if (maximum <= 0 || weight <= 0)
            return 0;
        return Math.Min(weight / maximum, 1);
    }

    /// <summary>
    /// 磁贴底色的偏黄程度，0 为纯白、1 为最黄。权重归一化后直接作为插值系数：
    /// 打开越频繁的项目底色越偏黄。
    /// </summary>
    public static double TileTint(double weight, double maximum) => Normalize(weight, maximum);
}
