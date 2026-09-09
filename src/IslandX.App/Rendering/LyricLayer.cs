using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace IslandX.Rendering;

/// <summary>
/// 歌词的一层（换句时两层轮流承担进场 / 退场）。自绘而不是 TextBlock + BlurEffect。
///
/// 理由是 WPF 的 <see cref="System.Windows.Media.Effects.BlurEffect"/> 只有一个各向同性的
/// <c>Radius</c>，做不出**只在水平方向**的模糊 —— 而那正是"横向拖影"观感的来源：
/// 垂直方向保持清晰，文字不是"失焦"，是"横着被抹开"。两者观感差别很大，
/// 各向同性模糊看着像没对上焦，方向性模糊才有速度感。
///
/// 实现是把同一行字画 N 份、各自水平偏移并按高斯权重分配不透明度，
/// 也就是手工做一次一维卷积。听起来贵，实际上：
/// - 采样点固定 2px 一个（<see cref="SampleStepDip"/>），份数随模糊强度伸缩，不模糊时只画一份；
/// - <see cref="FormattedText"/> 缓存着，N 份画的是同一组已排版好的 glyph；
/// - 只在换句那 ~300ms 里有多份。
///
/// 附带的好处：常态（不模糊）走的是**一次普通 DrawText**，没有 Effect 挂在元素上，
/// 因此不会像 BlurEffect 那样把 ClearType 降级成灰度抗锯齿。
/// </summary>
internal sealed class LyricLayer : FrameworkElement
{
    /// <summary>
    /// 采样间隔（DIP）。决定模糊看起来是"连续的"还是"几道重影"。
    ///
    /// 2px 是按内容定的，不是拍脑袋：中文笔画间距约 2–3px，
    /// 采样间隔一旦接近或超过它，叠加出来的就不是模糊而是能数得出来的重复笔画。
    /// 份数随 sigma 伸缩、间隔恒定，比"固定份数、间隔随 sigma 变大"稳得多。
    /// </summary>
    private const double SampleStepDip = 2.0;

    /// <summary>单侧采样范围取几倍 sigma。2σ 覆盖高斯 95% 的能量，再往外权重已可忽略。</summary>
    private const double SigmaSpan = 2.0;

    /// <summary>采样份数上限。sigma=12 时正好 25 份，再大也不必更密。</summary>
    private const int MaxSamples = 25;

    /// <summary>低于这个 sigma 就当作不模糊，直接画一份。</summary>
    private const double BlurEpsilon = 0.35;

    private FormattedText? _formatted;
    private FormattedText? _formattedSub;
    private string _text = "";
    private string _sub = "";
    private double _fontSize = 14;
    private double _pixelsPerDip = 1;
    private double _blurSigma;

    /// <summary>文字画刷。须 Freeze。</summary>
    internal Brush? Foreground { get; set; }

    /// <summary>字体。</summary>
    internal FontFamily? FontFamily { get; set; }

    /// <summary>超过这个宽度就截断加省略号。</summary>
    internal double MaxTextWidth { get; set; } = 340;

    /// <summary>第二行（译文）的画刷，比主行暗。须 Freeze。</summary>
    internal Brush? SubForeground { get; set; }

    /// <summary>第二行相对主行的字号比例。</summary>
    internal double SubScale { get; set; } = 0.82;

    /// <summary>两行之间的间距（DIP）。</summary>
    internal double LineGap { get; set; } = 2;

    /// <summary>当前有没有第二行。岛体据此决定要不要变高。</summary>
    internal bool HasSub => _formattedSub is not null;

    /// <summary>当前这层显示的文字。</summary>
    internal string Text => _text;

    /// <summary>
    /// 已排版的文字宽度（DIP），双行时取**较宽的那一行**。<c>0</c> 表示没有文字。
    /// 取 max 而不是主行宽度：译文常比原文长（"I don't wanna go" → "我不想走"反过来也有），
    /// 只按主行算的话译文会溢出岛体被裁掉。
    /// </summary>
    internal double TextWidth => Math.Max(_formatted?.Width ?? 0, _formattedSub?.Width ?? 0);

    /// <summary>当前的水平模糊强度。</summary>
    internal double BlurSigma => _blurSigma;

    /// <summary>
    /// 本句显示以来出现过的最大模糊强度，<see cref="SetText"/> 时归零。
    ///
    /// 存在的理由和 <c>SpringValue.PeakOvershoot</c> 一样：模糊只在换句那 100ms 里
    /// 由大变小，而探针限流 20fps，瞬时值**采不到峰**——读到 0.0 分不清是
    /// "已经走完了"还是"压根没模糊过"。峰值是累积量，任何采样频率都读得到。
    /// </summary>
    internal double PeakBlur { get; private set; }

    /// <summary>
    /// 换一句。会重建 <see cref="FormattedText"/>（排版一次），所以只在真的换句时调，
    /// 不要每帧调 —— 每帧变的是模糊与位移，那些不需要重新排版。
    /// </summary>
    internal void SetText(string text, string? sub, double fontSize, double pixelsPerDip)
    {
        var subText = sub ?? "";

        if (_text == text && _sub == subText && Math.Abs(_fontSize - fontSize) < 0.01
            && Math.Abs(_pixelsPerDip - pixelsPerDip) < 0.001)
        {
            return;
        }

        _text = text;
        _sub = subText;
        _fontSize = fontSize;
        _pixelsPerDip = pixelsPerDip;
        _formatted = null;
        _formattedSub = null;
        PeakBlur = 0;

        if (text.Length > 0) _formatted = Build(text, fontSize, pixelsPerDip, Foreground);

        if (subText.Length > 0)
        {
            _formattedSub = Build(
                subText, fontSize * SubScale, pixelsPerDip, SubForeground ?? Foreground);
        }

        InvalidateVisual();
    }

