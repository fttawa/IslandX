using System.Windows;
using System.Windows.Media;

namespace IslandX.Rendering;

/// <summary>
/// 收起态右侧的频谱竖条。
///
/// 自绘而不是摆 6 个 Rectangle：每帧都要改高度，走布局系统的话
/// 每次都会触发一轮 Measure/Arrange —— 那正是形变期最贵的开销。
/// 这里尺寸固定，只 InvalidateVisual 重画。
/// </summary>
internal sealed class SpectrumBars : FrameworkElement
{
    private const double BarWidth = 3;
    private const double BarGap = 2;
    private const double MinBarHeight = 3;

    private float[] _values = [];
    private Brush? _brush;

    /// <summary>条数由数据决定；控件宽度据此算出。</summary>
    internal static double WidthFor(int bars) => (bars * BarWidth) + ((bars - 1) * BarGap);

    /// <summary>竖条的最大高度。</summary>
    internal double MaxBarHeight { get; set; } = 16;

    /// <summary>用封面主色画条，null 时回落为半透明白。</summary>
    internal void SetAccent(Color? accent)
    {
        var color = accent ?? Color.FromRgb(220, 224, 232);
        var brush = new LinearGradientBrush(
            Color.FromArgb(0xFF, color.R, color.G, color.B),
            Color.FromArgb(0xB0, color.R, color.G, color.B),
            new Point(0, 1),
            new Point(0, 0));

        brush.Freeze();
        _brush = brush;
        InvalidateVisual();
    }

    internal void SetValues(float[] values)
    {
        _values = values;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_values.Length == 0 || _brush is null) return;

        var totalWidth = WidthFor(_values.Length);
        var startX = (RenderSize.Width - totalWidth) / 2;
        var centerY = RenderSize.Height / 2;
        var radius = BarWidth / 2;

        for (var i = 0; i < _values.Length; i++)
        {
            // 静止时留一排小圆点，而不是整排消失 —— 位置感比"有没有"更重要
            var height = Math.Max(MinBarHeight, _values[i] * MaxBarHeight);
            var x = startX + (i * (BarWidth + BarGap));

            drawingContext.DrawRoundedRectangle(
                _brush,
                null,
                new Rect(x, centerY - (height / 2), BarWidth, height),
                radius,
                radius);
        }
    }

    /// <summary>纯装饰，不参与命中测试。</summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) => null;
}
