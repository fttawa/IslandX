using System.Windows;
using System.Windows.Media;

namespace IslandX.Rendering;

/// <summary>
/// G3 连续曲率圆角（squircle）。
///
/// 普通圆角矩形在圆弧与直线的交接处曲率是突变的（G1 连续），眼睛能看出那道"接缝"；
/// G3 连续圆角让曲率及其变化率都平滑过渡，形状更"肉"、更接近 iOS / ColorOS 的观感。
///
/// 每个角由三段构成：
///   ① 前 30% —— 七次多项式混合曲线，从直线平滑过渡到圆弧
///   ② 中间 55° —— 真正的圆弧
///   ③ 后 30% —— ① 的镜像
/// 混合曲线的系数由「位置 / 斜率 / 曲率 / 曲率变化率」四个连续性约束解出，见 <see cref="SolveBlendCoefficients"/>。
/// </summary>
internal static class Squircle
{
    /// <summary>混合段占圆角的比例。0.3 是形状饱满度与平滑度的平衡点。</summary>
    private const double BlendExtent = 0.3;

    /// <summary>混合曲线的折线近似段数。6 段在岛体尺度下已看不出棱角。</summary>
    private const int BlendSegments = 6;

    private static readonly double[] Coefficients;
    private static readonly Point[] BlendPoints;
    private static readonly Point ArcEnd;
    private static readonly double ArcSweepDegrees;

    static Squircle()
    {
        const double extent = BlendExtent;

        // 单位圆上 x = extent 处的位置、斜率、曲率、曲率变化率
        var circleRoot = Math.Sqrt(1 - (extent * extent));
        var position = 1 - circleRoot;
        var slope = extent * extent / circleRoot;
        var curvature = extent * extent / Math.Pow(circleRoot, 3);
        var curvatureRate = 3 * Math.Pow(extent, 4) / Math.Pow(circleRoot, 5);

        Coefficients = SolveBlendCoefficients(position, slope, curvature, curvatureRate);

        BlendPoints = new Point[BlendSegments];
        for (var i = 0; i < BlendSegments; i++)
        {
            var x = extent * (i + 1) / BlendSegments;
            BlendPoints[i] = new Point(x, EvaluateBlend(x));
        }

        // 混合段各吃掉 asin(extent)，中间留给真圆弧
        var arcSweep = (Math.PI / 2) - (2 * Math.Asin(extent));
        ArcSweepDegrees = arcSweep * 180 / Math.PI;
        ArcEnd = new Point(circleRoot, 1 - extent);
    }

    /// <summary>解出七次多项式 t⁴(a₄ + a₅t + a₆t² + a₇t³) 的系数，使其在接缝处 G3 连续。</summary>
    private static double[] SolveBlendCoefficients(
        double position, double slope, double curvature, double curvatureRate) =>
    [
        (35 * position) - (15 * slope) + (2.5 * curvature) - (curvatureRate / 6),
        (-84 * position) + (39 * slope) - (7 * curvature) + (curvatureRate / 2),
        (70 * position) - (34 * slope) + (6.5 * curvature) - (curvatureRate / 2),
        (-20 * position) + (10 * slope) - (2 * curvature) + (curvatureRate / 6),
    ];

    private static double EvaluateBlend(double x)
    {
        var t = Math.Clamp(x / BlendExtent, 0, 1);
        var t2 = t * t;
        return t2 * t2 * (Coefficients[0]
            + (t * (Coefficients[1]
            + (t * (Coefficients[2]
            + (t * Coefficients[3]))))));
    }

    /// <summary>
    /// 构造 G3 圆角矩形。<paramref name="radius"/> 会被夹到宽高的一半以内，
    /// 等于一半时即为完整胶囊形。
    ///
    /// 用 StreamGeometry 而非 PathGeometry：形变过程中每帧都要重建轮廓，
    /// PathGeometry 是 DependencyObject 对象树，四个角的段与点会产生大量分配
    /// （实测形变期间 CPU 占用 53%）；StreamGeometry 内部是紧凑字节流，成本低一个量级。
    ///
    /// 仅在 UI 线程调用 —— 内部复用静态点缓冲。
    /// </summary>
    internal static Geometry Build(Rect rect, double radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return Geometry.Empty;

        radius = Math.Clamp(radius, 0, Math.Min(rect.Width / 2, rect.Height / 2));

        double left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (radius <= 0)
            {
                ctx.BeginFigure(new Point(left, top), true, true);
                ctx.LineTo(new Point(right, top), true, false);
                ctx.LineTo(new Point(right, bottom), true, false);
                ctx.LineTo(new Point(left, bottom), true, false);
            }
            else
            {
                ctx.BeginFigure(new Point(left + radius, top), true, true);

                // 顺时针：上边 → 右上角 → 右边 → 右下角 → 下边 → 左下角 → 左边 → 左上角
                ctx.LineTo(new Point(right - radius, top), true, false);
                AppendCorner(ctx, radius, right - radius, top, 1, 1, false);

                ctx.LineTo(new Point(right, bottom - radius), true, false);
                AppendCorner(ctx, radius, right, bottom - radius, -1, 1, true);

                ctx.LineTo(new Point(left + radius, bottom), true, false);
                AppendCorner(ctx, radius, left + radius, bottom, -1, -1, false);

                ctx.LineTo(new Point(left, top + radius), true, false);
                AppendCorner(ctx, radius, left, top + radius, 1, -1, true);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    // 每个角的点缓冲，避免形变时每帧分配。仅 UI 线程使用。
    private static readonly Point[] EntryBuffer = new Point[BlendSegments];
    private static readonly Point[] ExitBuffer = new Point[BlendSegments];

    /// <summary>
    /// 追加一个角。归一化的 (x,y) ∈ [0,1]² 经 (originX, originY) 与轴向 (signX, signY) 映射到该角，
    /// <paramref name="swapAxes"/> 处理左右两侧角的坐标轴互换，四个角由此共用同一套曲线数据。
    /// </summary>
    private static void AppendCorner(
        StreamGeometryContext ctx,
        double radius,
        double originX, double originY,
        double signX, double signY,
        bool swapAxes)
    {
        Point Map(double x, double y) => swapAxes
            ? new Point(originX + (signX * radius * y), originY + (signY * radius * x))
            : new Point(originX + (signX * radius * x), originY + (signY * radius * y));

        // ① 进入段：直线 → 圆弧的平滑过渡
        for (var i = 0; i < BlendSegments; i++)
            EntryBuffer[i] = Map(BlendPoints[i].X, BlendPoints[i].Y);
        ctx.PolyLineTo(EntryBuffer, true, false);

        // ② 中段：真圆弧（Rust 参考实现用带权 conic 表达，这里直接用 ArcTo）
        ctx.ArcTo(
            Map(ArcEnd.X, ArcEnd.Y),
            new Size(radius, radius),
            0,
            isLargeArc: ArcSweepDegrees > 180,
            SweepDirection.Clockwise,
            true,
            false);

        // ③ 退出段：① 沿对角线的镜像。x == BlendExtent 那点与圆弧终点重合，跳过，
        //    因此恰好填满 BlendSegments 个位置，可以直接把缓冲交出去、不产生分配。
        var count = 0;
        for (var i = BlendSegments - 1; i >= 0; i--)
        {
            var p = BlendPoints[i];
            if (p.X >= BlendExtent) continue;
            ExitBuffer[count++] = Map(1 - p.Y, 1 - p.X);
        }

        ExitBuffer[count] = Map(1, 1);
        ctx.PolyLineTo(ExitBuffer, true, false);
    }
}
