using System.Windows.Media;

namespace IslandX.Contracts;

public enum ActivityPriority
{
    Low = 0,
    Normal = 1,
    High = 2,
    Critical = 3,
}

/// <summary>
/// 所有系统事件归一化后的统一模型。Provider 只负责把系统状态翻译成活动，
/// 完全不碰 UI；加新功能等于加一个 Provider，而不是改渲染层。
/// </summary>
public sealed record IslandActivity
{
    public required string Id { get; init; }
    public required string ProviderId { get; init; }
    public ActivityPriority Priority { get; init; } = ActivityPriority.Normal;

    /// <summary>收起态左侧图标（Segoe Fluent Icons 字形）。</summary>
    public string Glyph { get; init; } = "";

    public required string Title { get; init; }
    public string? Subtitle { get; init; }

    /// <summary>0–1；为 null 时不显示进度条。</summary>
    public double? Progress { get; init; }

    /// <summary>展开态的封面图（如专辑封面）。</summary>
    public ImageSource? Artwork { get; init; }

    /// <summary>强调色，通常取自封面主色调。目前用于频谱条着色。</summary>
    public Color? AccentColor { get; init; }

    /// <summary>展开态是否显示媒体传输控件（上一曲/播放暂停/下一曲）。</summary>
    public bool HasTransportControls { get; init; }

    /// <summary>播放中为 true，用于切换播放/暂停按钮字形。</summary>
    public bool IsPlaying { get; init; }

    /// <summary>
    /// 当前该显示的那句歌词。由 Provider 按播放位置算好再放进来 ——
    /// 渲染层不该知道歌词有时间轴这回事，它只管画一行字。
    /// </summary>
    public string? Lyric { get; init; }

    /// <summary>
    /// 歌词的第二行（原文 + 译文并排显示时用）。null 表示只有一行。
    ///
    /// 刻意做成"第二行"而不是"译文"：渲染层不该知道这是翻译还是别的什么，
    /// **哪一行放什么由 Provider 按显示模式决定**（仅译文时第一行就是译文）。
    /// 这样加显示模式没有碰渲染层一行代码。
    /// </summary>
    public string? LyricSub { get; init; }

    /// <summary>
    /// 展开态里的迷你柱状图，值域 0–1。天气用它画未来几小时的降水预报。
    ///
    /// 刻意做成通用的"一组归一化数值"而不是 <c>Precipitation</c>：
    /// 渲染层不该知道这是雨还是别的什么，它只管画一排柱子 ——
    /// 这条约束正是"加功能不碰渲染层"能成立的原因。
    /// </summary>
    public IReadOnlyList<double>? Chart { get; init; }

    /// <summary>柱状图下方的一行说明，如"未来 4 小时 · 每格 15 分钟"。</summary>
    public string? ChartCaption { get; init; }

    /// <summary>
    /// 柱状图里要高亮的区间（含两端），<c>-1</c> 表示不高亮。
    /// 光看柱子高低分不清"哪几格算作这一场雨"，得把结论直接标出来。
    /// </summary>
    public int ChartFrom { get; init; } = -1;
    public int ChartTo { get; init; } = -1;

    /// <summary>
    /// 非 null 表示这是个**瞬时活动**：最后一次更新之后经过这段时间自动撤下。
    ///
    /// 由 <see cref="Core.ActivityScheduler"/> 统一计时，而不是各 Provider 自己起定时器 ——
    /// 插电、调音量、截图都是这个模式，各写一遍就是重复五份；
    /// 而且"连续事件要续期而不是各自倒计时"这条特别容易漏
    /// （连续按音量键会变成一串互相打架的定时器）。
    /// </summary>
    public TimeSpan? AutoDismissAfter { get; init; }

    /// <summary>
    /// 瞬时活动是否要占**主体位置**，而不是右侧那个 Split 小圆。
    ///
    /// 默认 false：插电、调音量、截图的信息量就一个图标那么大，小圆刚好，
    /// 而且不打断正在看的东西。但降水提醒有标题、时长、柱状图三层信息，
    /// 塞进 32px 的圆里等于没显示 —— 这类活动要临时占据主位，几秒后自动让回去。
    /// </summary>
    public bool WantsMainSlot { get; init; }
}

public interface IIslandProvider
{
    string Id { get; }

    /// <summary>当前活动变化时触发；传 null 表示该 Provider 当前无活动。</summary>
    event Action<IslandActivity?>? ActivityChanged;

    Task StartAsync();
    void Stop();
}
