using System.Windows;
using System.Windows.Media;

namespace IslandX.Rendering;

/// <summary>
/// 两个胶囊的**平滑并集**。
///
/// ⚠ 当前**未被岛体使用**。Split 态最初用它把主体和事件圆连成一体，
/// 实际挂上去是个哑铃形，笨重、不像灵动岛 —— 真机的 Split 就是两个分离的形状，
/// 所以改成了独立圆球。这段代码保留下来是因为 M4 还有个 Merged 聚合态，
/// 那才是它真正该用的地方：两个活动融合成一块，而不是并列。
/// 几何本身是验证过的（见 tools/BridgeViz）。
///
/// 硬并集（CombinedGeometry.Union）在两个形状交界处留下尖角，看着是两块拼在一起，
/// 不是一团液体。要的是 metaball 那种效果：靠近时先长出一道细腰，再逐渐融成一体。
///
/// 做法是在两端圆之间插一段**反向圆弧**（圆心在轮廓外侧，向内凹），
/// 与两个圆同时外切。这有解析解，不需要 SDF 着色器：
///
///   设两圆 C1(r1)、C2(r2) 垂直居中对齐、圆心距 d，桥弧半径 R。
///   桥弧圆心 B 到两圆心的距离分别是 r1+R 与 r2+R，于是
///       u = ((r1+R)² − (r2+R)² + d²) / (2d)        （B 在 C1→C2 方向上的投影）
///       by = cy − √((r1+R)² − u²)                   （上半桥的圆心，取上方解）
///   切点就在 B 与各自圆心的连线上。根号内为负 = 两圆离太远，桥接不上。
///
/// 之所以不上 SDF：为两个胶囊搬 600–900 行 D3D interop 是本末倒置，
/// 而这段解析式和现有的 G3 圆角是同一套 StreamGeometry 路子。
/// </summary>
internal static class FluidBridge
{
    /// <summary>
    /// 桥弧半径相对于较小那个圆半径的倍数。
    /// 越大腰越粗、融合感越强；1.6 是"看得出是一团"与"还能分辨出两个"的平衡点。
    /// </summary>
    private const double BridgeScale = 1.6;

    /// <summary>
    /// 腰的最窄处至少要有较小半径的这个比例，否则视为接不上。
    ///
    /// 这条判据不是审美，是**正确性**：腰宽 = 2(h − R)，h 随间距增大而减小，
    /// 一旦 h &lt; R，上下两条桥弧就会互相穿过，画出一个自相交的无效形状。
    /// 光判"根号内为负"拦不住它 —— 那时 h 还是正数，只是已经小于 R 了。
    /// 留 0.5 的余量，顺便也挡掉那种细得像根头发丝、看着已经断了的腰。
    /// </summary>
    private const double MinWaistRatio = 0.5;

