using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandX.Contracts;
using IslandX.Core;
using IslandX.Interop;
using IslandX.Rendering;

namespace IslandX;

public enum IslandVisualState
{
    /// <summary>灵动点：一条细横条，不干扰使用。</summary>
    Dot,

    /// <summary>收起胶囊：图标 + 一行摘要。</summary>
    Pill,

    /// <summary>展开卡片：完整信息 + 可交互控件。</summary>
    Expanded,
}

public partial class IslandWindow : Window
{
    // ===== 岛体几何（逻辑像素）=====
    private const double DotWidth = 68, DotHeight = 8, DotRadius = 4;
    private const double PillHeight = 40, PillRadius = 20;

    /// <summary>
    /// 双行歌词（原文 + 译文）时的胶囊尺寸。
    ///
    /// 不硬塞进 40 高里：那样两行各只剩 16px，字号得压到 11 以下才放得下，
    /// 小到不如不显示。加高 14px 换来两行都能读，而窗口画布（140）远够。
    /// 圆角跟着走保持半圆，否则加高后两端会从"胶囊"变成"圆角矩形"。
    /// </summary>
    private const double PillHeightTwoLine = 54, PillRadiusTwoLine = 27;
    private const double ExpandedHeight = 140, ExpandedRadius = 26;
    private const double PeekScale = 1.06;      // 悬停时胶囊轻微"呼吸"
    /// <summary>收起态 ↔ 展开态切换时，内容的淡入淡出位移量。</summary>
    private const double ContentShift = 8;

    /// <summary>
    /// 切歌转场时新旧两层的错开距离。
    ///
    /// 比 <see cref="ContentShift"/> 大得多是**必须的**：那两层在交叉期同时可见，
    /// 靠垂直错位才不糊（8px 时两层压在同一位置，是一团重影）。
    /// 14px 约等于一行文字的高度，中点两层相距约 18px，刚好错开。
    /// 展开/收起没有这个问题 —— 那里同一时刻只有一套内容在动。
    /// </summary>
    private const double SwapShift = 14;

    /// <summary>
    /// 形变期的目标帧率，按 vsync 整数倍分频逼近（见 <see cref="OnRendering"/>）。
    ///
    /// 240 在本机意味着除数 1（跟满回调流）。实际到不了 240：WPF 分层窗口的
    /// 呈现路径每帧固定 ~5.1ms（逐项 ISLANDX_SKIP 对照过，我们自己的每帧操作
    /// 各只占 0.3–0.5ms，大头在渲染线程→表面拷贝→DWM 这条路上），
    /// 上限约 195fps —— 这已是 WPF 能给的全部，再往上只能换 DirectComposition（M6）。
    /// 要省电可降回 120（除数 2，快段 ~105fps，形变 CPU 从 ~20% 降到 ~17%）。
    /// </summary>
    private const double MorphTargetFps = 240;

    /// <summary>纯律动（频谱条）的目标帧率。鼓点每秒就几次，再高只是白烧。</summary>
    private const double PulseTargetFps = 36;

    /// <summary>
    /// 回弹尾巴的"慢"判据：|速度| ≤ 此值 × RestScale（尺寸弹簧即 60px/s，
    /// 约合 120fps 下每帧半像素）。所有弹簧都慢于它之后，帧率减半也分辨不出。
    /// </summary>
    private const double SlowTailSpeed = 2.4;

    /// <summary>
    /// 岛体画布的恒定尺寸。所有元素尺寸固定、形变只走几何与 RenderTransform，
    /// 这样每帧都不触发布局 —— 布局是 WPF 里最贵的一项，形变期尤其明显。
    /// 宽度须 ≥ <see cref="ExpandedMaxWidth"/>。
    ///
    /// 与 XAML 里窗口尺寸（640×168）的耦合：分层窗口每帧要把整个表面推给 DWM，
    /// 面积直接就是钱，所以窗口只比"形状可能到达的最大范围"大一圈。
    /// 最坏情况是 Dot→Expanded 入场（ζ≈0.55，首摆过冲约 13%）：
    ///   宽 560 + 0.13×(560−68) ≈ 624，高 140 + 0.09×(140−8) ≈ 152。
    /// 窗口 640×168 各留 ≥16 的余量。若把入场阻尼再调低（更弹），先重算这笔账，
    /// 否则过冲的形状会探出窗口边被硬切。
    /// </summary>
    private const double BodyCanvasWidth = 560, BodyCanvasHeight = 140;

    /// <summary>感应区的基准边长，实际尺寸靠 ScaleTransform 撑开。</summary>
    private const double HitBase = 100;

    // 两态宽度都随内容自适应（流体云的重要特征：宽度跟着信息量走）
    private const double PillMinWidth = 190, PillMaxWidth = 480;
    private const double ExpandedMinWidth = 380, ExpandedMaxWidth = 560;
    private const double CompactArtSize = 28;
    private const double CompactPadLeft = 6, CompactPadRight = 14, CompactGap = 10, CompactSubGap = 10;

    /// <summary>频谱条与文字之间的间距。</summary>
    private const double SpectrumGap = 12;

    // ===== 歌词逐句切换 =====
    //
    // 参数对齐 WinIsland（E:\WinIsland，Rust + Skia）的做法：
    // 旧句向上飘 10px、越飘越糊、线性淡出；新句从下方 10px 上来、由糊转清、线性淡入。
    // 两条透明度曲线**严格互补**，不做错峰 —— 这一点与切歌转场刻意相反：
    // 那里两层内容完全不同、必须靠错开区分；而这里两层是同一位置的连续歌词，
    // 靠"一个在糊掉、一个在变清"就足以区分，再错峰反而显得拖沓。
    //
    // 关键是模糊**只在水平方向**（见 Rendering/LyricLayer.cs）。
    // 各向同性模糊看着像没对上焦，方向性模糊才有横向抹开的速度感。

    /// <summary>进出场的位移（DIP）。对称：旧句上飘多少，新句就从下方多少上来。</summary>
    private const double LyricShift = 10;

    /// <summary>
    /// 水平模糊的峰值 sigma（DIP）。旧句飘到头 / 新句刚起步时的强度。
    ///
    /// WinIsland 用的是 12（对它 12px 的歌词字号，比例 1.0），但**不能照抄**：
    /// 它走 Skia 的真高斯，而这里是多份偏移叠加（见 <see cref="LyricLayer"/>）。
    /// 两者对文字这种"细笔画 + 大量空隙"的形状差别很大 ——
    /// 真高斯让空隙得到低强度的能量，而多份叠加走的是 source-over
    /// （1−(1−a₁)(1−a₂)…），空隙会被相邻份**完整填满**，很快糊成一团。
    /// 实测 12 时中间帧完全看不出是文字，6 才对得上 WinIsland 那种"抹开但还认得出"。
    /// </summary>
    private const double LyricBlurSigma = 6;

    /// <summary>
    /// 旧句淡到看不见的行程点，也就是**槽宽可以收窄的时刻**。
    ///
    /// 透明度是线性互补的（旧句 = 1 − t），所以 0.88 对应残余 0.12 —— 已经看不出来了。
    /// 不等 t=1 是因为弹簧还要走完过冲回弹，实测多等约 200ms，
    /// 看到的是"新句早就位了、岛体过一会儿才想起来收窄"的两段式。
    /// </summary>
    private const double LyricOutFaded = 0.88;

    /// <summary>歌词字号。比艺人名（12）大 —— 它接管的是主体位置，不再是附注。</summary>
    private const double LyricFontSize = 14;

    /// <summary>歌词单句的最大宽度，与 XAML 里 LyricA/B 的 MaxWidth 一致。</summary>
    private const double LyricMaxWidth = 340;

    /// <summary>译文相对原文的字号比例。小一号，让原文仍是主角。</summary>
    private const double LyricSubScale = 0.82;

    // ===== Split 态：常驻活动 + 右侧瞬时事件圆 =====

    /// <summary>瞬时事件圆的直径。</summary>
    private const double SplitDotSize = 32;

    /// <summary>
    /// 主体与事件圆之间的间距。
    ///
    /// 这里**刻意不连流体桥**。桥做出来了（<see cref="Rendering.FluidBridge"/>，
    /// 几何正确、有验证台），但实际挂在岛体上是个哑铃形，笨重、不像灵动岛 ——
    /// 真机的 Split 就是两个分离的形状。桥留给 M4 的 Merged 聚合态。
    ///
    /// 10px 是"明确分开"与"仍是一组"的平衡：再窄会看着像没分开，再宽就散了。
    /// </summary>
    private const double SplitGap = 10;
    private const double ExpandedArtSize = 88;
    private const double ExpandedPad = 20, ExpandedGap = 16;

    // ===== 形变弹簧 =====
    private readonly SpringValue _width = new(DotWidth, 220, 26);
    private readonly SpringValue _height = new(DotHeight, 220, 26);
    private readonly SpringValue _radius = new(DotRadius, 220, 26);
    private readonly SpringValue _compactOpacity = new(0, 320, 30);   // 快出
    private readonly SpringValue _expandedOpacity = new(0, 200, 28);  // 慢入，形成错峰

    /// <summary>
    /// 切歌转场进度：0 = 完全是旧内容快照，1 = 完全是新内容。
    /// 刚度取得比形变更高 —— 内容转场拖沓会让空档期变长，看着像卡了一下。
    /// ζ≈0.82 不过冲：透明度过冲会被 clamp 成"到了又退回来"，是明显的抖。
    /// </summary>
    private readonly SpringValue _contentSwap = new(1, 480, 36);

    /// <summary>
    /// 歌词换句进度：0 = 旧句原位、新句还在下方，1 = 新句就位。
    ///
    /// ζ≈0.59（约 10% 过冲），是这里唯一**刻意过冲**的内容动画。
    /// 过冲落在**位移**上而不是缩放上：行程 7px × 10% ≈ 0.7px 肉眼刚好能感到"顶一下"，
    /// 而缩放行程只有 0.10，同样的过冲率折算到 14px 字上是 0.02px —— 等于没有。
    /// 透明度则一律 clamp，冲过 1 再退回来是肉眼可见的闪。
    /// </summary>
    private readonly SpringValue _lyricSwap = new(1, 680, 31);

    /// <summary>
    /// 歌名组 ↔ 歌词组的让位进度：0 = 显示「歌名 + 艺人」，1 = 显示歌词。
    /// 不过冲（ζ≈0.95）：这是一次纯交叉淡化，过冲只会让两组一起忽明忽暗。
    /// </summary>
    private readonly SpringValue _titleHold = new(0, 400, 38);

    private readonly SpringValue[] _allSprings;

    // ===== 歌词双层轮换 =====

    /// <summary>当前承担"进场/显示"角色的是不是 LyricB。每换一句翻转一次。</summary>
    private bool _lyricUseB;

    /// <summary>当前显示的歌词，用于识别换句。</summary>
    private string _lyricText = "";

    /// <summary>当前的第二行（译文），null = 单行。</summary>
    private string? _lyricSub;

    /// <summary>
    /// 换句期间文本槽宽度的下限 —— 上一句的宽度。换完清零。
    ///
    /// 没有它的话，槽宽在换句那一瞬就跳到新句的长度，而**旧句还完整地待在退场层里**，
    /// 于是被立刻收窄的槽连同 TextTrimming 一起硬截断：
    /// 长句「把所有没说出口的答案都留给明天早上的风」当场变成「把所有…」，
    /// 而那一帧它还是 100% 不透明 —— 看到的是"文字突然被切短，然后才飘走"。
    /// 顺带还解决了另一半：岛体宽度目标同样按 max 算，动画期间几乎不动、
    /// 旧句飘走后才收窄，中途那段"内容居中、左右大片留白"也就没有了。
    /// </summary>
    private double _lyricSlotFloor;

