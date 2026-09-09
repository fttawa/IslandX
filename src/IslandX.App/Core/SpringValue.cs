namespace IslandX.Core;

/// <summary>
/// 单个属性的弹簧求解器。整个灵动岛"活的"手感全部来自这里 —— 用弹簧而非贝塞尔曲线，
/// 目标值中途改变时不会出现速度跳变，形变可以随时被打断并平滑接续。
///
/// 唯一一处**故意偏离物理**的地方是反向打断：那时旧速度会被衰减到三成
/// （见 <see cref="SetTarget"/>）。纯物理下弹簧得先减速、停住、再掉头，
/// 实测在形变最活跃时打断要倒着走 35% 的行程、43ms 才转向 —— 那是用户眼里的
/// "点了没反应"。交互动画里响应优先于动量的真实感。
/// </summary>
public sealed class SpringValue
{
    private const double RestDelta = 0.01;    // 位置收敛阈值（× RestScale）
    private const double RestSpeed = 0.05;    // 速度收敛阈值（× RestScale）
    private const double MaxSubStep = 1.0 / 240.0;  // 固定子步长，保证大 dt 下积分稳定

    /// <summary>
    /// 反向打断时保留的速度比例，见 <see cref="SetTarget"/>。
    /// 1.0 即纯物理（旧行为），可用 ISLANDX_REVERSAL 覆盖做 A/B。
    /// </summary>
#if DEBUG
    private static readonly double ReversalRetain =
        double.TryParse(Environment.GetEnvironmentVariable("ISLANDX_REVERSAL"), out var rr) && rr >= 0 ? rr : 0.3;
#else
    private const double ReversalRetain = 0.3;
#endif

    private double _current;
    private double _target;
    private double _velocity;

    /// <summary>本次形变的起点、行程倒数与已耗时，只为统计超调与到位时刻用。</summary>
    private double _start;
    private double _invTravel;
    private double _elapsed;

    public SpringValue(double initial, double stiffness = 220, double damping = 26, double mass = 1)
    {
        _current = initial;
        _target = initial;
        Stiffness = stiffness;
        Damping = damping;
        Mass = mass;
    }

    public double Stiffness { get; set; }
    public double Damping { get; set; }
    public double Mass { get; set; }

    /// <summary>
    /// 静止判定的尺度：位置阈值 = 0.01 × RestScale，速度阈值 = 0.05 × RestScale。
    ///
    /// 存在的理由是不同弹簧的量纲差着两个数量级 —— 宽度是几百像素，透明度是 0–1。
    /// 用同一个绝对阈值，对透明度刚好，对尺寸则严苛得离谱：0.01 像素的收敛精度
    /// 远超肉眼可辨，而指数衰减的尾巴很长，为这点看不见的余量要多跑约 250ms 的
    /// 满帧渲染。尺寸弹簧把它放大到 0.25 像素级，视觉上没有任何区别。
    /// </summary>
    public double RestScale { get; set; } = 1;

    public double Current => _current;
    public double Target => _target;

    /// <summary>当前速度（单位/秒）。渲染层据此判断回弹尾巴是否已慢到可以降帧。</summary>
    public double Velocity => _velocity;

    /// <summary>是否已静止。所有弹簧静止时可以停掉渲染循环省电。</summary>
    public bool IsAtRest { get; private set; } = true;

    /// <summary>
    /// 本次形变至今的最大超调，已按总行程归一化（0.08 = 冲过目标 8%）。
    ///
    /// 过冲是刻意调出来的手感，而"到底有没有真的冲过去、冲了多少"肉眼极难判断——
    /// 阻尼比改动一点点，观感差别很微妙，全靠感觉调容易自我说服。
    /// 这个量可以直接断言，调参时对着它看比对着屏幕看靠谱。
    /// </summary>
    public double PeakOvershoot { get; private set; }

