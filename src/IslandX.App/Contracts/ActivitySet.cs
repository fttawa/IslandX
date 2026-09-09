namespace IslandX.Contracts;

/// <summary>
/// 此刻该显示什么。
///
/// 分成常驻与瞬时两路，是因为它们的**视觉角色**不同，而不只是优先级不同：
/// 常驻活动（媒体）占岛体主体，瞬时事件（插电 / 调音量 / 截图）在右侧另起一个小圆，
/// 两者用流体桥连成一体 —— 这就是 Split 态，"流体云"这名字的由来。
///
/// 如果只按优先级排出一个赢家，瞬时事件会把正在播的歌**顶掉**几秒再还回来，
/// 那是"打断"，不是"并列"。真机上这两件事是同时可见的。
/// </summary>
public sealed record ActivitySet(IslandActivity? Resident, IslandActivity? Transient)
{
    public static readonly ActivitySet Empty = new(null, null);

    /// <summary>岛体主体显示的活动。Split 时是常驻那个，否则就是唯一那个。</summary>
    public IslandActivity? Main => Resident ?? Transient;

    /// <summary>两路都在 = 并列显示 + 流体桥。</summary>
    public bool IsSplit => Resident is not null && Transient is not null;

    /// <summary>什么都没有（连兜底时钟都还没上来）。</summary>
    public bool IsEmpty => Resident is null && Transient is null;
}