    /// <summary>
    /// 歌词动画还有没有未落地的一帧。
    ///
    /// 两个歌词弹簧静止时就不必每帧重刷了 —— 律动期的渲染是持续跑着的，
    /// 而歌词几秒才换一句，白刷十几个属性会变成常驻开销。
    /// 但**弹簧刚静止那一帧的终值必须写下去**，否则动画会停在最后一个中间态上
    /// （差个零点几像素的位移、半点模糊），所以用这个标记补上收尾的那次。
    /// </summary>
    private bool _lyricDirty;

    /// <summary>歌词文字色。比艺人名亮得多 —— 它接管的是主体位置，不再是附注。</summary>
    private static readonly Brush LyricBrush = CreateFrozenBrush(0xED, 0xFF, 0xFF, 0xFF);

    /// <summary>译文比原文暗一档，视觉上分出主次。</summary>
    private static readonly Brush LyricSubBrush = CreateFrozenBrush(0x9E, 0xFF, 0xFF, 0xFF);

    private static Brush CreateFrozenBrush(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private readonly Stopwatch _frameClock = new();

    /// <summary>收起态的自适应目标宽度，由标题/副标题实测文本宽度算出。</summary>
    private double _pillWidth = 300;

    /// <summary>展开态的自适应目标宽度，避免长标题被固定宽度截断。</summary>
    private double _expandedWidth = 420;

    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _guardTimer;

    private ActivityScheduler? _scheduler;
    private IslandActivity? _activity;
    private AudioPulse? _audio;

    /// <summary>
    /// 正在画的瞬时事件。**退场动画期间仍然保留** —— 圆要缩回去才算走完，
    /// 一撤下就置 null 的话渲染分支立刻停画，split 弹簧白跑一趟，
    /// 看到的是圆凭空消失。等 split 归零静止后才真正清掉。
    /// </summary>
    private IslandActivity? _transient;

    /// <summary>逻辑上此刻有没有瞬时事件。决定 split 的目标值，与"还画不画"分开。</summary>
    private bool _hasTransient;

    /// <summary>
    /// Split 的展开度：0 = 事件圆缩到看不见，1 = 圆到位。
    /// ζ≈0.58，过冲约 11% —— 圆弹出时先胀一下再收住，比匀速放大有生气。
    /// </summary>
    private readonly SpringValue _split = new(0, 300, 20);

    /// <summary>最近 9 次渲染回调间隔的环形窗口，取中位数当 vsync 周期估计。</summary>
    private readonly double[] _dtWindow = new double[9];
    private int _dtIndex;
    private int _dtFill;

    /// <summary>分频计数器：攒够除数才渲染一帧。</summary>
    private int _skipCounter;

    /// <summary>频谱条上次重画的时刻，限流用（形变期渲染帧率远高于频谱数据帧率）。</summary>
    private long _spectrumTicks;

    // 上次重建几何时的形状，用于跳过律动期无谓的轮廓重建
    private double _geoWidth = -1, _geoHeight = -1, _geoRadius = -1, _geoSplit = -1;

    private bool _renderHooked;
    private bool _expanded;
    private bool _hovering;
    private bool _dotMode;

    /// <summary>限帧用的时间累积器：被跳过的 vsync 的时长留在这里，不丢弃。</summary>
    private double _pendingDt;

    /// <summary>是否正在做切歌交叉淡化（TransitionLayer 持有旧内容快照）。</summary>
    private bool _snapshotActive;

    /// <summary>
    /// 性能定位用：跳过每帧某一类操作，测出真正的开销所在。
    /// Release 下是编译期常量 null，分支会被 JIT 消除。
    /// </summary>
#if DEBUG
    private static readonly string? PerfSkipMode = Environment.GetEnvironmentVariable("ISLANDX_SKIP");
    private static bool Skip(string what) => PerfSkipMode == what;

    /// <summary>调参用：ISLANDX_FPS 覆盖形变帧率上限，0 = 不限（跟随 vsync）。</summary>
    private static readonly double FpsOverride =
        double.TryParse(Environment.GetEnvironmentVariable("ISLANDX_FPS"), out var f) && f >= 0 ? f : -1;
#else
    private static bool Skip(string what) => false;
#endif

    /// <summary>媒体控制回调，由 App 接线，避免窗口直接依赖具体 Provider。</summary>
    public Func<Task>? OnPrevious { get; set; }
    public Func<Task>? OnTogglePlay { get; set; }
    public Func<Task>? OnNext { get; set; }

    public IslandWindow()
    {
        InitializeComponent();

        _allSprings =
        [
            _width, _height, _radius, _compactOpacity, _expandedOpacity,
            _contentSwap, _split, _lyricSwap, _titleHold,
        ];

        // 尺寸弹簧按像素量级判定静止；透明度与 Split 展开度留在默认的 0–1 量级。
        _width.RestScale = _height.RestScale = _radius.RestScale = 25;

        // 岛体画布尺寸恒定，形变只改几何
        IslandBody.Width = BodyCanvasWidth;
        IslandBody.Height = BodyCanvasHeight;

        var pen = new Pen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)), 1);
        pen.Freeze();
        BodyShape.ShapeFill = new SolidColorBrush(Color.FromArgb(0xFA, 0, 0, 0));
        BodyShape.ShapeFill.Freeze();
        BodyShape.ShapePen = pen;

        // 歌词两层是自绘元素，画刷/字体/截断上限由代码给（XAML 里没有这些属性）
        foreach (var layer in new[] { LyricA, LyricB })
        {
            layer.Foreground = LyricBrush;
            layer.SubForeground = LyricSubBrush;
            layer.SubScale = LyricSubScale;
            layer.FontFamily = UiFontFamily;
            layer.MaxTextWidth = LyricMaxWidth;
        }

        // 柱状图同样是自绘的。两档亮度：有值的柱子亮，空时段只留一条暗底座 ——
        // 底座让横轴连得起来，否则"没有降水"的时段整段消失，读不出雨从哪一格开始
        Chart.BarBrush = CreateFrozenBrush(0xF2, 0xFF, 0xFF, 0xFF);
        Chart.FloorBrush = CreateFrozenBrush(0x33, 0xFF, 0xFF, 0xFF);

        // 封面缩略图尺寸固定，G3 圆角只需生成一次
        CompactArt.Clip = Squircle.Build(new Rect(0, 0, CompactArtSize, CompactArtSize), 9);
        ExpandedArt.Clip = Squircle.Build(new Rect(0, 0, ExpandedArtSize, ExpandedArtSize), 22);

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!_hovering) Collapse();
        };

#if DEBUG
        StartLyricBeat();
#endif

        // 进度独立于内容转场刷新，避免每 500ms 重播一次入场动画
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _progressTimer.Tick += (_, _) =>
        {
            RefreshProgress();

            // 音乐刚开始播放时渲染循环可能已停掉，这里把它唤起来开始律动
            if (!_renderHooked && IsPulsing) HookRender();
        };

        // 置顶权巡检 + 全屏让位
        _guardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _guardTimer.Tick += (_, _) => RunGuards();

        Loaded += OnLoaded;
        SourceInitialized += (_, _) =>
        {
            WindowInterop.MakeOverlay(this);
            RegisterHotKeys();
        };
        DpiChanged += (_, _) => Reposition();

        WireInteractions();
    }

    // ================= 全局热键 =================

    private const int HotkeyToggleExpand = 0xA101;
    private const int HotkeyToggleDot = 0xA102;

    /// <summary>
    /// 热键候选，按序尝试。全局热键在不同机器上被占用的情况差别很大
    /// （显卡驱动、输入法、录屏工具都会抢），所以必须有回退，而不是硬编码一组就完事。
    /// </summary>
    private static readonly (uint Mod, uint Vk, string Label)[] ExpandCandidates =
    [
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x49, "Ctrl+Alt+I"),
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x49, "Ctrl+Shift+I"),
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x59, "Ctrl+Alt+Y"),
    ];

    private static readonly (uint Mod, uint Vk, string Label)[] DotCandidates =
    [
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x44, "Ctrl+Alt+D"),
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_ALT, 0x4A, "Ctrl+Alt+J"),
        (NativeMethods.MOD_CONTROL | NativeMethods.MOD_SHIFT, 0x44, "Ctrl+Shift+D"),
    ];

    private HwndSource? _hwndSource;

    /// <summary>实际生效的热键组合，供托盘菜单展示；注册全失败时为 null。</summary>
    public string? ExpandHotkeyLabel { get; private set; }
    public string? DotHotkeyLabel { get; private set; }

    /// <summary>
    /// 用户在配置里指定的热键文本。设了就优先用它，注册失败再回落到内置候选 ——
    /// 不静默忽略用户的选择，也不因为它无效就彻底没有热键。
    /// </summary>
    public string? PreferredExpandHotkey { get; set; }
    public string? PreferredDotHotkey { get; set; }

    /// <summary>
    /// 注册展开与灵动点模式的全局热键。
    /// 灵动点态只有 8px 高，没有键盘途径的话几乎无法唤回。
    /// </summary>
    private void RegisterHotKeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(OnWndProc);

        ExpandHotkeyLabel = Register(hwnd, HotkeyToggleExpand, PreferredExpandHotkey, ExpandCandidates);
        DotHotkeyLabel = Register(hwnd, HotkeyToggleDot, PreferredDotHotkey, DotCandidates);

#if DEBUG
        _hotkeyStatus = $"I={ExpandHotkeyLabel ?? "FAIL"},D={DotHotkeyLabel ?? "FAIL"}";
#endif
    }

    /// <summary>
    /// 先试用户配置的组合，再退回内置候选。
    /// 用户填了但注册不上（被占用或写错）时不静默作废，仍给一组能用的。
    /// </summary>
    private static string? Register(
        IntPtr hwnd, int id, string? preferred, (uint Mod, uint Vk, string Label)[] candidates)
    {
        if (Core.Hotkey.TryParse(preferred, out var mod, out var vk, out var label)
            && NativeMethods.RegisterHotKey(hwnd, id, mod, vk))
        {
            return label;
        }

        return TryRegisterFirst(hwnd, id, candidates);
    }

    /// <summary>
    /// 热键真的被按到时触发。
    ///
    /// 设置页需要它，而且这个需求不是锦上添花：<c>RegisterHotKey</c>
    /// **报成功不代表按下去有反应** —— 被低级键盘钩子截住的组合注册照样成功
    /// （本机 Ctrl+Alt+D 与 Ctrl+Alt+J 都是这样）。所以设置页只能让用户
    /// 当场按一下、看它有没有响，光看注册返回值等于什么都没验。
    /// </summary>
    public event Action<HotkeyKind>? HotkeyFired;

    /// <summary>
    /// 运行时换一组热键。成功返回生效的标签，失败返回 null 且**保持原来那组不变** ——
    /// 换不上就把旧的也弄丢，用户会连唤回岛体的途径都没有。
    /// </summary>
    public string? TrySetHotkey(HotkeyKind kind, string? text)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return null;

        if (!Core.Hotkey.TryParse(text, out var mod, out var vk, out var label)) return null;

        var id = kind == HotkeyKind.Expand ? HotkeyToggleExpand : HotkeyToggleDot;
        var old = kind == HotkeyKind.Expand ? ExpandHotkeyLabel : DotHotkeyLabel;

        // 必须先退订再注册：同一个 id 重复注册会失败，而不是覆盖
        NativeMethods.UnregisterHotKey(hwnd, id);

        if (!NativeMethods.RegisterHotKey(hwnd, id, mod, vk))
        {
            // 新的注册不上，把旧的那组恢复回去
            if (Core.Hotkey.TryParse(old, out var oldMod, out var oldVk, out _))
                NativeMethods.RegisterHotKey(hwnd, id, oldMod, oldVk);

            return null;
        }

        if (kind == HotkeyKind.Expand) ExpandHotkeyLabel = label;
        else DotHotkeyLabel = label;

