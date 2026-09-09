using System.Windows;
using System.Windows.Media;

namespace IslandX.Rendering;

/// <summary>
/// 展开态里的迷你柱状图。天气用它画未来几小时的降水预报。
///
/// 与 <see cref="SpectrumBars"/> 分开而不是复用：那个是**每帧**跟着 FFT 跳的，
/// 有峰值跟踪、上升快下降慢的平滑、静止时的圆点占位；这个是一组静态数值，
/// 几分钟才换一次。硬凑到一起的话，两边的"下一步要做什么"会互相拖累。
/// </summary>
internal sealed class MiniChart : FrameworkElement
{
    /// <summary>柱子之间的间隙占单格宽度的比例。</summary>
    private const double GapRatio = 0.34;

    /// <summary>
    /// 零值柱子的可见高度（DIP）。
    ///
    /// 不画成零高：那样"没有降水"的时段会整段消失，柱状图变成断断续续的几根，
    /// 读不出"从哪一格开始下"。留一条底座，横轴才连得起来。
    /// </summary>
    private const double FloorHeight = 2.5;

    private double[] _values = [];

    /// <summary>柱子画刷。须 Freeze。</summary>
    internal Brush? BarBrush { get; set; }

    /// <summary>零值底座的画刷，比柱子暗。须 Freeze。</summary>
    internal Brush? FloorBrush { get; set; }

    /// <summary>
    /// 高亮从第几格开始（含）到第几格结束（含）。<c>-1</c> 表示不高亮。
    /// 用来标出"这场雨"的区间 —— 光看柱子高低分不清哪几格算作一场。
    /// </summary>
    internal int HighlightFrom { get; set; } = -1;
    internal int HighlightTo { get; set; } = -1;

    /// <summary>换一组数据。值域 0–1，超出会被夹住。</summary>
    internal void SetValues(IReadOnlyList<double>? values)
    {
        if (values is null || values.Count == 0)
        {
            if (_values.Length == 0) return;
            _values = [];
            InvalidateVisual();
            return;
        }

        if (_values.Length != values.Count) _values = new double[values.Count];

        for (var i = 0; i < values.Count; i++) _values[i] = Math.Clamp(values[i], 0, 1);

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_values.Length == 0) return;

        var w = ActualWidth;
        var h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        var slot = w / _values.Length;
        var barWidth = Math.Max(1.5, slot * (1 - GapRatio));
        var radius = Math.Min(barWidth / 2, 2.5);

        for (var i = 0; i < _values.Length; i++)
        {
            var value = _values[i];

            // 底座保证空时段也看得见，横轴才连得起来
            var barHeight = Math.Max(FloorHeight, value * h);
            var x = (i * slot) + ((slot - barWidth) / 2);
            var y = h - barHeight;

            var inRange = HighlightFrom >= 0 && i >= HighlightFrom && i <= HighlightTo;
            var brush = value <= 0.001 && !inRange ? FloorBrush : BarBrush;
            if (brush is null) continue;

            dc.DrawRoundedRectangle(
                brush, null,
                new Rect(x, y, barWidth, barHeight),
                radius, radius);
        }
    }

    /// <summary>图表不接收鼠标 —— 点击要落到岛体上去。</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}
