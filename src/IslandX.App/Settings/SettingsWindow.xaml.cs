using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using IslandX.Core;

namespace IslandX.Settings;

/// <summary>
/// 设置窗口。
///
/// 每一项都**即时生效、即时保存**，没有「确定 / 取消」。理由是这些设置的效果
/// 全都能立刻在岛体上看到（歌词出现、频谱停下、岛体收成细横条）——
/// 攒一批再提交只会让人不确定改动到底有没有落地。
///
/// 它和托盘菜单共用同一个 <see cref="SettingsBridge"/>，不各接一遍：
/// 两处各自接线的话，加一个开关就得改两处，而漏改的那处不会有编译错误，
/// 只会表现为"从设置页改了、托盘里没跟着变"。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsBridge _bridge;

    /// <summary>正在初始化 UI —— 此期间的 Checked 事件不该回写配置。</summary>
    private bool _loading = true;

    /// <summary>正在等哪个热键被按到（"按一下试试"）；null 表示没在等。</summary>
    private HotkeyKind? _awaitingTest;

    private readonly DispatcherTimer _testTimeout;
    private readonly Action<HotkeyKind> _hotkeyFiredHandler;

    /// <summary>
    /// 反查行政区用。按需创建、随窗口释放 —— 每次点按钮新建一个是经典的
    /// socket 耗尽反模式，而这里是个手点的按钮，一个实例足够。
    /// </summary>
    private System.Net.Http.HttpClient? _http;

    public SettingsWindow(SettingsBridge bridge)
    {
        _bridge = bridge;
        InitializeComponent();

        // 热键测试的超时。没有它的话"没反应"和"还没按"在界面上是同一个样子，
        // 而这两件事的含义完全相反 —— 一个是热键坏了，一个是用户还没动手
        _testTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _testTimeout.Tick += (_, _) => FinishTest(fired: false);

        _hotkeyFiredHandler = OnHotkeyFired;
        _bridge.SubscribeHotkeyFired(_hotkeyFiredHandler);

        Loaded += OnLoaded;
        Closed += OnClosed;

        WireUp();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FitToWorkArea();
        UpdateShape();
        LoadFromConfig();
        _loading = false;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _testTimeout.Stop();
        _bridge.UnsubscribeHotkeyFired(_hotkeyFiredHandler);
        _http?.Dispose();
        _http = null;
    }

    // ================= 窗口轮廓（自绘） =================

    /// <summary>
    /// 用产品里那份 <see cref="Rendering.Squircle"/> 画窗口轮廓，与岛体同一份实现。
    ///
    /// 一开始走的是系统标题栏 + DWM 深色属性那条路，理由写的是"自绘要重做拖动、
    /// 双击最大化、贴边分屏、系统菜单，为一条标题栏不值得"。
    /// 那个判断错了：**代价算对了，收益算漏了**。系统标题栏那一条白边
    /// 让整页立刻看着像另一个程序，而这个项目的卖点恰恰是形状。
    /// 设置面板不是文档窗口，丢掉贴边分屏和双击最大化并不疼。
    ///
    /// 半径 18 与岛体展开态同级；描边只有 #1F 一档，够勾出边界又不抢戏。
    /// </summary>
    private void UpdateShape()
    {
        var w = ActualWidth;
        var h = ActualHeight;
        if (w < 2 || h < 2) return;

        // 内缩半个描边宽度，否则 1px 的线有一半画在几何之外、被窗口边缘切掉
        Backdrop.Data = Rendering.Squircle.Build(new Rect(0.5, 0.5, w - 1, h - 1), 18);
    }

    /// <summary>
    /// 标题栏拖动。<see cref="Window.DragMove"/> 必须在按下的那一刻调用 ——
    /// 它内部靠 WM_NCLBUTTONDOWN 接管，晚一步（比如在 MouseMove 里）会抛异常。
    /// </summary>
    private void OnTitleBarDrag(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left) return;

        try { DragMove(); }
        catch (InvalidOperationException) { /* 按键已被释放，忽略 */ }
    }

    /// <summary>
    /// 高度不超过工作区。默认 880 在本机（1280 DIP 高）绰绰有余，
    /// 但小屏或缩放更大的机器上会顶出屏幕 —— 而这个窗口不可调整大小，
    /// 顶出去就等于底部那几节永远看不到。
    /// </summary>
    private void FitToWorkArea()
    {
        var limit = SystemParameters.WorkArea.Height - 60;
        if (limit > 320 && Height > limit) Height = limit;
    }

    // ================= 读取当前配置 =================

    private void LoadFromConfig()
    {
        var c = _bridge.Config;

        DotModeBox.IsChecked = c.DotMode;
        PulseBox.IsChecked = c.PulseEnabled;
        LyricsBox.IsChecked = c.LyricsEnabled;
        WeatherBox.IsChecked = c.WeatherEnabled;
        PhiraBox.IsChecked = c.PhiraEnabled;

        BuildSegments();
        SelectSegment(c.LyricTranslation, animate: false);

        LatBox.Text = Fmt(c.Latitude);
        LonBox.Text = Fmt(c.Longitude);
        RegionBox.Text = c.WeatherRegion ?? "";

        AutoStartBox.IsChecked = _bridge.AutoStartEnabled();
        StartMenuBox.IsChecked = _bridge.StartMenuRegistered();

        ShowHotkey(HotkeyKind.Expand);
        ShowHotkey(HotkeyKind.Dot);

        BuildProviderList();
    }

    /// <summary>坐标用不变文化格式化。跟着系统区域走会写出 "45,80" 这种用逗号做小数点的值。</summary>
    private static string Fmt(double? value)
        => value is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "";

    private void BuildProviderList()
    {
        ProviderList.Children.Clear();

        foreach (var provider in _bridge.Providers)
        {
            var box = new CheckBox
            {
                Content = provider.Label,
                IsChecked = _bridge.Config.IsProviderEnabled(provider.Id),
                Margin = new Thickness(0, 0, 0, 10),
            };

            var set = provider.Set;
            box.Checked += (_, _) => { if (!_loading) set(true); };
            box.Unchecked += (_, _) => { if (!_loading) set(false); };

            ProviderList.Children.Add(box);
        }
    }

    // ================= 接线 =================

    private void WireUp()
    {
        Toggle(DotModeBox, _bridge.SetDotMode);
        Toggle(PulseBox, _bridge.SetPulse);
        Toggle(LyricsBox, _bridge.SetLyrics);
        Toggle(WeatherBox, _bridge.SetWeather);
        Toggle(PhiraBox, _bridge.SetPhira);

        // 位置三项一起提交：坐标与行政区是一组，分开写会出现
        // "坐标已换、行政区还是上一个地方"的中间状态，而预警就是按行政区筛的
        LatBox.LostFocus += (_, _) => CommitLocation();
        LonBox.LostFocus += (_, _) => CommitLocation();
        RegionBox.LostFocus += (_, _) => CommitLocation();
        LatBox.KeyDown += CommitOnEnter;
        LonBox.KeyDown += CommitOnEnter;
        RegionBox.KeyDown += CommitOnEnter;

        DetectButton.Click += async (_, _) => await DetectLocationAsync();

        ExpandHotkeyBox.PreviewKeyDown += (s, e) => CaptureHotkey(HotkeyKind.Expand, e);
        DotHotkeyBox.PreviewKeyDown += (s, e) => CaptureHotkey(HotkeyKind.Dot, e);

        AutoStartBox.Checked += (_, _) => CommitAutoStart(true);
        AutoStartBox.Unchecked += (_, _) => CommitAutoStart(false);

        StartMenuBox.Checked += (_, _) => CommitStartMenu(true);
        StartMenuBox.Unchecked += (_, _) => CommitStartMenu(false);

        OpenFolderButton.Click += (_, _) => _bridge.OpenConfigFolder();
        CloseButton.Click += (_, _) => Close();

        // 自绘窗口要自己接这几样
        TitleBar.MouseLeftButtonDown += OnTitleBarDrag;
        SizeChanged += (_, _) => UpdateShape();

        // Esc 关闭。系统标题栏没了，键盘途径得自己给 ——
        // 而这个窗口是可激活的（不像岛体），所以收得到键盘事件
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;

            // 正在录热键时 Esc 该被录入路径吃掉，不该关窗
            if (ExpandHotkeyBox.IsKeyboardFocusWithin || DotHotkeyBox.IsKeyboardFocusWithin) return;

            Close();
            e.Handled = true;
        };
    }

    private void Toggle(CheckBox box, Action<bool> set)
    {
        box.Checked += (_, _) => { if (!_loading) set(true); };
        box.Unchecked += (_, _) => { if (!_loading) set(false); };
    }

    private void CommitOnEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        CommitLocation();
        e.Handled = true;
    }

    // ================= 分段选择器 =================
    //
    // 三选一用一枚**滑动的白胶囊**，而不是三个各自亮灭的背景。
    // 后者会撞上和开关旋钮一样的问题：淡出与淡入交叉时，中途是两个半亮的白胶囊，
    // 比不做动画难看。滑动只有一个元素在动，不存在交叉。
    //
    // 文字的对比度这样解决：**指示器自己带一份暗色的选中标签**，居中放在胶囊里。
    // 亮字那一层铺满整行、被不透明的胶囊盖住它经过的那一段，
    // 于是"胶囊上是暗字、胶囊外是亮字"任何时刻都成立 ——
    // 而如果只是在点击时翻一下前景色，胶囊滑过的那 240ms 里字一定有一段看不清。
    //
    // 第一版是"整行暗字层 + 按胶囊裁剪 + 反向平移"。思路没错，
    // 但两条互相抵消的位移动画一旦对不上，暗字就整个跑到裁剪区外 ——
    // 实测就是这样：落位后胶囊里只剩透过 93% 白露出来的一点亮字，几乎看不见。
    // 现在只放一份居中的标签：宽度与该段相同，居中就自然对齐，
    // **没有需要保持同步的第二个量**。少一个耦合，少一类 bug。

    private static readonly (LyricTranslationMode Mode, string Label)[] SegmentItems =
    [
        (LyricTranslationMode.Both, "原文 + 译文"),
        (LyricTranslationMode.TranslationOnly, "只显示译文"),
        (LyricTranslationMode.OriginalOnly, "只显示原文"),
    ];

    private const double SegFontSize = 12.5;
    private static readonly Thickness SegPadding = new(14, 7, 14, 7);

    private StackPanel? _segLight, _segHit;
    private Border? _segIndicator;
    private TextBlock? _segLabel;
    private TranslateTransform? _segShift;
    private LyricTranslationMode _segCurrent = LyricTranslationMode.Both;

    private void BuildSegments()
    {
        if (_segLight is not null) return;   // 只搭一次

        SegRoot.Children.Clear();

        _segLight = MakeLabelRow(Frozen(0xA6, 0xFF, 0xFF, 0xFF));

        _segLabel = new TextBlock
        {
            Foreground = Frozen(0xFF, 0x0F, 0x0F, 0x13),
            FontFamily = (FontFamily)FindResource("UiFont"),
            FontSize = SegFontSize,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _segShift = new TranslateTransform();

        _segIndicator = new Border
        {
            Background = Frozen(0xED, 0xFF, 0xFF, 0xFF),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = _segShift,
            Child = _segLabel,
        };

        _segHit = new StackPanel { Orientation = Orientation.Horizontal };

        foreach (var (mode, label) in SegmentItems)
        {
            var picked = mode;
            var cell = new Border
            {
                Background = Brushes.Transparent,
                Padding = SegPadding,
                Cursor = System.Windows.Input.Cursors.Hand,
                Child = new TextBlock
                {
                    // 放一份同样的文字（全透明）撑出同样的宽度 ——
                    // 命中区若比可见层窄，点边缘就没反应；若宽，就会点到隔壁
                    Text = label,
                    FontFamily = (FontFamily)FindResource("UiFont"),
                    FontSize = SegFontSize,
                    Opacity = 0,
                },
            };

            cell.MouseLeftButtonDown += (_, _) =>
            {
                if (_segCurrent == picked) return;
                SelectSegment(picked, animate: true);
                if (!_loading) _bridge.SetLyricTranslation(picked);
            };

            _segHit.Children.Add(cell);
        }

        SegRoot.Children.Add(_segLight);
        SegRoot.Children.Add(_segIndicator);
        SegRoot.Children.Add(_segHit);
    }

    private StackPanel MakeLabelRow(Brush foreground)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        foreach (var (_, label) in SegmentItems)
        {
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = foreground,
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = SegFontSize,
                Margin = SegPadding,
            });
        }

        return row;
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// 选中某一段。<paramref name="animate"/> 为 false 时瞬间就位 ——
    /// 打开窗口那一下不该看到指示器从头飞过来。
    /// </summary>
    private void SelectSegment(LyricTranslationMode mode, bool animate)
    {
        _segCurrent = mode;

        var index = Array.FindIndex(SegmentItems, x => x.Mode == mode);
        if (index < 0 || _segLabel is null) return;

        // 标签在动画**开始时**就换成目标那一段：飞过去的胶囊带着目的地的文字，
        // 到位时不需要再闪一下换字
        _segLabel.Text = SegmentItems[index].Label;

        // 目标位置要等布局算完才知道。窗口刚打开时 ActualWidth 还是 0，
        // 直接读会得到 0，指示器缩成一条线
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            () => ApplySegment(index, animate));
    }

    private void ApplySegment(int index, bool animate)
    {
        if (_segHit is null || _segIndicator is null || _segShift is null) return;
        if (index >= _segHit.Children.Count) return;

        var cell = (FrameworkElement)_segHit.Children[index];
        if (cell.ActualWidth < 1) return;

        var left = cell.TranslatePoint(new Point(0, 0), _segHit).X;
        var width = cell.ActualWidth;

        _segIndicator.Height = cell.ActualHeight;

        if (!animate)
        {
            _segIndicator.BeginAnimation(FrameworkElement.WidthProperty, null);
            _segShift.BeginAnimation(TranslateTransform.XProperty, null);

            _segIndicator.Width = width;
            _segShift.X = left;
            return;
        }

        // X 用 BackEase：它顺着行进方向掠过终点再回来，方向自动正确，
        // 不必判断是往左还是往右。幅度 0.18 在 90–190px 的行程上约 4–8px，
        // 和开关那 17% 同一个道理 —— 小元件要更大的相对过冲才看得出弹性
        var slide = new DoubleAnimation
        {
            To = left,
            Duration = TimeSpan.FromMilliseconds(240),
            EasingFunction = new BackEase { Amplitude = 0.18, EasingMode = EasingMode.EaseOut },
        };

        // 宽度不过冲：三段宽度不同，换段时胶囊本来就要伸缩，
        // 再让宽度弹一下会像在"喘气"。位移弹就够了
        var resize = new DoubleAnimation
        {
            To = width,
            Duration = slide.Duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        _segShift.BeginAnimation(TranslateTransform.XProperty, slide);
        _segIndicator.BeginAnimation(FrameworkElement.WidthProperty, resize);
    }

    // ================= 位置 =================

    private void CommitLocation()
    {
        if (_loading) return;

        var lat = ParseCoord(LatBox.Text);
        var lon = ParseCoord(LonBox.Text);
        var region = string.IsNullOrWhiteSpace(RegionBox.Text) ? null : RegionBox.Text.Trim();

        // 只有一个坐标有值等于没有：定位需要成对的经纬度。
        // 不成对时当作"没填"，回到系统定位，而不是拿半个坐标去查天气
        if (lat is null || lon is null)
        {
            lat = null;
            lon = null;
        }

        _bridge.SetLocation(lat, lon, region);

        // 回写规范化后的文本：用户可能填了 "45.80 " 或非法内容，
        // 让框里显示的和实际生效的一致 —— 否则他以为填上了，其实被丢掉了
        LatBox.Text = Fmt(lat);
        LonBox.Text = Fmt(lon);
        RegionBox.Text = region ?? "";
    }

    private static double? ParseCoord(string? text)
        => double.TryParse(
            (text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;

    /// <summary>
    /// 现在就跑一次定位 + 反查行政区，把结果填进输入框。
    ///
    /// 存在的理由是"手填位置"这件事门槛不低：多数人不知道自己的经纬度，
    /// 也不知道该怎么写行政区才能匹配上预警。检测一次给出一个能用的起点，
    /// 之后想模糊化就自己改粗。填进框里而不是直接生效 ——
    /// 让用户看清即将保存的是什么，再决定要不要留。
    /// </summary>
    private async Task DetectLocationAsync()
    {
        DetectButton.IsEnabled = false;
        Status(DetectStatus, "正在定位…", ok: true);

        try
        {
            // 传 null 表示忽略已填的手填值，真的去问系统 ——
            // 否则填过一次之后这个按钮就永远只是把旧值念回来
            var here = await GeoLocation.TryResolveAsync(null, null);

            if (here is not { } coord)
            {
                Status(DetectStatus,
                    $"定位失败（{GeoLocation.LastError}）。检查系统设置 → 隐私和安全性 → 位置，"
                    + "打开「位置服务」与「让桌面应用访问你的位置」。",
                    ok: false);
                return;
            }

            // 显示与保存都用降精度后的值。这里没必要拿到米级精度，
            // 而 2 位小数（约 1km）对天气足够 —— 见 GeoLocation.Coordinate.Rounded
            var rounded = coord.Rounded();
            LatBox.Text = Fmt(rounded.Latitude);
            LonBox.Text = Fmt(rounded.Longitude);

            Status(DetectStatus, $"已定位到 {rounded.Latitude:F2}, {rounded.Longitude:F2}（来源 {coord.Source}）。正在查行政区…", ok: true);

            _http ??= new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            var region = await GeoLocation.TryResolveRegionAsync(_http, coord);

            if (region is { } r)
            {
                // 写 ToConfigText 而不是 ToString：配置文本必须能被
                // Weather.ParseRegionText 原样读回去（往返性由自检守着）
                RegionBox.Text = r.ToConfigText();
                Status(DetectStatus,
                    $"已填入 {rounded.Latitude:F2}, {rounded.Longitude:F2} · {r}。"
                    + "确认后点别处或按回车保存；想模糊化可以直接改粗。",
                    ok: true);
            }
            else
            {
                Status(DetectStatus,
                    $"坐标已填入，但行政区没查到（{GeoLocation.LastError}）。可以手填，形如「辽宁省沈阳市」。",
                    ok: false);
            }
        }
        catch (Exception ex)
        {
            Status(DetectStatus, $"定位出错：{ex.GetType().Name}", ok: false);
        }
        finally
        {
            DetectButton.IsEnabled = true;
        }
    }

    // ================= 热键 =================

    private void ShowHotkey(HotkeyKind kind)
    {
        var label = _bridge.HotkeyLabel(kind);
        var box = kind == HotkeyKind.Expand ? ExpandHotkeyBox : DotHotkeyBox;
        var status = kind == HotkeyKind.Expand ? ExpandHotkeyStatus : DotHotkeyStatus;

        box.Text = label ?? "（无）";

        if (label is null)
        {
            Status(status, "全部候选都被占用了，请自己指定一组", ok: false);
        }
        else
        {
            Status(status, "已注册，按一下试试", ok: true);
        }
    }

    /// <summary>
    /// 捕获组合键。只在按下"非修饰键"时成组提交 ——
    /// 边按边提交的话，按 Ctrl 那一下就会先提交一个残缺组合。
    /// </summary>
    private void CaptureHotkey(HotkeyKind kind, KeyEventArgs e)
    {
        e.Handled = true;   // 别让空格/回车之类跑去触发按钮

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // 修饰键本身不构成组合，继续等
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System)
        {
            return;
        }

        var mods = Keyboard.Modifiers;

        // 无修饰键的组合一律拒绝：单个字母做全局热键会把这个键在整个系统里吞掉
        if (mods == ModifierKeys.None)
        {
            Status(StatusOf(kind), "至少要带一个 Ctrl / Alt / Shift / Win", ok: false);
            return;
        }

        var text = Describe(mods, key);
        if (text is null)
        {
            Status(StatusOf(kind), "这个键不支持，换一个", ok: false);
            return;
        }

        var applied = _bridge.SetHotkey(kind, text);

        if (applied is null)
        {
            // 注册失败（多半是被别的程序占了）。原来那组仍然有效，如实说清楚
            Status(StatusOf(kind), $"{text} 注册不上，可能已被别的程序占用；仍在用原来那组", ok: false);
            BoxOf(kind).Text = _bridge.HotkeyLabel(kind) ?? "（无）";
            return;
        }

        BoxOf(kind).Text = applied;
        SaveHotkey(kind, applied);

        // 进入"等你按一下"状态。注册成功**不代表按下去有反应**，
        // 这一步才是真正的验证 —— 项目里为此栽过：RegisterHotKey 报成功，
        // 而组合被低级键盘钩子截走，按了毫无动静
        _awaitingTest = kind;
        Status(StatusOf(kind), $"已设为 {applied} —— 现在按一下它，验证真的能用", ok: true);
        _testTimeout.Stop();
        _testTimeout.Start();
    }

    private void SaveHotkey(HotkeyKind kind, string label)
    {
        if (kind == HotkeyKind.Expand) _bridge.Config.ExpandHotkey = label;
        else _bridge.Config.DotHotkey = label;

        _bridge.Config.Save();
    }

    private void OnHotkeyFired(HotkeyKind kind)
    {
        // 事件来自岛体窗口的 WndProc，不一定在 UI 线程上
        Dispatcher.BeginInvoke(() =>
        {
            if (_awaitingTest != kind) return;
            FinishTest(fired: true);
        });
    }

    private void FinishTest(bool fired)
    {
        _testTimeout.Stop();

        if (_awaitingTest is not { } kind) return;
        _awaitingTest = null;

        Status(StatusOf(kind),
            fired
                ? "✓ 收到了，这组热键可用"
                : "没收到 —— 注册是成功的，但按键被别的程序截走了。换一组试试。",
            ok: fired);
    }

    private TextBlock StatusOf(HotkeyKind kind)
        => kind == HotkeyKind.Expand ? ExpandHotkeyStatus : DotHotkeyStatus;

    private System.Windows.Controls.TextBox BoxOf(HotkeyKind kind)
        => kind == HotkeyKind.Expand ? ExpandHotkeyBox : DotHotkeyBox;

    /// <summary>
    /// 把 WPF 的按键描述成 <see cref="Hotkey"/> 能解析回去的文本。
    /// 两边必须能来回转 —— 存进配置的是这个文本，重启后由 Hotkey.TryParse 读。
    /// </summary>
    private static string? Describe(ModifierKeys mods, Key key)
    {
        var name = KeyName(key);
        if (name is null) return null;

        var parts = new List<string>(4);
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);

        return string.Join("+", parts);
    }

    private static string? KeyName(Key key) => key switch
    {
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.F1 and <= Key.F24 => key.ToString(),
        Key.Space => "Space",
        Key.Enter => "Enter",
        Key.Tab => "Tab",
        Key.Escape => "Esc",
        Key.Back => "Backspace",
        Key.Insert => "Insert",
        Key.Delete => "Delete",
        Key.Home => "Home",
        Key.End => "End",
        Key.PageUp => "PageUp",
        Key.PageDown => "PageDown",
        Key.Up => "Up",
        Key.Down => "Down",
        Key.Left => "Left",
        Key.Right => "Right",
        _ => null,
    };

    // ================= 其它 =================

    private void CommitStartMenu(bool enabled)
    {
        if (_loading) return;

        // 和自启同一套：失败就把勾回弹并说明。这里失败多半是目录写不了
        // （安全软件盯着开始菜单目录，那是常见的驻留位置）
        if (_bridge.SetStartMenu(enabled))
        {
            StartMenuStatus.Visibility = Visibility.Collapsed;
            return;
        }

        _loading = true;
        StartMenuBox.IsChecked = !enabled;
        _loading = false;

        Status(StartMenuStatus, "写不进开始菜单 —— 可能被安全软件挡住了", ok: false);
    }

    private void CommitAutoStart(bool enabled)
    {
        if (_loading) return;

        // 写注册表可能被组策略挡住。失败就把勾回弹并说明 ——
        // 否则界面显示已开启、实际没生效，用户是被骗了
        if (_bridge.SetAutoStart(enabled))
        {
            AutoStartStatus.Visibility = Visibility.Collapsed;
            return;
        }

        _loading = true;
        AutoStartBox.IsChecked = !enabled;
        _loading = false;

        Status(AutoStartStatus, "设置失败 —— 可能被组策略或安全软件挡住了", ok: false);
    }

    /// <summary>正常状态用近白（与整页同一档），出错才上色。</summary>
    private static readonly System.Windows.Media.Brush OkBrush = Freeze(0xC4, 0xFF, 0xFF, 0xFF);
    private static readonly System.Windows.Media.Brush BadBrush = Freeze(0xE8, 0xF2, 0xA0, 0xA0);

    private static System.Windows.Media.Brush Freeze(byte a, byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void Status(TextBlock target, string text, bool ok)
    {
        target.Text = text;
        target.Foreground = ok ? OkBrush : BadBrush;
        target.Visibility = Visibility.Visible;
    }
}