#if DEBUG
        _hotkeyStatus = $"I={ExpandHotkeyLabel ?? "FAIL"},D={DotHotkeyLabel ?? "FAIL"}";
#endif
        return label;
    }

    /// <summary>依次尝试候选组合，返回第一个注册成功的标签；全部失败返回 null。</summary>
    private static string? TryRegisterFirst(
        IntPtr hwnd, int id, (uint Mod, uint Vk, string Label)[] candidates)
    {
        foreach (var (mod, vk, label) in candidates)
        {
            if (NativeMethods.RegisterHotKey(hwnd, id, mod, vk)) return label;
        }

        // 全部被占用时静默降级 —— 托盘菜单仍然可用
        return null;
    }

#if DEBUG
    private string _hotkeyStatus = "?";
    private int _hotkeyHits;

#if DEBUG
    /// <summary>岛体收到右键的次数。用来区分"事件没到"与"到了但没生效"。</summary>
    private int _rightClicks;
#endif

    // 几何构建耗时的移动平均，用于区分"几何构建成本"与"渲染管线成本"
    private readonly Stopwatch _geoClock = new();
    private double _geoAvgMs;
    private long _geoFrames;

    /// <summary>内容转场发生次数：一次切歌若触发两次，说明标题与封面不是原子更新的。</summary>
    private int _swapCount;

    /// <summary>渲染帧数。与 _geoFrames（几何重建次数）对照，可看出脏检查是否真的跳过了重建。</summary>
    private long _renderFrames;

    /// <summary>最近一次 WM_NCHITTEST 命中的元素与窗口内坐标。</summary>
    private string _lastHit = "-";

    // 最近一次形变的帧间隔统计
    private bool _morphActive;
    private int _morphFrames;
    private double _morphMs, _morphMaxDt;
    private int _morphGen0;

    // 尾段（亚像素回弹，刻意半帧率）单独统计
    private int _tailFrames;
    private double _tailMs;

    /// <summary>最近一次算出的分频除数。</summary>
    private int _lastDivisor;

    /// <summary>探针上次刷新的时刻，用于限流。</summary>
    private long _probeTicks;

    /// <summary>最近一次切歌转场起步时的「旧层/新层」透明度。</summary>
    private string _swapStart = "-";

    /// <summary>换句次数，以及起步那一帧两层的透明度（进场/退场）。</summary>
    private int _lyricSwitches;
    private string _lyricStart = "-";
#endif

    private void UnregisterHotKeys()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            NativeMethods.UnregisterHotKey(hwnd, HotkeyToggleExpand);
            NativeMethods.UnregisterHotKey(hwnd, HotkeyToggleDot);
        }

        _hwndSource?.RemoveHook(OnWndProc);
        _hwndSource = null;
    }

    private IntPtr OnWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            // 分层窗口按像素 alpha 决定鼠标是否穿透：alpha==0 系统直接放行、连这条消息
            // 都不发；alpha>0 就把消息投给本窗口。感应区那块 #01FFFFFF 正属后者 ——
            // 它肉眼不可见，却会在非灵动点态把整片矩形上的点击吞掉（既不响应也不下传）。
            // 所以这里自己回答：WPF 命中测试找不到元素的点，一律让给下方窗口。
            var raw = lParam.ToInt64();
            var point = new NativeMethods.POINT
            {
                X = (short)(raw & 0xFFFF),
                Y = (short)((raw >> 16) & 0xFFFF),
            };

            try
            {
                // 两步换算：屏幕物理 → 客户区物理 → DIP。
                // 直接用 PointFromScreen 在 PerMonitorV2 下会漏掉 DPI 缩放，
                // 算出的点偏到窗口外，命中判定于是整个错位。
                if (!NativeMethods.ScreenToClient(hwnd, ref point)) return IntPtr.Zero;
                if (_hwndSource?.CompositionTarget is not { } target) return IntPtr.Zero;

                var local = target.TransformFromDevice.Transform(new Point(point.X, point.Y));
                var hit = InputHitTest(local);
#if DEBUG
                _lastHit = $"{hit?.GetType().Name ?? "null"}@{local.X:F0},{local.Y:F0}";

                // 就地刷新探针。命中测试在弹簧静止之后照常工作，而探针平时挂在
                // 渲染回调上 —— 静止时回调已被摘除，标题就停在最后一帧的值上。
                // 拿那份陈旧的 box 去判定当前的命中结果，等于拿旧尺子量新东西。
                UpdateDebugProbe(_width.Current, _height.Current, _radius.Current, force: true);
#endif

                if (hit is null)
                {
                    handled = true;
                    return NativeMethods.HTTRANSPARENT;
                }
            }
            catch
            {
                // 布局尚未就绪时不干预，交给默认处理
            }

            return IntPtr.Zero;
        }

        if (msg != NativeMethods.WM_HOTKEY) return IntPtr.Zero;

#if DEBUG
        _hotkeyHits++;
