using IslandX.Contracts;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using IslandX.Core;

namespace IslandX;

/// <summary>
/// 岛体的右键工具栏 —— **视图切换器**，不是设置面板。
///
/// 点一下把岛体**临时**切到那个视图（想知道雨几时到就点天气），
/// 指针离开岛体与工具栏就还原成正常仲裁的结果。
/// 改"以后怎样"的开关在托盘菜单和设置窗口里，不在这条上。
///
/// **来源说明**：用户要的样式参照是 <c>E:\WinIsland</c>，但那个项目里
/// **没有右键工具栏** —— 它岛体上的右键是"拖动岛体改位置"
/// （<c>src/window/app/input.rs:36-76</c>，4px 阈值，设置里叫「右键长按移动」），
/// 全仓 <c>WM_CONTEXTMENU</c> / <c>TrackPopupMenu</c> 零命中，
/// 唯一的菜单是托盘的**系统原生**菜单，没有可抄的样式数值。
///
/// 所以这里的做法是：**外观数值照抄它，动效走本项目的规矩**，两处来源都标在常量上。
/// 外观抄的是它自绘的设置下拉菜单（<c>window/settings/renderer.rs:445-480</c>）
/// 与组件卡片（<c>ui/widget/expanded/mod.rs:87-113</c>）。
/// 动效不抄 —— 它那个下拉是纯 opacity 指数淡入（<c>settings/input.rs:26</c>，speed 0.3），
/// 紧挨着弹簧驱动的岛体会很出戏。
/// </summary>
public partial class IslandWindow
{
    // ===== 几何。除注明外均来自 WinIsland =====

    /// <summary>卡片边长。WinIsland 的设置组件卡片是 50×50，这里的岛体比它小，按比例收到 40。</summary>
    private const double ToolbarButtonSize = 40;

    /// <summary>卡片间距。WinIsland 组件网格是 7（<c>expanded/mod.rs:73</c>），这里少一列、收到 6。</summary>
    private const double ToolbarGap = 6;

    /// <summary>外壳内边距。<c>POPUP_MENU_PAD = 5.0</c>（<c>utils/settings_ui/items.rs:25</c>）。</summary>
    private const double ToolbarPad = 5;

    /// <summary>卡片圆角。<c>widget_corner_radius = min(12, 短边/2)</c>（<c>expanded/mod.rs:111-113</c>）。</summary>
    private const double ToolbarButtonRadius = 12;

    /// <summary>
    /// 外壳圆角。**这一个没照抄** —— WinIsland 的 <c>POPUP_MENU_R</c> 是 10，
    /// 但它的下拉菜单里装的是文字行，不是圆角卡片；这里要在外壳里嵌卡片，
    /// 10 会让卡片比外壳还圆，看着像卡片顶破了外壳。
    ///
    /// 同心圆角的规矩是**外圆角 = 内圆角 + 内边距**，于是 12 + 5 = 17。
    /// </summary>
    private const double ToolbarShellRadius = ToolbarButtonRadius + ToolbarPad;

    /// <summary>工具栏顶边与岛体底边的间距。</summary>
    private const double ToolbarOffsetY = 8;

    /// <summary>图标占卡片短边的比例。<c>ui/widget/expanded/settings.rs:17</c> 的 <c>0.38</c>。</summary>
    private const double ToolbarGlyphRatio = 0.38;

    // ===== 配色。除注明外均来自 WinIsland =====

    /// <summary>卡片底色。<c>argb(α·0.05, 28,28,30)</c> → α=255 时 0x0D。</summary>
    private static readonly Brush ToolbarCardFill = Frozen(0x0D, 0x1C, 0x1C, 0x1E);

    /// <summary>卡片描边。<c>argb(α·0.16, 255,255,255)</c> → α=255 时 0x29。</summary>
    private static readonly Brush ToolbarCardStroke = Frozen(0x29, 0xFF, 0xFF, 0xFF);

    /// <summary>图标不透明度 0.72（<c>settings.rs:19</c>）→ 0xB8。</summary>
    private static readonly Brush ToolbarGlyphIdle = Frozen(0xB8, 0xFF, 0xFF, 0xFF);