    /// <summary>
    /// 构造两个胶囊的平滑并集轮廓。两者垂直中心必须一致（实际用法就是并排）。
    /// 距离过远、桥接不上时返回 null —— 调用方据此退回"画两个独立形状"。
    /// </summary>
    /// <param name="left">左胶囊外框。</param>
    /// <param name="right">右胶囊外框。</param>
    /// <param name="tension">0–1，桥的饱满度。0 时腰最细（刚要断开），1 时最粗。</param>
    internal static Geometry? Build(Rect left, Rect right, double tension = 1)
    {
        if (left.Width <= 0 || left.Height <= 0 || right.Width <= 0 || right.Height <= 0)
            return null;

        // 胶囊：圆角等于半高，两端是半圆
        var r1 = left.Height / 2;
        var r2 = right.Height / 2;

        // 参与桥接的是左胶囊的右端圆与右胶囊的左端圆
        var cy = left.Top + r1;
        var c1 = new Point(left.Right - r1, cy);
        var c2 = new Point(right.Left + r2, right.Top + r2);

        // 垂直不对齐时本函数的简化推导不成立
        if (Math.Abs(c2.Y - cy) > 0.5) return null;

        var d = c2.X - c1.X;
        if (d <= 0) return null;   // 已经重叠或左右颠倒，交给调用方处理

        var R = Math.Min(r1, r2) * BridgeScale * Math.Clamp(tension, 0.05, 1);

        var s1 = r1 + R;
        var s2 = r2 + R;
        var u = ((s1 * s1) - (s2 * s2) + (d * d)) / (2 * d);
        var hSq = (s1 * s1) - (u * u);

        // 根号内为负：两圆太远，这个半径的桥弧同时切不到两边
        if (hSq <= 0) return null;

        var h = Math.Sqrt(hSq);

        // 腰宽 = 2(h − R)。太细（乃至为负 = 两条桥弧互相穿过）就认定接不上，
        // 交给调用方画成两个独立形状
        var waist = 2 * (h - R);
        if (waist < Math.Min(r1, r2) * MinWaistRatio) return null;

        var bx = c1.X + u;

        // 上桥圆心在轮廓上方，下桥镜像
        var bTop = new Point(bx, cy - h);
        var bBottom = new Point(bx, cy + h);

        // 切点在桥心与各自圆心的连线上
        var p1Top = Along(c1, bTop, r1);
        var p2Top = Along(c2, bTop, r2);
        var p1Bottom = Along(c1, bBottom, r1);
        var p2Bottom = Along(c2, bBottom, r2);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // 从左胶囊左端的正上方起笔，顺时针走一圈
            var startTop = new Point(left.Left + r1, left.Top);
            ctx.BeginFigure(startTop, isFilled: true, isClosed: true);

            // 上边直线 → 左胶囊右端圆的上切点
            ctx.LineTo(new Point(c1.X, left.Top), isStroked: true, isSmoothJoin: true);
            ArcTo(ctx, p1Top, r1, SweepDirection.Clockwise);

            // 桥：反向圆弧，圆心在外侧，所以扫掠方向与主轮廓相反
            ArcTo(ctx, p2Top, R, SweepDirection.Counterclockwise);

            // 右胶囊：上切点 → 上边 → 右端半圆 → 下边 → 下切点
            ArcTo(ctx, new Point(c2.X, right.Top), r2, SweepDirection.Clockwise);
            ctx.LineTo(new Point(right.Right - r2, right.Top), isStroked: true, isSmoothJoin: true);
            ArcTo(ctx, new Point(right.Right - r2, right.Bottom), r2, SweepDirection.Clockwise);
            ctx.LineTo(new Point(c2.X, right.Bottom), isStroked: true, isSmoothJoin: true);
            ArcTo(ctx, p2Bottom, r2, SweepDirection.Clockwise);

            // 下桥
            ArcTo(ctx, p1Bottom, R, SweepDirection.Counterclockwise);

            // 左胶囊下半：下切点 → 下边 → 左端半圆回到起点
            ArcTo(ctx, new Point(c1.X, left.Bottom), r1, SweepDirection.Clockwise);
            ctx.LineTo(new Point(left.Left + r1, left.Bottom), isStroked: true, isSmoothJoin: true);
            ArcTo(ctx, startTop, r1, SweepDirection.Clockwise);
        }

        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// 两个胶囊在这个间距下能否接上桥。
    /// 状态机据此决定走 Split（带桥）还是直接分成两块。
    /// </summary>
    internal static bool CanBridge(Rect left, Rect right, double tension = 1)
        => Build(left, right, tension) is not null;

    /// <summary>
    /// 在给定的两端尺寸下，桥还能撑住的最大间距（像素）。
    /// 布局要把 Split 态的间距控制在这个数以内，否则一到边界就会"啪"地断成两块。
    /// 二分求解 —— 腰宽对间距单调递减，但反解 R 没有解析式。
    /// </summary>
    internal static double MaxGap(double leftHeight, double rightHeight, double tension = 1)
    {
        double lo = 0, hi = (leftHeight + rightHeight) * 2;

        for (var i = 0; i < 24; i++)
        {
            var mid = (lo + hi) / 2;
            var left = new Rect(0, 0, leftHeight * 3, leftHeight);
            var right = new Rect(left.Right + mid, (leftHeight - rightHeight) / 2,
                rightHeight, rightHeight);

            if (Build(left, right, tension) is not null) lo = mid;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>从 <paramref name="from"/> 朝 <paramref name="toward"/> 走 <paramref name="distance"/>。</summary>
    private static Point Along(Point from, Point toward, double distance)
    {
        var dx = toward.X - from.X;
        var dy = toward.Y - from.Y;
        var len = Math.Sqrt((dx * dx) + (dy * dy));
        if (len < 1e-9) return from;

        return new Point(from.X + (dx / len * distance), from.Y + (dy / len * distance));
    }

    /// <summary>
    /// 圆弧段。这里的弧永远小于半圆（isLargeArc = false）——
    /// 桥弧的张角受"同时外切两圆"约束，主轮廓的弧最多也就是个半圆，
    /// 所以不需要判断大弧小弧。
    /// </summary>
    private static void ArcTo(StreamGeometryContext ctx, Point to, double radius, SweepDirection sweep)
        => ctx.ArcTo(to, new Size(radius, radius), 0, false, sweep, isStroked: true, isSmoothJoin: true);
}