#endif

        switch (wParam.ToInt32())
        {
            case HotkeyToggleExpand:
                ToggleExpanded();
                HotkeyFired?.Invoke(HotkeyKind.Expand);
                handled = true;
                break;

            case HotkeyToggleDot:
                SetDotMode(!_dotMode);
                HotkeyFired?.Invoke(HotkeyKind.Dot);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Reposition();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _progressTimer.Start();
        _guardTimer.Start();

        ApplyState(animate: false);
        ApplyVisuals();
    }

    protected override void OnClosed(EventArgs e)
    {
        UnregisterHotKeys();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _progressTimer.Stop();
        _guardTimer.Stop();
        _collapseTimer.Stop();
        UnhookRender();
        base.OnClosed(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Reposition();

    /// <summary>顶部居中。MVP 只锚定主显示器，多屏策略在 M5 补。</summary>
    private void Reposition()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private void RunGuards()
    {
        if (WindowInterop.ShouldYieldToFullscreen())
        {
            if (Visibility == Visibility.Visible) Visibility = Visibility.Hidden;
            return;
        }

        if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
        WindowInterop.ReassertTopmost(this);
    }

    // ================= 活动绑定 =================

    /// <summary>接入音频能量源，频谱条据此随音乐律动。不接也能正常工作，只是频谱条不动。</summary>
    public void BindAudio(AudioPulse audio) => _audio = audio;

    public void BindScheduler(ActivityScheduler scheduler)
    {
        _scheduler = scheduler;
        scheduler.WinnerChanged += set =>
            Dispatcher.InvokeAsync(() => SetActivitySet(set), DispatcherPriority.Normal);
    }

    private void SetActivitySet(ActivitySet set)
    {
        Diag.Log($"ui: SetActivitySet 主={set.Main?.ProviderId ?? "-"}");

        // 瞬时事件只改右侧那个圆，不参与主体的交叉淡化 ——
        // 让它走 SetActivity 的话，插一次电就会把正在播的歌当成"切歌"重播一次转场
        _hasTransient = set.Transient is not null;

        // 撤下时不清 _transient：圆还要缩回去，那期间仍得照着它画
        if (set.Transient is not null)
        {
            var changed = _transient?.Id != set.Transient.Id
                || _transient?.Title != set.Transient.Title;

            _transient = set.Transient;
            if (changed) SplitGlyph.Text = _transient.Glyph;
        }

        SetActivity(set.Main);
    }

    private void SetActivity(IslandActivity? activity)
    {
        // 切歌（同一个活动但内容换了）要交叉淡化，不能硬切。
        // 形变本身有自己的动画，不走这条路径。
        //
        // 判据**只看文字，不看封面**：封面要异步取回再解码，总比标题晚到几百毫秒
        // （MediaSessionProvider 还会追几轮）。把它算进来的话一次切歌会转场两次 ——
        // 先文字一次，封面到了又一次，而第二次文字明明没变却跟着重新淡入，
        // 看到的就是"信息闪一下"。封面单独换 Source 即可，它本来就是后到的。
        var contentChanged = _activity is not null
            && activity is not null
            && (_activity.Title != activity.Title
                || _activity.Subtitle != activity.Subtitle);

        if (contentChanged)
        {
            CaptureContentSnapshot();

            // 换歌：上一首的歌词要从两层里清掉，否则新歌前奏期间旧词还挂在那儿。
            // 清 _lyricText 也顺带保证新歌第一句一定触发进场动画 ——
            // 万一它和上一首最后一句文本相同（"啦啦啦"之类），不清就判定成"没换句"。
            _lyricText = "";
            _lyricSlotFloor = 0;

            _lyricSub = null;

            var ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            LyricA.SetText("", null, LyricFontSize, ppd);
            LyricB.SetText("", null, LyricFontSize, ppd);
#if DEBUG
            _swapCount++;
#endif
        }

        _activity = activity;
        var lyricChanged = false;

        if (activity is not null)
        {
            // 歌词接管文本槽：开歌词后歌名只在换歌时站一会儿就整块让位，
            // 位置交给"现在唱的这句"—— 同一个槽位里它的信息量远大于曲名+艺人。
            // 让位的时机由 Provider 决定（换歌后 TitleHoldMs 内不给歌词），
            // 前奏、间奏未到下一句、歌词还没拉到时 Lyric 为 null，歌名与艺人自动回来。
            var line = string.IsNullOrWhiteSpace(activity.Lyric) ? null : activity.Lyric.Trim();
#if DEBUG
            // 压测模式下歌词一律由节拍器给，忽略 Provider 的那句 —— 否则每 110ms
            // 推来的真实歌词会和节拍器互相打断，换句次数又不受控了
            if (LyricBeatMs > 0) line = BeatSamples[_beatIndex % BeatSamples.Length];
#endif

            CompactTitle.Text = activity.Title;
            CompactSubtitle.Text = activity.Subtitle ?? "";

            // 给歌名一个上限，它才会真的截成省略号。
            // TextTrimming 只在**测量被约束**时生效，而横向 StackPanel 给孩子的是无限宽，
            // 所以光在 XAML 里写 TextTrimming 是不够的 —— 超长曲名会直接溢出去压到图标上。
            // 剩余额度 = 文本槽宽 − 副标题实占（副标题自己有 MaxWidth=240 兜底）
            //
            // 只在换内容时算：开着歌词是每 110ms 走一遍这里的，
            // 而副标题不变时这次 MeasureText 纯属白做（构造 FormattedText 不是免费的）
            if (contentChanged)
            {
                var subtitleTake = string.IsNullOrEmpty(activity.Subtitle)
                    ? 0
                    : CompactSubGap + Math.Min(CompactSubtitleMaxWidth, MeasureText(
                        activity.Subtitle, 12, FontWeights.Normal, VisualTreeHelper.GetDpi(this).PixelsPerDip));

                // +2 是防截断余量。MeasureText 本身已经故意多报 1px（见它的注释：
                // FormattedText 的小数宽被 UseLayoutRounding 抹掉后会误触发 TextTrimming），
                // 这里再留一点，免得"刚好放得下"的标题被自己的上限截出个省略号
                CompactTitle.MaxWidth = Math.Max(60, TextSlotWidth - subtitleTake + 2);
            }

            var sub = string.IsNullOrWhiteSpace(activity.LyricSub) ? null : activity.LyricSub.Trim();
#if DEBUG
            if (LyricBeatMs > 0 && line is not null) sub = BeatSubs[_beatIndex % BeatSubs.Length];
#endif

            // 第二行变了也算换句 —— 否则原文相同、只有译文变的那一句不会重绘
            lyricChanged = line is not null && (line != _lyricText || sub != _lyricSub);
            if (lyricChanged)
            {
                // 旧句的宽度取**它自己那一层实际排版出来的**宽度，而不是再 MeasureText 一遍：
                // 同一套排版才不会差那零点几像素，而零点几像素足以触发截断（见 MeasureText）
                _lyricSlotFloor = LyricIn.TextWidth > 0 ? Math.Ceiling(LyricIn.TextWidth) + 1 : 0;

                // 双层轮换：新句装进空闲那层，旧句留在原层负责退场。
                // 单层做不到 —— 换 Text 是瞬间的，换完就没有旧句可退了。
                _lyricUseB = !_lyricUseB;
                LyricIn.SetText(line!, sub, LyricFontSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                _lyricText = line!;
                _lyricSub = sub;

                _lyricSwap.SnapTo(0);
                _lyricSwap.SetTarget(1);
            }
#if DEBUG
            if (lyricChanged) _lyricSwitches++;
#endif

            _titleHold.SetTarget(line is not null ? 1 : 0);

            // 收起态优先显示封面缩略图，没有封面才回落为字形图标
            CompactArtwork.Source = activity.Artwork;
            CompactGlyph.Text = activity.Glyph;
            var noArt = activity.Artwork is null;
            CompactGlyph.Visibility = noArt ? Visibility.Visible : Visibility.Collapsed;
            CompactArtBackdrop.Visibility = noArt ? Visibility.Visible : Visibility.Collapsed;

            ExpandedTitle.Text = activity.Title;
            ExpandedSubtitle.Text = activity.Subtitle ?? "";

            ExpandedArtwork.Source = activity.Artwork;
            ArtworkFallback.Text = activity.Glyph;
            ArtworkFallback.Visibility = noArt ? Visibility.Visible : Visibility.Collapsed;

            TransportPanel.Visibility = activity.HasTransportControls ? Visibility.Visible : Visibility.Collapsed;
            PlayPauseGlyph.Text = activity.IsPlaying ? "\uE769" : "\uE768";  // 暂停 / 播放

            ProgressTrack.Visibility = activity.Progress is null ? Visibility.Collapsed : Visibility.Visible;

            // 柱状图：有数据才占位。Visibility 是 AffectsMeasure，会触发一次布局 ——
            // 这里可以接受（活动切换本来就要重新布局），但绝不能挪到每帧的路径上
            var hasChart = activity.Chart is { Count: > 0 };
            ChartPanel.Visibility = hasChart ? Visibility.Visible : Visibility.Collapsed;

            if (hasChart)
            {
                Chart.HighlightFrom = activity.ChartFrom;
                Chart.HighlightTo = activity.ChartTo;
                Chart.SetValues(activity.Chart);
                ChartCaption.Text = activity.ChartCaption ?? "";
                ChartCaption.Visibility = string.IsNullOrEmpty(activity.ChartCaption)
                    ? Visibility.Collapsed
                    : Visibility.Visible;
            }

            var showSpectrum = ShowSpectrumFor(activity);
            Spectrum.Visibility = showSpectrum ? Visibility.Visible : Visibility.Collapsed;
            Spectrum.Width = SpectrumBars.WidthFor(AudioPulse.BandCount);
            if (showSpectrum) Spectrum.SetAccent(activity.AccentColor);

            // 只算岛体该有多宽。内容的**布局**不再跟着变 ——
            // 文本槽宽度恒定且居中，封面与频谱靠 Transform 每帧钉到岛体两端，
            // 于是换句一次布局都不触发（见 ApplyVisuals 末尾）。
            _pillWidth = MeasurePillWidth(activity, line, line is null ? null : sub);

            _expandedWidth = MeasureExpandedWidth(activity);
            ExpandedContent.Width = _expandedWidth;
        }
        else
        {
            // 活动消失时若正展开，先收起再变为灵动点
            _expanded = false;
        }

        RefreshProgress();

        if (contentChanged && _snapshotActive)
        {
            // 新内容已就位，从"完全是旧快照"开始推向"完全是新内容"
            _contentSwap.SnapTo(0);
            _contentSwap.SetTarget(1);

            // **必须当场生效，不能等下一帧的 ApplyVisuals**：
            // 此刻文本已经换成新的，而两层的 Opacity 都还是上次转场收尾时留下的 1。
            // 晚一帧应用，这一帧就会把旧快照和新内容**同时全亮**地叠在一起 ——
            // 两份文字重影一闪，正是"切歌时信息闪一下"的来源。
            ApplyContentSwap();
            HookRender();
#if DEBUG
            // 转场起步瞬间两层的透明度。正确值是 1.00/0.00（旧的全亮、新的全暗）；
            // 若读到 1.00/1.00 就说明新内容没被压住，会闪一帧重影
            _swapStart = $"{TransitionLayer.Opacity:F2}/{ContentLayers.Opacity:F2}";
#endif
        }

        if (lyricChanged || !_titleHold.IsAtRest)
        {
            // 同 ApplyContentSwap 的道理，也是同一个坑：此刻新句的文本已经就位，
            // 而两层的 Opacity/位移还是上次换句收尾时留下的值（都是"就位态"）。
            // 晚一帧应用，这一帧就会把新旧两句**同时全亮**地叠在一起闪过去。
            ApplyLyricVisuals();
            HookRender();
#if DEBUG
            // 换句起步瞬间两层的透明度。正确值是 0.00/1.00（新句全暗、旧句全亮）；
            // 读到 1.00/1.00 就是新句没被压住，会闪一帧重影 —— 与 _swapStart 同一个坑。
            if (lyricChanged) _lyricStart = $"{LyricIn.Opacity:F2}/{LyricOut.Opacity:F2}";
#endif
        }

        ApplyState(animate: true);
    }

    /// <summary>
    /// 把当前内容渲染成一张位图，作为交叉淡化的"旧的那一半"。
    /// 用快照而不是第二套控件树：常态零开销，代价只是切歌时一次约 1–3ms 的软件渲染。
    /// </summary>
    private void CaptureContentSnapshot()
    {
        // 上一次转场还没结束就又切歌了，直接硬接下一次，不叠加快照
        if (_snapshotActive) return;

        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var bitmap = new RenderTargetBitmap(
                (int)Math.Ceiling(BodyCanvasWidth * dpi.DpiScaleX),
                (int)Math.Ceiling(BodyCanvasHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX,
                96 * dpi.DpiScaleY,
                PixelFormats.Pbgra32);

            bitmap.Render(ContentLayers);
            bitmap.Freeze();

            TransitionLayer.Source = bitmap;
            TransitionLayer.Visibility = Visibility.Visible;
            _snapshotActive = true;
        }
        catch (Exception ex)
        {
            // 快照失败就退化成硬切，不能因为转场把内容更新弄丢
            System.Diagnostics.Debug.WriteLine($"[island] 内容快照失败: {ex.Message}");
            _snapshotActive = false;
        }
    }

    /// <summary>
    /// 把交叉淡化的当前进度铺到两层上：旧快照淡出并上移，新内容淡入并从下方就位。
    ///
    /// 两条曲线的重叠窗口是调出来的，两个方向都会翻车：
    ///
    /// - **重叠太宽**（最初：旧的 ×1.5 快退、新的延后 0.25，窗口 [0.25, 1]）：
    ///   中途两层各有五成不透明度、位置又只差 7px，糊成一团重影。
    /// - **重叠太窄**（[0.45, 0.55]，两层透明度都不到 0.12）：重影是没了，
    ///   但中点岛体几乎全空 —— 空档本身就是一次闪。
    ///
    /// 落在 [0.32, 0.68]：中点两层各约 0.4，**位置相距约 18px**（近一行的高度），
    /// 靠错位而不是靠"不同时出现"来避免糊；同时任何时刻都至少有一层在 0.3 以上，
    /// 不会出现空档。<see cref="SwapShift"/> 是这个平衡的前提，调小了就只能退回窄窗口。
    ///
    /// 单独成方法是因为它有两个调用点：每帧的 ApplyVisuals，
    /// 以及转场刚起步时的 SetActivity —— 后者不能等下一帧，否则会闪一帧重影。
    /// </summary>
    private void ApplyContentSwap()
    {
        var swap = Math.Clamp(_contentSwap.Current, 0, 1);
        var fadeOut = Math.Clamp((0.68 - swap) / 0.45, 0, 1);
        var fadeIn = Math.Clamp((swap - 0.32) / 0.55, 0, 1);

        TransitionLayer.Opacity = fadeOut;
        TransitionShift.Y = (1 - fadeOut) * -SwapShift;
        ContentLayers.Opacity = fadeIn;
        ContentLayersShift.Y = (1 - fadeIn) * SwapShift;
    }

    private void EndContentTransition()
    {
        _snapshotActive = false;
        TransitionLayer.Visibility = Visibility.Collapsed;
        TransitionLayer.Source = null;
        TransitionLayer.Opacity = 1;
        TransitionShift.Y = 0;
        ContentLayers.Opacity = 1;
        ContentLayersShift.Y = 0;
    }

    /// <summary>
    /// 歌词的两级动画：外层是「歌名组 ↔ 歌词组」的让位，内层是歌词自己的逐句换行。
    ///
    /// 逐句换行的四个量（位移 / 缩放 / 透明度 / 模糊）全部由同一个弹簧
    /// <see cref="_lyricSwap"/> 驱动，但**各自取不同的映射**：
    ///
    /// - 位移与缩放用弹簧的**原始值**（可以 &gt;1），过冲因此真的表现出来；
    /// - 透明度用 clamp 后的值，且分母不同（进场 0.7、退场 0.55）——
    ///   退场淡得更快，两句才不会在中途一起半亮地糊成一团；
    /// - 模糊只在 raw &lt; 1 的区间有值，过冲段已经是清晰的了。
    ///
    /// 退场向上 13px、进场从下方 7px 上来，两句最远相距 20px（约一行高度）。
    /// 这个"靠错开而不是靠不重叠"的做法是从切歌转场那次学到的：
    /// 一味压窄重叠窗口会换来一段两层都近乎透明的空档，而空档本身就是一次闪。
    /// </summary>
    /// <summary>正在进场（当前显示）的歌词层。</summary>
    private LyricLayer LyricIn => _lyricUseB ? LyricB : LyricA;

    /// <summary>正在退场的歌词层。</summary>
    private LyricLayer LyricOut => _lyricUseB ? LyricA : LyricB;

    private void ApplyLyricVisuals()
    {
        var hold = Math.Clamp(_titleHold.Current, 0, 1);
        TitleGroup.Opacity = 1 - hold;
        LyricGroup.Opacity = hold;

        // 歌词整组不可见时不必算逐句动画，顺手把模糊归零 ——
        // 留着的话下次歌词回来时会先闪一帧上次残留的模糊
        if (hold <= 0.001)
        {
            LyricA.SetBlur(0);
            LyricB.SetBlur(0);
            return;
        }

        var raw = _lyricSwap.Current;          // 可 >1：过冲留给位移
        var t = Math.Clamp(raw, 0, 1);

        var inIsB = _lyricUseB;
        var inLayer = inIsB ? LyricB : LyricA;
        var outLayer = inIsB ? LyricA : LyricB;

        // 进场：从下方 10px 上来、由糊转清、线性淡入。
        // 位移用 raw 而不是 t —— 过冲（约 9%）让它掠过终点约 0.9px 再落回，
        // 那一下"顶"是全部的弹性感来源；透明度与模糊用 clamp 后的值，
        // 冲过头再退回来会是肉眼可见的闪。
        // ISLANDX_SKIP=blur 只摘模糊、位移与透明度照旧 —— 这是唯一能单独量出
        // 「模糊值不值这个钱」的办法：三个量同时在跑，光看总 CPU 分不出是谁花的
        var sigma = Skip("blur") ? 0 : LyricBlurSigma;

        inLayer.Opacity = t;
        (inIsB ? LyricBShift : LyricAShift).Y = (1 - raw) * LyricShift;
        inLayer.SetBlur((1 - t) * sigma);

        // 退场：向上飘 10px、越飘越糊、线性淡出。
        // 与进场**严格互补**，不错峰 —— 两层是同一位置的连续歌词，
        // 靠"一个在糊掉、一个在变清"就足以区分。
        var outAlpha = 1 - t;
        outLayer.Opacity = outAlpha;
        (inIsB ? LyricAShift : LyricBShift).Y = -t * LyricShift;

        // 淡尽后把 sigma 也归零。LyricLayer 的 OnRender 已经会在整层透明时短路，
        // 所以这不是为了省开销，是为了**探针读数不骗人** ——
        // 停在 @6.0 会让人以为动画还没走完，而那时它早已一片都不画了
        outLayer.SetBlur(outAlpha <= 0.004 ? 0 : t * sigma);
    }

    /// <summary>
    /// 旧句淡尽之后，把**岛体**宽度从 <c>max(旧句, 新句)</c> 收到新句的实际长度。
    ///
    /// 这就是"旧句长、新句短"时看到的两段式收窄：先等旧句淡完（宽度不动），再收拢。
    /// 反过来立刻收窄的话，还在场上的旧句两端会被正在收拢的岛体裁掉。
    /// 只动岛体目标宽度，内容布局全程不变（文本槽宽度恒定、两侧靠 Transform 定位）。
    /// </summary>
    private void ReleaseSlotFloor()
    {
        _lyricSlotFloor = 0;

        if (_activity is null) return;

        // 此刻显示的是歌词还是歌名，看让位弹簧的目标而不是当前值 ——
        // 当前值可能还在路上，而目标才是"该显示谁"的结论
        var line = _titleHold.Target > 0.5 && _lyricText.Length > 0 ? _lyricText : null;

        _pillWidth = MeasurePillWidth(_activity, line, line is null ? null : _lyricSub);
        ApplyState(animate: true);
    }

    /// <summary>
    /// 收起态两侧被固定占掉的宽度：左边永远是封面（或回落的字形图标），
    /// 右边是内边距、有媒体时还有频谱。文本只能用中间剩下的那段。
    ///
    /// 单独摘出来，是因为**胶囊宽度和文本槽的偏移必须由同一组数算出来**。
    /// 这两处一旦各算各的就会错位，而错位的表现是文字压到图标上 ——
    /// 恰恰是本项目栽过的那个 bug（详见 <see cref="CompactTextShift"/> 的更新处）。
    /// </summary>
    private (double Left, double Right) CompactSides(IslandActivity activity)
    {
        var left = CompactPadLeft + CompactArtSize + CompactGap;

        // 频谱条常驻占位（静止时是一排小圆点），否则它一出现胶囊就会突然变宽
        var right = CompactPadRight;
        if (ShowSpectrumFor(activity))
            right += SpectrumGap + SpectrumBars.WidthFor(AudioPulse.BandCount);

        return (left, right);
    }

    /// <summary>
    /// 按实际文本宽度算出收起态胶囊该有多宽 —— 长标题撑开、短标题收窄，
    /// 宽度跟着信息量走，而不是把所有内容都塞进一个固定宽度里。
    ///
    /// 左右相加，两侧**各按自己的实际占用**留白（不是取较大者对称留白）。
    /// 对称留白能避开图标，但会在没有频谱时于右边空出一整个封面的宽度 ——
    /// 图标贴着左缘、右边空 44px，整条看着往左歪。
    /// 正确的做法是让**内容整体**居中，而不是让文本在胶囊里居中：
    /// 文本槽因此要相应偏移，见 <see cref="ApplyVisuals"/> 里的 CompactTextShift。
    /// </summary>
    private double MeasurePillWidth(IslandActivity activity, string? line, string? sub)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var textWidth = MeasureTextSlot(activity, line, sub, pixelsPerDip);

        var (left, right) = CompactSides(activity);
        return Math.Clamp(Math.Ceiling(left + textWidth + right), PillMinWidth, PillMaxWidth);
    }

    /// <summary>
    /// 文本槽该有多宽 —— 歌词模式下**只按当前这句歌词算**，否则按「歌名 + 艺人」算。
    ///
    /// 歌词一句长一句短，所以胶囊会跟着逐句伸缩。这是刻意的：宽度跟着内容走
    /// 才有"流体"的意思。之所以不难看，是因为宽度走的是 <see cref="_width"/> 弹簧，
    /// 而新句是在岛体张开的过程中淡入的 —— 连拍看得到短句换长句时新句左右两端
    /// 确实被还没张开的岛体裁掉一点，但那一帧它的不透明度只有 0.3 上下，
    /// 淡到基本看不出来。不是"没有中间态"，是中间态恰好落在最淡的时候。
    ///
    /// 上一版给歌词留的是固定预算（190px，整首歌宽度不变）。那样确实稳，
    /// 但胶囊与歌词长度完全脱钩，短句时右边空一大块，长句又被截断。
    /// </summary>
    /// <param name="line">
    /// 此刻**实际显示**的那句歌词（已 trim），null 表示回落到歌名。
    /// 显式传入而不是从 <paramref name="activity"/> 里取：压测节拍器会替换掉歌词，
    /// 两处各自判断的话宽度就和屏上的字对不上了。
    /// </param>
    /// <param name="sub">
    /// 第二行（译文），null 表示单行。同样显式传入，理由与 <paramref name="line"/> 相同。
    /// </param>
    private double MeasureTextSlot(IslandActivity activity, string? line, string? sub, double pixelsPerDip)
    {
        if (line is not null)
        {
            var lyricWidth = MeasureText(line, LyricFontSize, FontWeights.Normal, pixelsPerDip);

            // 译文常常比原文长（英译中尤其），只按原文算的话译文会溢出岛体两端被裁掉。
            // 字号要带上 LyricSubScale —— 渲染层就是按这个比例排第二行的
            if (sub is not null)
            {
                lyricWidth = Math.Max(lyricWidth, MeasureText(
                    sub, LyricFontSize * LyricSubScale, FontWeights.Normal, pixelsPerDip));
            }

            // 下限是上一句的宽度，直到它飘走为止（见 _lyricSlotFloor）
            return Math.Max(_lyricSlotFloor, Math.Min(LyricMaxWidth, lyricWidth));
        }

        var width = MeasureText(activity.Title, 15, FontWeights.Medium, pixelsPerDip);

        if (!string.IsNullOrEmpty(activity.Subtitle))
        {
            // 要按**截断后**的宽度算：CompactSubtitle 在 XAML 里有 MaxWidth=240，
            // 量未截断的宽度会把胶囊撑得比内容宽 —— 剪贴板预览最长 28 字，
            // 12px 下约 336px，右边就白空出 96px。
            // 又一次"量的模型和画的模型不一致"，和图标重叠那次同一个病根。
            width += CompactSubGap + Math.Min(
                CompactSubtitleMaxWidth,
                MeasureText(activity.Subtitle, 12, FontWeights.Normal, pixelsPerDip));
        }

        // 和歌词那一路一样要有上限。歌词有 LyricMaxWidth 兜着，歌名以前没有 ——
        // 超长曲名会把 total 顶过 PillMaxWidth，胶囊被钳住而文本照旧按原宽居中，
        // 于是又从左边压进图标里（和预警那个 bug 同一个病，只是触发条件更少见）。
        // 上限取文本槽自己的宽度：再宽也放不下，CompactTitle 会截成省略号。
        return Math.Min(TextSlotWidth, width);
    }

    /// <summary>
    /// 文本槽的宽度，与 XAML 里 <c>CompactTextSlot</c> 的 Width 一致。
    /// 超过它的文本无论如何都会被截，所以宽度计算也不该按超出的部分去撑胶囊。
    /// </summary>
    private const double TextSlotWidth = 340;

    /// <summary>副标题的宽度上限，与 XAML 里 <c>CompactSubtitle</c> 的 MaxWidth 一致。</summary>
    private const double CompactSubtitleMaxWidth = 240;

    /// <summary>只有能播放的媒体活动才配频谱条；时钟之类的没有意义。</summary>
    private bool ShowSpectrumFor(IslandActivity activity) =>
        activity.HasTransportControls && _audio is { IsAvailable: true };

    /// <summary>展开态同样按标题实测宽度撑开，否则长曲名会被固定宽度切掉。</summary>
    private double MeasureExpandedWidth(IslandActivity activity)
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var textWidth = Math.Max(
            MeasureText(activity.Title, 17, FontWeights.SemiBold, pixelsPerDip),
            MeasureText(activity.Subtitle ?? "", 12, FontWeights.Normal, pixelsPerDip));

        var total = ExpandedPad + ExpandedArtSize + ExpandedGap + textWidth + ExpandedPad;
        return Math.Clamp(Math.Ceiling(total), ExpandedMinWidth, ExpandedMaxWidth);
    }

    private static readonly FontFamily UiFontFamily = new("Microsoft YaHei UI, Segoe UI");

    /// <summary>
    /// 文本的排版宽度，**向上取整并留 1px 余量**。
    ///
    /// 余量不是"保险起见"：返回值被直接当作容器宽度用，而窗口开了
    /// <c>UseLayoutRounding</c>，布局尺寸会舍入到整像素。容器宽度恰好等于
    /// 文本宽度时（266.4 → 266），被舍掉的那点亚像素就足以触发 TextBlock 的
    /// TextTrimming —— 表现是一句明明放得下的歌词末尾莫名变成省略号，
    /// 而探针里的宽度数字看起来完全正确，从数字上根本查不出来。
    /// </summary>
    private static double MeasureText(string text, double fontSize, FontWeight weight, double pixelsPerDip)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(UiFontFamily, FontStyles.Normal, weight, FontStretches.Normal),
            fontSize,
            Brushes.White,
            pixelsPerDip);

        return Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 1;
    }

    private void RefreshProgress()
    {
        var progress = (_scheduler?.Peek() ?? _activity)?.Progress;

        if (progress is null)
        {
            ProgressFill.Width = 0;
        }
        else
        {
            var track = ProgressTrack.ActualWidth;
            if (track > 0) ProgressFill.Width = Math.Clamp(track * progress.Value, 0, track);
        }

#if DEBUG
        // 静止时渲染循环已摘除，进度探针需要在这里独立刷新。
        // 注意用弹簧当前值 —— IslandBody 的尺寸现在是恒定画布，不再代表岛体。
        UpdateDebugProbe(_width.Current, _height.Current, _radius.Current);
#endif
    }

    // ================= 交互 =================

    private void WireInteractions()
    {
        HoverZone.MouseEnter += (_, _) => OnPointerEnter();
        HoverZone.MouseLeave += (_, _) => OnPointerLeave();
        IslandBody.MouseEnter += (_, _) => OnPointerEnter();
        IslandBody.MouseLeave += (_, _) => OnPointerLeave();

        IslandBody.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ToggleExpanded();
        };

        // 右键弹工具栏。左键是展开／收起，两者互不干扰
        IslandBody.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