    /// <summary>
    /// 打断后朝**原**方向逆行的最大距离，按新行程归一化（0.2 = 倒着走了新行程的 20%）。
    ///
    /// 反向打断（展开到一半改收起）时弹簧带着旧速度，要先减速、停住、再掉头，
    /// 这期间形状仍朝旧方向走 —— 用户看到的就是"点了没反应，它还在往外张"。
    /// 逆行量和逆行时长才是打断手感的真实指标，光看"总时长"看不出来。
    /// </summary>
    public double PeakBacktrack { get; private set; }

    /// <summary>逆行持续的毫秒数：从打断到速度掉头（开始朝新目标走）为止。</summary>
    public double BacktrackMs { get; private set; }

    /// <summary>
    /// 本次形变首次走完 95% 行程所用的毫秒数。
    ///
    /// 各维度**刻意错峰**（宽先高后或反之）是流体感的来源，但"谁先到"在
    /// 300ms 的形变里靠眼睛分辨不出来。外部高频轮询窗口标题又会走 WM_GETTEXT
    /// 阻塞 UI 线程、反过来干扰被测的形变，所以由弹簧自己记。
    /// </summary>
    public double SettleMs { get; private set; }

    public void SetTarget(double target)
    {
        if (Math.Abs(target - _target) < double.Epsilon) return;

        var travel = target - _current;
        _start = _current;
        _invTravel = Math.Abs(travel) > 1e-9 ? 1 / travel : 0;
        _elapsed = 0;
        PeakOvershoot = 0;
        PeakBacktrack = 0;
        BacktrackMs = 0;
        SettleMs = 0;

        // 反向打断：旧速度指着新目标的反方向，弹簧得先减速、停住、再掉头，
        // 这段"逆行"就是用户眼里的"点了没反应"。按物理该保留全部速度，
        // 但那是把动量的真实感摆在响应之前 —— 交互动画里反了。
        // 衰减到三成：掉头快得多，又不是硬清零（清零会看出速度断层）。
        // 同向打断不动它，那时动量正好帮忙。
        if (_velocity * travel < 0) _velocity *= ReversalRetain;

        _target = target;
        IsAtRest = false;
    }

    /// <summary>无动画直接跳到目标值，用于初始化与窗口重定位。</summary>
    public void SnapTo(double value)
    {
        _current = value;
        _target = value;
        _velocity = 0;
        _start = value;
        _invTravel = 0;
        _elapsed = 0;
        PeakOvershoot = 0;
        SettleMs = 0;
        IsAtRest = true;
    }

    /// <summary>配置弹簧参数，用于不同转场使用不同手感。</summary>
    public SpringValue Configure(double stiffness, double damping, double mass = 1)
    {
        Stiffness = stiffness;
        Damping = damping;
        Mass = mass;
        return this;
    }

    /// <summary>推进 dt 秒（半隐式欧拉 + 子步长切分）。</summary>
    public void Update(double dt)
    {
        if (IsAtRest) return;

        // dt 过大时（窗口被拖动、断点命中后恢复）夹住，避免弹簧炸开
        if (dt > 0.1) dt = 0.1;

        var remaining = dt;
        while (remaining > 0)
        {
            var step = Math.Min(remaining, MaxSubStep);
            var displacement = _current - _target;
            var force = (-Stiffness * displacement) - (Damping * _velocity);
            _velocity += force / Mass * step;
            _current += _velocity * step;
            remaining -= step;
            _elapsed += step;

            // 峰值出现在速度过零那一刻，可能落在子步之间 —— 所以逐子步取，
            // 放到循环外面会漏掉真正的峰
            var over = (_current - _target) * _invTravel;
            if (over > PeakOvershoot) PeakOvershoot = over;

            // 逆行：朝原方向倒退的距离，以及掉头所花的时间
            var back = (_start - _current) * _invTravel;
            if (back > PeakBacktrack)
            {
                PeakBacktrack = back;
                BacktrackMs = _elapsed * 1000;
            }

            if (SettleMs <= 0 && (_current - _start) * _invTravel >= 0.95)
                SettleMs = _elapsed * 1000;
        }

        if (Math.Abs(_current - _target) < RestDelta * RestScale
            && Math.Abs(_velocity) < RestSpeed * RestScale)
        {
            _current = _target;
            _velocity = 0;
            IsAtRest = true;
        }
    }
}
