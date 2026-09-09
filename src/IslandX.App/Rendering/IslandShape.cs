using System.Windows;
using System.Windows.Media;

namespace IslandX.Rendering;

/// <summary>
/// 岛体轮廓的绘制元素。
///
/// 存在的唯一理由是**绕开布局系统**：<see cref="System.Windows.Shapes.Path"/> 的
/// MeasureOverride 依赖 Data 的包围盒，每帧换几何就会触发一次 InvalidateMeasure，
/// 而形变期间 WPF 的完整布局传递（含所有 TextBlock 的文本测量）是最贵的一项开销。
///
/// 这里尺寸由父容器固定给定，几何变化只调 InvalidateVisual —— 只重绘，不重新布局。
/// </summary>
internal sealed class IslandShape : FrameworkElement
{
    private Geometry? _fillGeometry;
    private Geometry? _strokeGeometry;

    /// <summary>岛体填充色。</summary>
    internal Brush? ShapeFill { get; set; }

    /// <summary>描边画笔（须 Freeze）。</summary>
    internal Pen? ShapePen { get; set; }

    /// <summary>
    /// 更新轮廓。填充与描边分两条几何：描边那条内缩半像素，
    /// 让 1px 线落在像素中心而不发虚。
    /// </summary>
    internal void SetGeometry(Geometry? fill, Geometry? stroke)
    {
        _fillGeometry = fill;
        _strokeGeometry = stroke;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_fillGeometry is not null && ShapeFill is not null)
            drawingContext.DrawGeometry(ShapeFill, null, _fillGeometry);

        if (_strokeGeometry is not null && ShapePen is not null)
            drawingContext.DrawGeometry(null, ShapePen, _strokeGeometry);
    }

    /// <summary>
    /// 只有落在轮廓内的点才算命中。
    /// 默认实现按布局边界判定，而这个元素的边界是整块恒定画布（远大于岛体），
    /// 那会让岛体四周的透明区域也拦下鼠标、挡住下方窗口。
    /// </summary>
    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        if (_fillGeometry?.FillContains(hitTestParameters.HitPoint) == true)
            return new PointHitTestResult(this, hitTestParameters.HitPoint);

        return null;
    }
}