#if DEBUG
            _rightClicks++;
#endif
            // 这是"右键卡死"那条路径的入口。日志停在这一行之后的哪里，
            // 就说明卡在哪一步
            Diag.Log("=== 岛体收到右键 ===");
            Diag.Timed("整个右键处理", ToggleToolbar, warnMs: 100);
            Diag.Log("=== 右键处理返回 ===");
        };

        // 传输控件必须吞掉事件，否则冒泡到岛体会顺手把卡片收起
        PrevButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Invoke(OnPrevious); };
        PlayButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Invoke(OnTogglePlay); };
        NextButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Invoke(OnNext); };
    }

    private static void Invoke(Func<Task>? action)
    {
        if (action is null) return;
        _ = action();
    }

    private void OnPointerEnter()
    {
        _hovering = true;
        _collapseTimer.Stop();
        ApplyState(animate: true);
    }

    private void OnPointerLeave()
    {
        _hovering = false;

        // 展开态给一个宽限期，避免鼠标稍微滑出就立刻收起
        if (_expanded) _collapseTimer.Start();

        // 离开岛体同样要开始收工具栏。原先只有 Toolbar.MouseLeave 会启动它，
        // 于是"工具栏 → 岛体 → 移开"这条路上没人再启动过计时器，
        // 工具栏（和被钉住的视图）就一直留着 —— 而"指针离开岛体就还原"正是它的约定
        ScheduleToolbarClose();

        ApplyState(animate: true);
    }

    public void ToggleExpanded()
    {
        if (_activity is null) return;

        _expanded = !_expanded;
        _collapseTimer.Stop();
        ApplyState(animate: true);
    }

    public void Collapse()
    {
        if (!_expanded) return;
        _expanded = false;
        ApplyState(animate: true);
    }

    /// <summary>灵动点模式：不交互时缩为一条细横条，用于观察完整形变链路。</summary>
    public void SetDotMode(bool enabled)
    {
        _dotMode = enabled;
        if (enabled) _expanded = false;
        ApplyState(animate: true);
    }

    // ================= 状态机 =================

    private IslandVisualState ComputeState()
    {
        if (_activity is null) return IslandVisualState.Dot;
        if (_expanded) return IslandVisualState.Expanded;
        if (_dotMode && !_hovering) return IslandVisualState.Dot;
        return IslandVisualState.Pill;
    }

    private void ApplyState(bool animate)
    {
        var state = ComputeState();

        double targetWidth, targetHeight, targetRadius;

        switch (state)
        {
            case IslandVisualState.Expanded:
                targetWidth = _expandedWidth;
                targetHeight = ExpandedHeight;
                targetRadius = ExpandedRadius;
                break;

            case IslandVisualState.Pill:
                targetWidth = _hovering ? _pillWidth * PeekScale : _pillWidth;
                // 双行歌词要更高的胶囊。判据用**渲染层的实际状态**（LyricIn.HasSub）
                // 而不是活动字段：换句动画期间两层内容不同步，看活动会在
                // "旧句双行、新句单行"的那一瞬把高度提前改掉，形状抖一下
                var twoLine = _titleHold.Target > 0.5 && LyricIn.HasSub;
                targetHeight = twoLine ? PillHeightTwoLine : PillHeight;
                targetRadius = twoLine ? PillRadiusTwoLine : PillRadius;
                break;

            default:
                targetWidth = DotWidth;
                targetHeight = DotHeight;
                targetRadius = DotRadius;
                break;
        }

        ConfigureFeel(state, targetWidth, targetHeight);

        _width.SetTarget(targetWidth);
        _height.SetTarget(targetHeight);
        _radius.SetTarget(targetRadius);

        // Split 只在收起态成立：展开态是张卡片，右边挂个小圆很怪；
        // 灵动点态本就是"别打扰我"，更不该分裂出东西来
        var wantSplit = _hasTransient && state == IslandVisualState.Pill;

        // 进场要弹（ζ≈0.58，胀一下再收住），退场不该回弹 —— 那会像"缩回去又想再冒出来"。
        // 退场也走得更快：事件已经结束了，圆还在慢悠悠缩只是拖着人等。
        _split.Configure(wantSplit ? 300 : 340, wantSplit ? 20 : 34);
        _split.SetTarget(wantSplit ? 1 : 0);

        _compactOpacity.SetTarget(state == IslandVisualState.Pill ? 1 : 0);
        _expandedOpacity.SetTarget(state == IslandVisualState.Expanded ? 1 : 0);

        // 感应区只在灵动点态可命中。它是个矩形，而岛体是 G3 圆角 ——
        // 胶囊/卡片态留着它，四个圆角外那几片 alpha=1 的小三角就会吞掉点击。
        // 那两态的悬停由岛体自己的轮廓负责（IslandBody 已接 MouseEnter/Leave）。
        HoverZone.IsHitTestVisible = state == IslandVisualState.Dot;

        if (animate)
        {
            HookRender();
        }
        else
        {
            foreach (var spring in _allSprings) spring.SnapTo(spring.Target);
        }
    }

    /// <summary>
    /// 形变手感。三个维度**刻意不同步** —— 这是"流体"与"整体缩放"的分界线：
    /// 参数一致时形状全程是等比胶囊，每一帧都只是上一帧的放大版，看着硬；
    /// 错开之后形变中途会经过一个被拉伸或挤压的中间形态，那才是液体铺开的样子。
    ///
    /// 错峰方向跟着形变方向走：
    ///   变大 —— 宽度先冲出去，高度慢半拍跟上（水先铺开，再涌起来）
    ///   变小 —— 高度先塌下去，宽度慢半拍收拢
    ///            （反过来会在中途得到一个窄而高的方块，非常难看）
    ///
    /// 刚度决定快慢（ω = √k），阻尼比决定弹不弹：
    /// ζ = damping / (2√stiffness)，首次超调约 exp(-πζ/√(1-ζ²)) ——
    ///   0.55 → 13%    0.62 → 8%    0.70 → 5%    0.76 → 3%    0.88 → 1%
    /// 原先三个维度统一用 0.88，那 1% 肉眼根本看不出来，所以形变一直是"到位就停"。
    /// </summary>
    private void ConfigureFeel(IslandVisualState state, double targetWidth, double targetHeight)
    {
        // 入场（细横条 → 胶囊/卡片）是个"冒出来"的动作，比日常形变更跳
        if (state != IslandVisualState.Dot && _height.Current <= DotHeight + 1)
        {
            _width.Configure(300, 19);    // ζ=0.55
            _height.Configure(250, 19);   // ζ=0.60
            _radius.Configure(270, 20);   // ζ=0.61
            return;
        }

        // 用宽高之和判断整体方向，而不是各维度分别判断 ——
        // 宽增高减时分别判断会让两个维度取到相反的错峰顺序，形变就散了
        if (targetWidth + targetHeight > _width.Current + _height.Current)
        {
            _width.Configure(260, 20);    // ζ=0.62  快而弹，先冲出去
            _height.Configure(190, 21);   // ζ=0.76  慢半拍跟上
            _radius.Configure(215, 21);   // ζ=0.72  居中，跟着高度走但略快，中途更饱满
        }
        else
        {
            _width.Configure(200, 25);    // ζ=0.88  慢半拍收拢，几乎不过冲
            _height.Configure(270, 23);   // ζ=0.70  快而弹，先塌下去
            _radius.Configure(240, 23);   // ζ=0.74
        }
    }

    // ================= 渲染循环 =================

    private void HookRender()
    {
        if (_renderHooked) return;
        _renderHooked = true;
        _pendingDt = 0;
        _skipCounter = 999;   // 首帧立即渲染，不等分频攒数
        _frameClock.Restart();
#if DEBUG
        // 每次形变重新统计。不能靠 OnRendering 的 else 分支重置 ——
        // 弹簧一静止渲染回调就被摘掉了，那个分支根本轮不到执行，
        // 于是两次形变的帧数会累加到一起。
        _morphActive = false;
#endif
        CompositionTarget.Rendering += OnRendering;
    }

    private void UnhookRender()
    {
        if (!_renderHooked) return;
        _renderHooked = false;
        CompositionTarget.Rendering -= OnRendering;
        _frameClock.Stop();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var rawDt = _frameClock.Elapsed.TotalSeconds;
        _frameClock.Restart();
        _pendingDt += rawDt;

        // vsync 周期估计：最近 9 次回调间隔的**中位数**。
        // Rendering 回调并不严格一 vsync 一次，间隔向两个方向都会撒野：
        // 刚挂上回调时第一次几乎立刻到（≈0ms，曾把"衰减最小值"方案咬死，
        // 除数算到几百、渲染一停一秒多）；补偿调度又会提前到 2.5–3.3ms；
        // 渲染帧自己的耗时则把间隔拖长到 5ms+。均值和最值都会被这些离群值
        // 拽偏，中位数对两侧都免疫。
        if (rawDt > 0.001 && rawDt < 0.05)
        {
            _dtWindow[_dtIndex] = rawDt;
            _dtIndex = (_dtIndex + 1) % _dtWindow.Length;
            if (_dtFill < _dtWindow.Length) _dtFill++;
        }

        var vsyncPeriod = 0.0;
        if (_dtFill == _dtWindow.Length)
        {
            Span<double> sorted = stackalloc double[9];
            _dtWindow.CopyTo(sorted);
            sorted.Sort();
            vsyncPeriod = sorted[4];
        }

        // 判断用"当前有没有弹簧还没静止"，而不是上一帧的结论：
        // 用上一帧的话，形变第一帧会按律动档多等一个间隔才动，开头凭空一顿。
        var morphing = false;
        foreach (var spring in _allSprings)
        {
            if (spring.IsAtRest) continue;
            morphing = true;
            break;
        }

        // 限帧按 vsync 整数倍**分频**，绝不拿绝对时间当阈值卡：
        // 阈值一旦接近 vsync 周期，浮点抖动会让约一半回调恰好落在阈值下方被跳过，
        // 帧率减半且节奏不均（实测限 240 反而只剩 92fps，而不限帧有 194fps）。
        // 分频锁在回调流本身上，间隔恒为整数个 vsync，节奏均匀。
        var target = morphing ? MorphTargetFps : PulseTargetFps;
#if DEBUG
        if (FpsOverride == 0) target = 0;                  // 0 = 不限帧，做 A/B 用
        else if (FpsOverride > 0) target = FpsOverride;
#endif
        // 上限 8 是保险丝：估计器再出什么幺蛾子，渲染最多也就慢到 vsync/8，
        // 不会像除数失控那样一停一秒多。
        var divisor = target > 0 && vsyncPeriod > 0
            ? Math.Clamp((int)Math.Round(1 / (vsyncPeriod * target)), 1, 8)
            : 1;

        // 回弹尾巴：所有弹簧的运动都低于每帧约半像素后，帧率再减半也分辨不出 ——
        // 而尾巴占形变时长的一多半，这一刀省下的帧比分频本身还多。
        var slowTail = false;
        if (morphing && target > 0)
        {
            slowTail = true;
            foreach (var spring in _allSprings)
            {
                if (spring.IsAtRest) continue;
                if (Math.Abs(spring.Velocity) <= SlowTailSpeed * spring.RestScale) continue;
                slowTail = false;
                break;
            }

            if (slowTail) divisor *= 2;
        }

#if DEBUG
        _lastDivisor = divisor;
#endif
        if (++_skipCounter < divisor) return;
        _skipCounter = 0;

        var dt = _pendingDt;
        _pendingDt = 0;

        var moving = false;
        foreach (var spring in _allSprings)
        {
            spring.Update(dt);
            if (!spring.IsAtRest) moving = true;
        }

        ApplyVisuals();

#if DEBUG
        // 形变期的帧统计，快段与尾段分开记 —— 尾段是刻意的半帧率，
        // 混在一起平均会把快段的真实帧率拉低，读数失真。
        if (morphing)
        {
            if (!_morphActive)
            {
                _morphActive = true;
                _morphFrames = 0; _morphMs = 0; _tailFrames = 0; _tailMs = 0; _morphMaxDt = 0;
                _morphGen0 = GC.CollectionCount(0);
            }

            if (slowTail) { _tailFrames++; _tailMs += dt * 1000; }
            else { _morphFrames++; _morphMs += dt * 1000; }
            if (dt * 1000 > _morphMaxDt) _morphMaxDt = dt * 1000;
        }
        else
        {
            _morphActive = false;
        }
#endif

        // 形变结束后，只要还在律动就继续渲染；两者都停了才摘掉回调，空闲 CPU 归零
        if (!moving && !IsPulsing) UnhookRender();
    }

    /// <summary>事件圆的圆心。位置固定，动画只改半径。</summary>
    private static Point SplitDotCenter(Rect body)
        => new(body.Right + SplitGap + (SplitDotSize / 2), body.Top + (body.Height / 2));

    /// <summary>把事件圆并进主体几何。两者不相交，所以填充规则无所谓。</summary>
    private static Geometry? Group(Geometry? body, Geometry dot)
    {
        if (body is null) return dot;

        var group = new GeometryGroup { FillRule = FillRule.Nonzero };
        group.Children.Add(body);
        group.Children.Add(dot);
        return group;
    }

    /// <summary>频谱条是否正在随音乐律动（需要持续渲染）。</summary>
    private bool IsPulsing =>
        Spectrum.Visibility == Visibility.Visible
        && _compactOpacity.Current > 0.01
        && _audio is { IsAvailable: true, IsActive: true };

    private void ApplyVisuals()
    {
#if DEBUG
        _renderFrames++;
#endif
        var w = Math.Max(1, _width.Current);
        var h = Math.Max(1, _height.Current);
        var r = Math.Max(0, _radius.Current);

        // 形状在恒定尺寸的画布中居中，再靠 LayoutShift 整体上移，
        // 使形状顶边始终贴在 y=6。全程只有渲染变换，不碰布局。
        var offsetX = (BodyCanvasWidth - w) / 2;
        var offsetY = (BodyCanvasHeight - h) / 2;

        // 工具栏挂在岛体底边下方。岛体高度是弹簧驱动的，所以这里每帧跟一次 ——
        // 展开／收起岛体时工具栏跟着滑，而不是等形变结束再跳过去。
        // 只在开着的时候算：关着时它连 Visibility 都是 Collapsed
        if (_toolbarOpen) UpdateToolbarPosition(h);

        // Split 展开度也要进脏检查 —— 漏了它，事件圆滑出来的整个过程
        // 主体形状不变，轮廓就会一直沿用没有桥的那一份
        var split = Math.Clamp(_split.Current, 0, 1);

        // 形状没变就跳过整块几何重建。律动是持续的，而它只改透明度与缩放 ——
        // 每帧照旧重建轮廓的话，那点开销会一直挂在常驻占用上。
        var shapeChanged =
            Math.Abs(w - _geoWidth) > 0.01 ||
            Math.Abs(h - _geoHeight) > 0.01 ||
            Math.Abs(r - _geoRadius) > 0.01 ||
            Math.Abs(split - _geoSplit) > 0.002;

        if (shapeChanged)
        {
            _geoWidth = w;
            _geoHeight = h;
            _geoRadius = r;
            _geoSplit = split;

            if (!Skip("size")) LayoutShift.Y = -offsetY;

            // G3 连续曲率轮廓（见 Rendering/Squircle.cs）。填充用完整矩形；
            // 描边单独一条内缩 0.5px 的几何，让 1px 线落在像素中心而不发虚。
#if DEBUG
            _geoClock.Restart();
#endif
            var body = new Rect(offsetX, offsetY, w, h);

            Geometry? outline = null;
            if (!Skip("geo") || !Skip("clip"))
                outline = Squircle.Build(body, r);

            // 内容裁剪只用主体轮廓，不含事件圆与桥 —— 那两块里没有要裁的内容，
            // 而把桥算进 Clip 会让主体内容在腰部位置被意外裁掉一条
            var clip = outline;

            if (!Skip("geo"))
            {
                var stroke = Squircle.Build(
                    new Rect(offsetX + 0.5, offsetY + 0.5, Math.Max(0, w - 1), Math.Max(0, h - 1)),
                    Math.Max(0, r - 0.5));

                // Split：主体右侧弹出一个独立的事件圆。
                // 用"原地缩放"而不是"从主体里滑出来" —— 滑出会有一段圆与主体重叠的时期，
                // 那时并集边界上会冒出两个尖角接缝；缩放全程两个形状都不相交，干净。
                if (split > 0.01 && _transient is not null)
                {
                    var center = SplitDotCenter(body);
                    var radius = SplitDotSize / 2 * split;

                    outline = Group(outline, new EllipseGeometry(center, radius, radius));
                    stroke = Group(stroke, new EllipseGeometry(
                        center, Math.Max(0, radius - 0.5), Math.Max(0, radius - 0.5)));
                }

                BodyShape.SetGeometry(outline, stroke);
            }
#if DEBUG
            _geoClock.Stop();
            _geoFrames++;
            _geoAvgMs += (_geoClock.Elapsed.TotalMilliseconds - _geoAvgMs) / _geoFrames;
#endif

            // 内容裁剪复用主体轮廓，形变过程中内容不会溢出胶囊边缘
            if (!Skip("clip")) ContentHost.Clip = clip;

            // 感应区基准 100×100，靠 Scale 撑到岛体大小。
            // 灵动点态岛体只有 68×8，感应区至少要 150×22 才好命中。
            HoverScale.ScaleX = Math.Max(w, 150) / HitBase;
            HoverScale.ScaleY = Math.Max(h, 22) / HitBase;
        }

        var compact = Math.Clamp(_compactOpacity.Current, 0, 1);
        var expanded = Math.Clamp(_expandedOpacity.Current, 0, 1);

        if (!Skip("content"))
        {
            CompactContent.Opacity = compact;
            ExpandedContent.Opacity = expanded;

            // 内容不参与形变，只做位移 + 淡入淡出，避免被"拉扯"的廉价感
            CompactTransform.Y = (1 - compact) * -ContentShift;
            ExpandedTransform.Y = (1 - expanded) * ContentShift;

            // 注意：这里刻意不把全透明的内容切成 Collapsed。
            // 直觉上摘出可视树能省渲染遍历，但 Visibility 是 AffectsMeasure，
            // 每次翻转都会触发一次完整布局（含所有 TextBlock 的文本测量），
            // 实测反而让形变期 CPU 从 32% 涨到 40%。Opacity=0 的布局是缓存的，更便宜。

            // 歌词动画自带脏检查，不跟着形变/律动的帧率白刷（见 _lyricDirty）
            if (!_lyricSwap.IsAtRest || !_titleHold.IsAtRest || _lyricDirty)
            {
                ApplyLyricVisuals();

                var moving = !_lyricSwap.IsAtRest || !_titleHold.IsAtRest;

                // 旧句淡到看不见的那一刻就把槽宽从 max(旧, 新) 收到新句的实际长度。
                //
                // 判据是**行程到了 LyricOutFaded**，不是"弹簧静止"：弹簧静止还要
                // 等过冲回弹收敛完，实测多等约 200ms，于是看到的是"新句早就位了、
                // 岛体过一会儿才想起来收窄"的两段式。
                if (_lyricSlotFloor > 0 && _lyricSwap.Current >= LyricOutFaded) ReleaseSlotFloor();

                _lyricDirty = moving;
            }

            // 封面钉在岛体左缘、频谱钉在右缘，位置每帧跟着**弹簧当前宽度**走。
            //
            // 关键是用 w（_width.Current）而不是 _pillWidth（目标宽度）：后者瞬间跳变，
            // 那会让封面与频谱在岛体还在平滑收拢时就已经站到终点上，
            // 两者节奏对不上，看着就是"边上那两样东西在抽"。
            // 用 Transform 而不是布局定位，还顺带让换句彻底不触发布局。
            var half = w / 2;
            CompactArtShift.X = -half + CompactPadLeft + (CompactArtSize / 2);

            if (Spectrum.Visibility == Visibility.Visible && Spectrum.Width > 0)
                SpectrumShift.X = half - CompactPadRight - (Spectrum.Width / 2);

            // 文本槽移到"两侧占用之间"的正中，而不是胶囊正中。
            //
            // 文本可用区是 [left, w − right]，它的中心相对胶囊中心偏 (left − right)/2。
            // 不偏的话，两侧不等宽时文本就会往占用多的那一侧压过去 ——
            // 没有频谱时左 44 / 右 14，文本压进图标 15px，正是用户看到的那次重叠。
            //
            // 这个量只随"有没有频谱"变，与 w 无关，所以不会在形变途中跳。
            if (_activity is not null)
            {
                var (left, right) = CompactSides(_activity);
                CompactTextShift.X = (left - right) / 2;
            }
        }

        CompactContent.IsHitTestVisible = compact > 0.5;
        ExpandedContent.IsHitTestVisible = expanded > 0.5;

        if (_snapshotActive)
        {
            ApplyContentSwap();
            if (_contentSwap.IsAtRest) EndContentTransition();
        }

        // Split：字形跟着圆一起缩放，否则圆还没长大时里面已经挤着一个满尺寸图标
        if (split > 0.01 && _transient is not null)
        {
            var center = SplitDotCenter(new Rect(offsetX, offsetY, w, h));
            var gw = SplitGlyph.ActualWidth;
            var gh = SplitGlyph.ActualHeight;

            SplitScale.CenterX = gw / 2;
            SplitScale.CenterY = gh / 2;
            SplitScale.ScaleX = SplitScale.ScaleY = split;

            SplitShift.X = center.X - (gw / 2);
            SplitShift.Y = center.Y - (gh / 2);

            // 比圆本身晚一点冒头，让人先看到圆弹出来
            SplitGlyph.Opacity = Math.Clamp((split - 0.35) / 0.4, 0, 1);
        }
        else
        {
            if (SplitGlyph.Opacity != 0) SplitGlyph.Opacity = 0;

            // 圆缩完了才真正放手。放在这里而不是 SetActivitySet 里，
            // 是因为"动画走完没有"只有渲染循环知道。
            if (!_hasTransient && _split.IsAtRest) _transient = null;
        }

        // 频谱条只在收起态可见时才值得重画，且自带 ~36fps 限流：
        // 形变期渲染跑到 ~200fps，跟着每帧重画会把频谱的重绘也抬到 200fps ——
        // 而 FFT 数据本身 21ms 才出一帧，画得再勤也是重复画同一组数。
        // 实测这一条在"形变 + 律动同时进行"时白吃约 5 个百分点。
        if (_audio is not null && Spectrum.Visibility == Visibility.Visible && compact > 0.01)
        {
            var nowTicks = Environment.TickCount64;
            if (nowTicks - _spectrumTicks >= 27)
            {
                _spectrumTicks = nowTicks;
                Spectrum.SetValues(_audio.Spectrum);
            }
        }

#if DEBUG
        UpdateDebugProbe(w, h, r);
#endif
    }