    /// <summary>
    /// 设置水平模糊强度（DIP）。每帧调，只重绘不重排版。
    ///
    /// 阈值 0.25 不是随手写的：<see cref="InvalidateVisual"/> 会让整个
    /// <see cref="OnRender"/> 重跑、十几个 DrawText 指令重建，而这是每帧都在调的。
    /// 模糊从峰值退到 0 大约跨 60 帧，每帧变化约 0.1 —— 阈值取 0.02 等于每帧都重建。
    /// 0.25 让重建降到约三分之一，而 sigma 差 0.25 在 6px 的模糊上肉眼分辨不出。
    /// 跳过的那几帧画的是上一次的 drawing，视觉上没有区别。
    /// </summary>
    internal void SetBlur(double sigma)
    {
        // 峰值先记，且不受下面的阈值影响 —— 阈值只是省重绘，不该让诊断也跟着失真
        if (sigma > PeakBlur) PeakBlur = sigma;

        // 归零必须精确落地，不能被阈值挡掉 —— 那会让动画停在"还剩一点糊"的状态
        var settled = sigma <= 0 && _blurSigma > 0;
        if (!settled && Math.Abs(_blurSigma - sigma) < 0.25) return;

        _blurSigma = sigma;
        InvalidateVisual();
    }

    private FormattedText Build(string text, double fontSize, double pixelsPerDip, Brush? brush)
    {
        var typeface = new Typeface(
            FontFamily ?? new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            brush ?? Brushes.White,
            pixelsPerDip)
        {
            MaxTextWidth = MaxTextWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
    }

    protected override void OnRender(DrawingContext dc)
    {
        var text = _formatted;
        if (text is null || _text.Length == 0) return;

        // 整层已经透明就别画了。WPF 在 Opacity=0 时照样会调 OnRender，
        // 而这里最多要画 25 份 —— 换句结束后退场层正是这个状态（透明但模糊值仍是峰值）
        if (Opacity < 0.004) return;

        var sub = _formattedSub;

        // 两行时整体居中：总高 = 主行 + 间距 + 次行，从中间往上退一半。
        // 只把主行居中再往下挂次行的话，双行内容整体会偏下、上边挤着轮廓
        var totalHeight = sub is null ? text.Height : text.Height + LineGap + sub.Height;
        var top = (ActualHeight - totalHeight) / 2;

        // 每行各自水平居中 —— 译文与原文长度差得多，左对齐会看着像没对齐
        var x = (ActualWidth - text.Width) / 2;
        var y = top;
        var subX = sub is null ? 0 : (ActualWidth - sub.Width) / 2;
        var subY = top + text.Height + LineGap;

        if (_blurSigma < BlurEpsilon)
        {
            dc.DrawText(text, new Point(x, y));
            if (sub is not null) dc.DrawText(sub, new Point(subX, subY));
            return;
        }

        // 一维高斯卷积：份数随 sigma 伸缩，间隔恒定 2px
        var half = Math.Min(MaxSamples / 2, (int)Math.Round(_blurSigma * SigmaSpan / SampleStepDip));
        if (half <= 0)
        {
            dc.DrawText(text, new Point(x, y));
            if (sub is not null) dc.DrawText(sub, new Point(subX, subY));
            return;
        }

        var twoSigmaSq = 2 * _blurSigma * _blurSigma;

        // 先算归一化系数，否则模糊越强整体越暗 ——
        // 那会让"淡出"里混进一段本不该有的变暗，两个效果纠缠在一起说不清
        var total = 0.0;
        for (var i = -half; i <= half; i++)
        {
            var d = i * SampleStepDip;
            total += Math.Exp(-d * d / twoSigmaSq);
        }

        if (total <= 0)
        {
            dc.DrawText(text, new Point(x, y));
            if (sub is not null) dc.DrawText(sub, new Point(subX, subY));
            return;
        }

        for (var i = -half; i <= half; i++)
        {
            var d = i * SampleStepDip;
            var weight = Math.Exp(-d * d / twoSigmaSq) / total;

            // 权重太低的那几份画了也看不见，跳过省几次填充
            if (weight < 0.004) continue;

            // 每份都走 DrawText。试过改成「预制 alpha 画刷 + DrawGeometry」想省掉
            // PushOpacity 的合成节点，结果**大幅变差**：CPU 21.5% → 34%、
            // 帧率 178 → 149fps。DrawText 用的是字形位图缓存（一次 GPU 纹理 blit），
            // 而 DrawGeometry 每份都要矢量填充（tessellation + 光栅化），
            // 十几份下来完全盖过省掉的那点合成开销。PushOpacity 不是瓶颈。
            dc.PushOpacity(weight);
            dc.DrawText(text, new Point(x + d, y));
            if (sub is not null) dc.DrawText(sub, new Point(subX + d, subY));
            dc.Pop();
        }
    }

    /// <summary>歌词不接收鼠标 —— 点击要落到岛体上去切换展开。</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}