    // 开启态与悬停态是**本项目的增量** —— WinIsland 的组件卡片没有开关状态，
    // 而这排按钮一半是开关，看不出开没开的话这个工具栏就没有意义。
    private static readonly Brush ToolbarCardFillOn = Frozen(0x1F, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ToolbarCardStrokeOn = Frozen(0x66, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ToolbarCardFillHover = Frozen(0x24, 0xFF, 0xFF, 0xFF);
    private static readonly Brush ToolbarGlyphOn = Frozen(0xF2, 0xFF, 0xFF, 0xFF);

    /// <summary>没内容可看时的图标色。比常态更暗，和"能点但没开"区分得开。</summary>
    private static readonly Brush ToolbarGlyphDisabled = Frozen(0x3D, 0xFF, 0xFF, 0xFF);

    private static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    // ===== 状态 =====

    private sealed class ToolbarButton
    {
        public required Border Card { get; init; }
        public required TextBlock Glyph { get; init; }
        public required IslandView View { get; init; }

        public bool Hovering { get; set; }

        /// <summary>此刻有没有东西可看。没有就置灰 —— 点开一条空的比点不动更糟。</summary>
        public bool Usable { get; set; } = true;
    }

    private readonly List<ToolbarButton> _toolbarButtons = [];
    private IReadOnlyList<IslandView> _views = [];
    private bool _toolbarOpen;

    /// <summary>此刻钉住的是哪个 Provider。null = 没在看任何视图。</summary>
    private string? _peeking;
    private DispatcherTimer? _toolbarCloseTimer;

    /// <summary>工具栏关着时的窗口高度，来自 XAML。撑高之后要能还原回去。</summary>
    private double _windowHeightIdle;

    /// <summary>
    /// 接入可切换的视图。顺序即左到右的排列顺序。
    /// 由 App 组装 —— 视图要拿各个 Provider 的数据，而 App 本来就是那个持有它们的地方。
    /// </summary>
    public void BindViews(IReadOnlyList<IslandView> views)
    {
        _views = views;
        _windowHeightIdle = Height;   // 必须在任何一次撑高之前取
        BuildToolbar();
    }

    private void BuildToolbar()
    {
        for (var i = 0; i < _views.Count; i++)
        {
            var view = _views[i];

            var glyph = new TextBlock
            {
                Text = view.Glyph,
                FontFamily = (FontFamily)Application.Current.Resources["IconFont"],
                FontSize = ToolbarButtonSize * ToolbarGlyphRatio,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var card = new Border
            {
                Width = ToolbarButtonSize,
                Height = ToolbarButtonSize,
                // 40px 上 G3 与普通圆角肉眼无差，而 WinIsland 的卡片本身用的也是
                // draw_round_rect（普通圆角）—— 外壳那块大的才走 Squircle
                CornerRadius = new CornerRadius(ToolbarButtonRadius),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Child = glyph,
                ToolTip = view.Label,
                Margin = new Thickness(i == 0 ? 0 : ToolbarGap, 0, 0, 0),
            };

            var button = new ToolbarButton { Card = card, Glyph = glyph, View = view };

            card.MouseEnter += (_, _) => { button.Hovering = true; RefreshToolbarStates(); };
            card.MouseLeave += (_, _) => { button.Hovering = false; RefreshToolbarStates(); };

            card.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                TogglePeek(button);
            };

            // 右键落在按钮上也要能收起工具栏，否则这一排就成了"右键死区"
            card.MouseRightButtonUp += (_, e) => { e.Handled = true; SetToolbarOpen(false); };

            _toolbarButtons.Add(button);
            ToolbarItems.Children.Add(card);
        }

        SizeToolbar(_views.Count);

        // 只管起停计时器，不再记"指针在不在里面" —— 那个缓存标志在
        // 工具栏会自己移动的前提下不可靠，判定统一走 PointerStillInside 的几何实测
        Toolbar.MouseEnter += (_, _) => _toolbarCloseTimer?.Stop();
        Toolbar.MouseLeave += (_, _) => ScheduleToolbarClose();

        RefreshToolbarStates();
    }

    // ================= 临时切视图 =================

    /// <summary>
    /// 点一下切过去，再点一下切回来。
    ///
    /// 切过去之后**工具栏不关** —— 用户点天气就是为了看那条内容，
    /// 而这一排就在岛体正下方，关掉反而要重新右键才能换下一个视图。
    /// 真正的结束条件是指针离开（见 <see cref="ScheduleToolbarClose"/>）。
    /// </summary>
    private void TogglePeek(ToolbarButton button)
    {
        if (!button.Usable) return;

        SetPeek(_peeking == button.View.ProviderId ? null : button.View);
    }

    private void SetPeek(IslandView? view)
    {
        Diag.Log($"toolbar: SetPeek({view?.ProviderId ?? "null"})");

        _peeking = view?.ProviderId;

        // Build() 出来的那条**优先**：它才是"我现在想知道什么"的答案。
        // 没有合成器的（媒体、时钟）返回 null，于是钉住实时那条、歌词照常走字
        // —— 优先级的理由见 ActivityScheduler.Pin 的注释
        var peek = view is null ? null : SafeBuild(view);
        Diag.Timed($"Pin({_peeking})", () => _scheduler?.Pin(_peeking, peek));

        Diag.Timed("SetPeek 后刷新状态", RefreshToolbarStates);
    }

    /// <summary>
    /// 合成一条视图活动，**出错不外抛**。
    ///
    /// 这是个跨越边界的调用：工具栏在**UI 线程**上调各个 Provider 的代码。
    /// 那边抛出来的异常会一路冒到 WPF 的输入处理里，成为 UI 线程的未处理异常 ——
    /// 表现不是"这个按钮不好用"，而是**整个岛体卡死**。
    ///
    /// 这个坑已经踩过一次：<c>WeatherProvider.HighlightRange</c> 隐含
    /// "只在下雨时调用"的前提，主动查看破了这个前提，于是**天晴时右键必卡**。
    /// 那次是真有 bug，但代价与 bug 的严重程度完全不成比例 ——
    /// 一个可空值判断，赔上整个应用。
    ///
    /// 所以这里兜住：一个视图坏掉就只是那一个按钮置灰，日志里留下证据。
    /// 全局的未处理异常钩子只记不吞（吞掉会把真 bug 变成"莫名其妙不工作"），
    /// 该兜的是这种**已知会调到外部代码**的具体边界。
    /// </summary>
    private static IslandActivity? SafeBuild(IslandView view)
    {
        try
        {
            IslandActivity? built = null;
            Diag.Timed($"Build({view.ProviderId})", () => built = view.Build());
            return built;
        }
        catch (Exception ex)
        {
            Diag.Log($"!!! 视图 {view.ProviderId} 的 Build 抛了: {ex}");
            System.Diagnostics.Debug.WriteLine($"[toolbar] {view.ProviderId} Build 失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 按按钮数量把外壳尺寸**算死**，再据此建轮廓。
    ///
    /// 这里最初挂在 <c>SizeChanged</c> 上、按 <c>ActualWidth</c> 建几何，那是个反馈环：
    /// 阴影的几何比容器**大 2px**（照抄 WinIsland 的 <c>w+2, h+2</c>），
    /// Path 把它报成自己的期望尺寸 → Grid 跟着长大 → 再次 SizeChanged → 每轮涨 2px，
    /// 一直涨到撞上窗口边界。**容器的尺寸不能由它自己的装饰层决定。**
    ///
    /// 所以改成从按钮数量正算，并显式写死 Width/Height ——
    /// 之后 Path 报多大都不再影响布局。
    /// </summary>
    private void SizeToolbar(int buttonCount)
    {
        var w = (buttonCount * ToolbarButtonSize)
            + (Math.Max(0, buttonCount - 1) * ToolbarGap)
            + (ToolbarPad * 2);

        var h = ToolbarButtonSize + (ToolbarPad * 2);

        Toolbar.Width = w;
        Toolbar.Height = h;

        // 描边 0.5px：内缩 0.25 让线落在像素上而不是跨在两个像素之间
        ToolbarBackdrop.Data = Rendering.Squircle.Build(
            new Rect(0.25, 0.25, w - 0.5, h - 0.5), ToolbarShellRadius);

        // WinIsland 的阴影是 (left, top+2, w+2, h+2) 的纯色圆角矩形，没有高斯模糊。
        // 它比外壳大一圈、越出 Grid 边界，这没问题 —— Grid 默认不裁剪，
        // 而尺寸已经写死，越界的部分不会再反过来把容器撑大
        ToolbarShadow.Data = Rendering.Squircle.Build(
            new Rect(0, 2, w + 2, h + 2), ToolbarShellRadius);
    }

    /// <summary>
    /// 刷新每个按钮的亮暗：亮起 = 正在看这个视图，置灰 = 此刻没内容可看。
    ///
    /// "有没有内容"每次都重新问，不缓存 —— 天气会随预报更新、Phira 会随游玩变化，
    /// 缓存下来的话按钮会一直停在打开工具栏那一刻的状态。
    /// 三个 Build 都很便宜（读缓存的解读 / 遍历几十条成绩 / 一次系统调用）。
    /// </summary>
    private void RefreshToolbarStates()
    {
        foreach (var button in _toolbarButtons)
        {
            var id = button.View.ProviderId;

            // CurrentOf 要拿仲裁器的锁，Build 可能走系统调用 ——
            // 两者都是这条路径上可能卡住的地方，各自计时
            var live = false;
            Diag.Timed($"CurrentOf({id})", () => live = _scheduler?.CurrentOf(id) is not null);

            button.Usable = live || SafeBuild(button.View) is not null;

            var on = _peeking == id;

            if (!button.Usable)
            {
                button.Card.Background = ToolbarCardFill;
                button.Card.BorderBrush = ToolbarCardStroke;
                button.Glyph.Foreground = ToolbarGlyphDisabled;
                button.Card.Cursor = Cursors.Arrow;
                continue;
            }

            button.Card.Cursor = Cursors.Hand;

            button.Card.Background = button.Hovering ? ToolbarCardFillHover
                : on ? ToolbarCardFillOn
                : ToolbarCardFill;

            button.Card.BorderBrush = on ? ToolbarCardStrokeOn : ToolbarCardStroke;
            button.Glyph.Foreground = on || button.Hovering ? ToolbarGlyphOn : ToolbarGlyphIdle;
        }
    }

#if DEBUG
    /// <summary>
    /// 探针：每个按钮一位 —— <c>*</c> 正在看、<c>1</c> 有内容可看、<c>0</c> 置灰。
    /// 后面跟当前钉住的 Provider Id。
    /// </summary>
    private string ToolbarStateProbe()
    {
        // 关着的时候 Usable 是上一次打开时算的，早就过期了。
        // 报一串陈旧值比不报更糟 —— 那会让人拿旧尺子去量新东西。
        // 这里不能就地重算：探针每帧都跑，而重算要走一次系统调用 + 一遍成绩表
        if (!_toolbarOpen) return new string('-', _toolbarButtons.Count) + "@-";

        return string.Concat(_toolbarButtons.Select(b =>
                   _peeking == b.View.ProviderId ? "*" : b.Usable ? "1" : "0"))
               + "@" + (_peeking ?? "-");
    }
#endif

    // ================= 开合 =================

    private void ToggleToolbar() => SetToolbarOpen(!_toolbarOpen);

    private void SetToolbarOpen(bool open)
    {
        if (_views.Count == 0) return;           // 没接线就没有按钮，弹出来是个空壳
        if (_toolbarOpen == open) return;

        Diag.Log($"toolbar: SetToolbarOpen({open}) 开始");

        _toolbarOpen = open;
        Toolbar.IsHitTestVisible = open;
        _toolbarCloseTimer?.Stop();

        if (open)
        {
            // 先撑高窗口，再让工具栏出场 —— 顺序反了的话，展开态下
            // 工具栏会先被窗口边界硬切一帧，然后才补上
            //
            // 分层窗口改尺寸要重建整个表面，是这条路径上最贵的一步 —— 单独计时
            Diag.Timed("改窗口高度", () => Height = ToolbarWindowHeight);

            Diag.Timed("刷新按钮状态", RefreshToolbarStates);

            Toolbar.Visibility = Visibility.Visible;
            UpdateToolbarPosition(_height.Current);

            // 进场要弹，对应岛体那条 ζ≈0.58、约 11% 过冲的规矩。
            //
            // Amplitude 到过冲不是线性的，**也不等于 Amplitude 本身**：
            // WPF 的 BackEase 是 f(t)=t³−t·a·sin(πt)，EaseOut 取 1−f(1−t)，
            // 数值解出来 a=0.18→1.7%、0.35→6.9%、0.45→11%、0.6→17.7%。
            // 这里要 11%，所以是 0.45。（第一版凭感觉写了 0.35 并在注释里断言"约 11%"，
            // 连拍实测只有 5.8% —— 数值关系不能靠估。）
            //
            // From 必须写死：上一次退场停在 0.94，不写的话第二次弹得比第一次弱
            var pop = new DoubleAnimation
            {
                From = 0.86,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(340),
                EasingFunction = new BackEase { Amplitude = 0.45, EasingMode = EasingMode.EaseOut },
            };

            ToolbarScale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            ToolbarScale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);

            // 不透明度**不过冲** —— 亮度回弹会被看成闪烁，这条规矩全项目一致
            Toolbar.BeginAnimation(OpacityProperty, new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        else
        {
            // 工具栏收了就不再看任何视图。用户说的是"直到指针离开岛体"，
            // 而工具栏的收起条件正是同一件事，两者绑在一起不会各走各的
            SetPeek(null);

            // 退场不回弹，而且更快 —— 要走的东西没必要挽留
            var shrink = new DoubleAnimation
            {
                To = 0.94,
                Duration = TimeSpan.FromMilliseconds(150),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };

            ToolbarScale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            ToolbarScale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(140),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            };

            // 收完要真的 Collapsed：opacity=0 的元素照样参与命中测试，
            // 留着它会在岛体下方压出一片看不见的"点不动"的区域
            fade.Completed += (_, _) =>
            {
                if (_toolbarOpen) return;

                Toolbar.Visibility = Visibility.Collapsed;

                // 收完立刻把窗口还原。多出来的那块是**每帧**都要推给 DWM 的
                // 分层表面，不能因为"工具栏偶尔会用到"就一直占着
                Height = _windowHeightIdle;
            };

            Toolbar.BeginAnimation(OpacityProperty, fade);
        }

        Diag.Log($"toolbar: SetToolbarOpen({open}) 结束");
    }

    /// <summary>
    /// 指针离开后延时收起。给宽限期是因为工具栏与岛体是**两块分离的矩形**，
    /// 中间那 8px 空隙里指针不在任何一块上 —— 不给宽限期的话，
    /// 从岛体往按钮上移的过程中它自己就先关了。
    /// </summary>
    private void ScheduleToolbarClose()
    {
        if (!_toolbarOpen) return;

        _toolbarCloseTimer ??= CreateToolbarCloseTimer();
        _toolbarCloseTimer.Stop();
        _toolbarCloseTimer.Start();
    }

    private DispatcherTimer CreateToolbarCloseTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 指针又回到工具栏或岛体上：**接着等**，而不是就此作罢。
            //
            // 原先这里直接 return，于是"从工具栏移到岛体上"会把这一轮消耗掉，
            // 之后再离开岛体时没有任何事件重新启动它 —— 工具栏就一直挂着。
            // 重新排一轮等于把它变成轮询：条件一满足就收，不依赖某个特定的
            // 离开事件恰好发生过。工具栏会跟着岛体滑动，那种"指针没动、
            // 元素却挪走了"的情况本来也发不出可靠的离开事件
            if (PointerStillInside())
            {
                timer.Start();
                return;
            }

            SetToolbarOpen(false);
        };

        return timer;
    }

    /// <summary>
    /// 工具栏打开期间窗口要多高。
    ///
    /// 窗口平时是 640×168，而这个尺寸是**算过账的**：分层窗口每帧要把整个表面
    /// 推给 DWM，面积直接就是钱，所以它只比"形状可能到达的最大范围"大一圈
    /// （见 <see cref="BodyCanvasWidth"/> 的注释）。
    ///
    /// 展开态下工具栏要落到 y=154…204，168 装不下 —— 实测会被硬切成一条。
    /// 但为此把窗口永久加高 48，等于让**每一帧**都多付近三成表面，
    /// 只为一个偶尔弹一下的工具栏。所以只在它打开期间撑高，收起就还原。
    ///
    /// 高度里算进了展开入场的首摆过冲（约 9%）—— 工具栏开着时左键展开岛体是
    /// 允许的，那一下岛体会冲到 140 以上，工具栏跟着被推得更低。
    /// </summary>
    private double ToolbarWindowHeight =>
        IslandLayout.Margin.Top
        + ExpandedHeight + (0.09 * (ExpandedHeight - DotHeight))
        + ToolbarOffsetY
        + Toolbar.Height
        + 6;   // 阴影外扩 2 + 描边余量

    /// <summary>
    /// 指针是不是还在岛体或工具栏上。
    ///
    /// 工具栏那一半**按当前几何实测**，不信 MouseEnter/Leave 的缓存标志：
    /// 工具栏会跟着岛体高度滑动，指针一动不动它却从指针底下挪走了 ——
    /// 那一刻 Leave 会触发、缓存说"不在里面"，但指针可能正落在它移动后的新位置上。
    /// 元素自己会动的时候，缓存的悬停状态就是不可靠的。
    /// </summary>
    private bool PointerStillInside()
    {
        if (_hovering) return true;
        if (!Toolbar.IsVisible) return false;

        return Toolbar.InputHitTest(Mouse.GetPosition(Toolbar)) is not null;
    }

    /// <summary>
    /// 把工具栏推到岛体底边下方。岛体高度是弹簧驱动、每帧在变的，
    /// 所以这里要跟着走 —— 展开／收起岛体时工具栏会**跟着滑**，而不是跳一下。
    /// </summary>
    private void UpdateToolbarPosition(double islandHeight)
    {
        // 岛体形状的顶边恒贴在 IslandLayout 的上边距处（见 ApplyVisuals 里
        // LayoutShift 的注释），所以底边就是"上边距 + 当前高度"。
        //
        // 上边距直接从 XAML 读，不在这里复制一个 6 —— 两处各写一份的话，
        // 改了 XAML 而漏改这里不会有编译错误，只会表现为工具栏与岛体差着几像素
        ToolbarShift.Y = IslandLayout.Margin.Top + islandHeight + ToolbarOffsetY;
    }
}