#if DEBUG
    /// <summary>
    /// 调试通道：把状态、实时尺寸与进度挂到窗口标题上。
    /// 窗口无边框，标题不可见，但外部脚本可据此断言形变与数据链路确实在工作。
    /// </summary>
    private void UpdateDebugProbe(double w, double h, double r, bool force = false)
    {
        // 探针自己不能成为瓶颈。拼一条 300 字符的标题再 SetWindowText（同步窗口消息）
        // 每帧跑一次，足以把形变帧率压下去 —— 那样测出来的"帧率低"是探针造成的，
        // 跟产品代码无关。20fps 对外部轮询脚本绰绰有余。
        // 命中测试那条路径要的是即时值，用 force 绕过限流。
        var now = Environment.TickCount64;
        if (!force && now - _probeTicks < 50) return;
        _probeTicks = now;

        var prog = _scheduler?.Peek()?.Progress is { } pv ? pv.ToString("F3") : "null";
        var acc = _activity?.AccentColor is { } ac ? $"{ac.R:X2}{ac.G:X2}{ac.B:X2}" : "null";

        // 标题与封面各自的短指纹：用于判断两者是否同一时刻更新
        var titleTag = (_activity?.Title ?? "-").GetHashCode() & 0xFFFF;
        var artTag = _activity?.Artwork is null ? 0 : _activity.Artwork.GetHashCode() & 0xFFFF;

        // 岛体轮廓与感应区的矩形，坐标系是**窗口客户区 DIP** —— 也就是 WM_NCHITTEST
        // 处理里 InputHitTest 用的那个空间。测试脚本必须用它来构造测试点。
        //
        // 这里原先报的是 PointToScreen 得到的屏幕坐标，结果它与 WM_NCHITTEST 收到的
        // 坐标不在同一空间（本机 125% 缩放下相差一个 DPI 因子）。据此构造测试点，
        // 采样点整体偏移，测出来"轮廓内不响应、轮廓外反而响应"，
        // 于是去改根本没错的命中代码 —— 白查一轮。探针自己错了最难发现，
        // 所以这两个字段只报命中测试自己的坐标系。
        var box = RectIn(BodyShape, (BodyCanvasWidth - w) / 2, (BodyCanvasHeight - h) / 2, w, h);
        var hz = RectIn(HoverZone, 0, 0, HoverZone.ActualWidth, HoverZone.ActualHeight);

        var probe = $"IslandX|{ComputeState()}|{(int)Math.Round(w)}x{(int)Math.Round(h)}|r{(int)Math.Round(r)}|p{prog}|hk{_hotkeyStatus}/{_hotkeyHits}|a{acc}"
            + $"|tier{RenderCapability.Tier >> 16}|hov{(_hovering ? 1 : 0)}"
            + $"|T{titleTag:X4}|A{artTag:X4}|n{_swapCount}"
            + $"|ev{Providers.MediaSessionProvider.DebugEventCount}"
            + $"/dr{Providers.MediaSessionProvider.DebugDroppedCount}"
            + $"/ld{Providers.MediaSessionProvider.DebugArtLoadCount}"
            + $"/ch{Providers.MediaSessionProvider.DebugChaseHitCount}"
            + $"|sha{Providers.MediaSessionProvider.DebugThumbHash}"
            + $"|hit{_lastHit}"
            + $"|tb{(_toolbarOpen ? "open" : "shut")}/{_toolbarButtons.Count}btn/rc{_rightClicks}/{ToolbarStateProbe()}"
            + $"|why{Providers.ArtworkColor.DebugLastReason}"
            + $"|lyr{Providers.MediaSessionProvider.DebugLyricState}"
            // 会话选取。"进度条/歌词忽有忽无"最可能的原因就是挑错了会话，
            // 而那在界面上完全看不出是选取问题
            + $"|sess{Providers.MediaSessionProvider.DebugSessionPick}"
            // 天气链路。同样不加 #if DEBUG —— "没提醒我下雨"有六种原因
            // （没开、没定到位、请求失败、判据没触发、去重吃掉了、真的不下雨），
            // 在界面上全都表现为什么都没发生，肉眼零区分度
            + $"|wx{Providers.WeatherProvider.DebugState}"
            + $"|al{Providers.WeatherProvider.DebugAlertState}"
            // Phira 联动。"打完没反应"有四种原因（功能关、没找到进程、
            // 存档路径不对、这局没刷新纪录），肉眼零区分度
            + $"|phi{Providers.PhiraProvider.DebugState}"
            + $"|clip{Providers.ClipboardProvider.DebugMessageCount}"
            + $"/f{Providers.ClipboardProvider.DebugReadFailCount}"
            + $"/p{Providers.ClipboardProvider.DebugPublishCount}"
            + $"/{Providers.ClipboardProvider.DebugLastError}"
            + $"|aud{(_audio is null ? "off" : _audio.IsAvailable ? "on" : "fail")}"
            + $"/l{_audio?.Level ?? 0:F2}/act{(_audio?.IsActive == true ? 1 : 0)}"
            + $"|err{(string.IsNullOrEmpty(_audio?.LastError) ? "-" : _audio.LastError)}"
            + $"|swap{(_snapshotActive ? _contentSwap.Current.ToString("F2") : "-")}@{_swapStart}"
            + $"|os{_width.PeakOvershoot:F3}/{_height.PeakOvershoot:F3}/{_radius.PeakOvershoot:F3}"
            + $"|ms{_width.SettleMs:F0}/{_height.SettleMs:F0}/{_radius.SettleMs:F0}"
            + $"|bt{_width.PeakBacktrack:F3}@{_width.BacktrackMs:F0}/{_height.PeakBacktrack:F3}@{_height.BacktrackMs:F0}"
            + $"|split{_split.Current:F2}/{(_transient is null ? "-" : _transient.Id)}"
            + $"|lsw{_lyricSwap.Current:F2}/{_titleHold.Current:F2}"
            // 模糊同时给瞬时值与本句峰值（pk）。瞬时值受探针 20fps 限流，
            // 采不到那 100ms 的峰 —— 读到 0.0 分不清"走完了"还是"压根没模糊过"
            + $"/i{LyricIn.Opacity:F2}@{LyricIn.BlurSigma:F1}pk{LyricIn.PeakBlur:F1}"
            + $"/o{LyricOut.Opacity:F2}@{LyricOut.BlurSigma:F1}pk{LyricOut.PeakBlur:F1}"
            // 岛体目标宽度，以及封面/频谱相对岛体中心的偏移。
            // 后两个用来断言它们真的钉在两端：art 应恒等于 −w/2+20、spec 恒等于 +w/2−14−频谱半宽，
            // 而且必须跟着**弹簧当前宽度**变，不是跟着目标宽度瞬间跳
            + $"/w{_pillWidth:F0}@{CompactArtShift.X:F0},{SpectrumShift.X:F0}"
            // 文本槽的偏移。它和上面两个必须与 CompactSides 同源 ——
            // 一旦各算各的，文字就会压到图标上（本项目栽过一次）。
            // 断言：txt 应恒等于 (左侧占用 − 右侧占用)/2，即无频谱 +15、有频谱 −5
            + $"txt{CompactTextShift.X:F0}"
            + $"/sub{(LyricIn.HasSub ? 1 : 0)}"
            // 换句次数、起步瞬间两层透明度、本次换句的实测过冲。
            // 过冲由弹簧在子步级别自记，是**累积峰值** —— 200ms 的动画靠外部轮询
            // 采不到那一瞬，而这个量一直保持到下次换句，任何采样频率都读得到。
            + $"|lsn{_lyricSwitches}@{_lyricStart}/os{_lyricSwap.PeakOvershoot:F3}"
            + $"|box{box}|hz{hz}/{(HoverZone.IsHitTestVisible ? 1 : 0)}"
            + $"|mf{_morphFrames}/{_morphMs:F0}+{_tailFrames}/{_tailMs:F0}/max{_morphMaxDt:F1}/gc{GC.CollectionCount(0) - _morphGen0}/dv{_lastDivisor}"
            + $"|rf{_renderFrames}|geo{_geoAvgMs:F3}ms/{_geoFrames}";
        if (!string.Equals(Title, probe, StringComparison.Ordinal)) Title = probe;
    }

#if DEBUG
    /// <summary>
    /// 换句压测节拍器：<c>ISLANDX_LYRICBEAT=&lt;毫秒&gt;</c> 时按固定间隔强制换句。
    ///
    /// 存在的理由是**真实歌词的换句次数不受控**：同一首歌不同段落的歌词密度差得多，
    /// 而每轮测量重启岛体、播放器却一直在走，各轮采到的根本是歌曲的不同段落。
    /// 实测 A/B/A 三轮分别换了 13 / 6 / 8 句 —— 换句次数直接决定动画占了多少时间，
    /// 它在 CPU 对比里的权重远超被测的模糊开销本身，那组数据得不出任何结论。
    ///
    /// 节拍器走的是完整的 <see cref="SetActivity"/> 路径，只是歌词由它给：
    /// 双层轮换、宽度重算、四个弹簧、模糊挂摘全都真实发生，不是合成负载。
    /// </summary>
    private static readonly int LyricBeatMs =
        int.TryParse(Environment.GetEnvironmentVariable("ISLANDX_LYRICBEAT"), out var lb) && lb > 0 ? lb : 0;

    private DispatcherTimer? _lyricBeat;
    private int _beatIndex;

    /// <summary>长短轮换的假歌词，顺带把宽度自适应那条路径也压上。</summary>
    private static readonly string[] BeatSamples =
    [
        "Hey there Delilah",
        "We are standing at the edge of time",
        "Rain",
        "And I will keep every word you never said to me",
    ];

    /// <summary>
    /// 压测用的译文，与 <see cref="BeatSamples"/> 一一对应。
    ///
    /// 第三条**故意为 null**：外语歌里纯感叹句（"Oh oh oh"）常常没有译文
    /// （实测《Spring Day》80 行里有 3 行如此）。有了它，节拍器每轮都会走一遍
    /// 双行 ↔ 单行的高度切换（54 ↔ 40），而那正是最容易抖的一段。
    /// 全给译文的话这条路径压根测不到。
    /// </summary>
    private static readonly string?[] BeatSubs =
    [
        "嘿 迪丽拉",
        "我们站在时间的边缘",
        null,
        "我会记住每一句你从未说出口的话",
    ];

    private void StartLyricBeat()
    {
        if (LyricBeatMs <= 0) return;

        _lyricBeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LyricBeatMs) };
        _lyricBeat.Tick += (_, _) =>
        {
            if (_activity is null) return;

            // 只推进索引，歌词本身在 SetActivity 里被替换 ——
            // Provider 每 110ms 也在推活动，若这里直接塞 Lyric，两边就会互相打断，
            // 换句次数重新变得不可控，正是这个节拍器要消除的东西。
            _beatIndex++;
            SetActivity(_activity);
        };

        _lyricBeat.Start();
    }
#endif

    /// <summary>
    /// 把元素内的一块矩形换算到窗口客户区 DIP，形如 "x,y,wxh"。
    /// 用 TransformBounds 而不是变换左上角一个点：感应区靠 ScaleTransform 撑开，
    /// 只换算原点会报出未缩放的 100×100，探针自己先骗了自己。
    /// </summary>
    private string RectIn(UIElement element, double x, double y, double w, double h)
    {
        try
        {
            var r = element.TransformToAncestor(this).TransformBounds(new Rect(x, y, w, h));
            return $"{(int)Math.Round(r.X)},{(int)Math.Round(r.Y)},{(int)Math.Round(r.Width)}x{(int)Math.Round(r.Height)}";
        }
        catch { return "?"; }
    }
#endif
}
