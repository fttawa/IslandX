using IslandX.Contracts;
using IslandX.Interop;
using Microsoft.Win32;

namespace IslandX.Providers;

/// <summary>
/// 电源：插拔充电器与低电量提醒。
///
/// 这是第一个**瞬时**活动 —— 浮现几秒就自动退场，而不是像媒体/时钟那样常驻。
/// 计时交给 <see cref="Core.ActivityScheduler"/>（见 <see cref="IslandActivity.AutoDismissAfter"/>）。
///
/// 只在"状态真的变了"时才发布：电量每掉 1% 都弹一次会烦死人，
/// 所以记住上一次的交流电状态与低电量档位，只在跨越边界时出声。
/// </summary>
public sealed class PowerProvider : IIslandProvider
{
    /// <summary>低电量阈值。20% 是 Windows 自己的第一档提醒线。</summary>
    private const int LowBattery = 20;

    /// <summary>危急阈值，用 Critical 优先级压过一切。</summary>
    private const int CriticalBattery = 10;

    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(3.5);

    private bool _started;
    private bool? _lastOnAc;
    private int _lastWarnLevel;   // 0 = 正常，20 / 10 = 已就该档位提醒过

    public string Id => "power";

    public event Action<IslandActivity?>? ActivityChanged;

    public Task StartAsync()
    {
        // 先记下当前状态但**不发布** —— 否则开机自启时会凭空弹一条电源通知
        if (TryRead(out var status))
        {
            _lastOnAc = status.ACLineStatus == 1;
            _lastWarnLevel = WarnLevelOf(status);
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _started = true;
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (!_started) return;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _started = false;
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        // Resume/Suspend 不关我们的事，只看状态变化
        if (e.Mode != PowerModes.StatusChange) return;
        if (!TryRead(out var status)) return;

        var onAc = status.ACLineStatus == 1;
        var warn = WarnLevelOf(status);

        var plugChanged = _lastOnAc is { } prev && prev != onAc;
        var warnEscalated = warn > _lastWarnLevel;   // 只在恶化时提醒，回升不吭声

        _lastOnAc = onAc;
        _lastWarnLevel = warn;

        if (plugChanged) PublishPlug(status, onAc);
        else if (warnEscalated) PublishWarning(status, warn);
    }

    private void PublishPlug(NativeMethods.SYSTEM_POWER_STATUS status, bool onAc)
    {
        var pct = PercentOf(status);
        var full = onAc && pct >= 100;

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = $"power:{(onAc ? "ac" : "battery")}",
            ProviderId = Id,
            Priority = ActivityPriority.High,
            Glyph = GlyphFor(status),
            Title = full ? "已充满" : onAc ? "正在充电" : "使用电池",
            Subtitle = pct is { } p ? $"{p}%" : null,
            Progress = pct is { } q ? q / 100.0 : null,
            AutoDismissAfter = Dwell,
        });
    }

    private void PublishWarning(NativeMethods.SYSTEM_POWER_STATUS status, int level)
    {
        var pct = PercentOf(status);

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = $"power:low{level}",
            ProviderId = Id,
            Priority = level == CriticalBattery
                ? ActivityPriority.Critical
                : ActivityPriority.High,
            Glyph = GlyphFor(status),
            Title = level == CriticalBattery ? "电量严重不足" : "电量偏低",
            Subtitle = pct is { } p ? $"剩余 {p}%" : null,
            Progress = pct is { } q ? q / 100.0 : null,
            // 低电量比插拔更值得多看两眼
            AutoDismissAfter = TimeSpan.FromSeconds(5),
        });
    }

    /// <summary>
    /// 主动查看时用的那条活动 —— 右键工具栏点「电量」走这里。
    ///
    /// 这个 Provider 平时**只在事件发生时**说话（插拔充电器、电量跌破档位），
    /// 所以"现在还剩多少电"从来没有一条常驻活动可看。这条补上那个缺口。
    /// 台式机（没有电池）返回 null，工具栏据此把按钮置灰。
    /// </summary>
    public IslandActivity? BuildPeek()
    {
        if (!TryRead(out var status)) return null;

        var pct = PercentOf(status);
        if (pct is null) return null;   // 没有电池

        var onAc = status.ACLineStatus == 1;
        var full = onAc && pct >= 99;

        // 剩余时间只有用电池时才有意义，而且系统常给 -1（还没算出来）
        var left = !onAc && status.BatteryLifeTime > 0
            ? TimeSpan.FromSeconds(status.BatteryLifeTime)
            : (TimeSpan?)null;

        var detail = left is { } span
            ? $"约可用 {(span.TotalHours >= 1 ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分" : $"{span.Minutes} 分钟")}"
            : onAc ? (full ? "已接通电源" : "正在充电") : "使用电池";

        return new IslandActivity
        {
            Id = "power:peek",
            ProviderId = Id,
            Priority = ActivityPriority.High,
            Glyph = GlyphFor(status),
            Title = $"电量 {pct}%",
            Subtitle = detail,
            WantsMainSlot = true,

            // 没有 AutoDismissAfter：主动看的东西不该自己跑掉
        };
    }

    private static bool TryRead(out NativeMethods.SYSTEM_POWER_STATUS status)
    {
        try { return NativeMethods.GetSystemPowerStatus(out status); }
        catch { status = default; return false; }
    }

    private static int? PercentOf(NativeMethods.SYSTEM_POWER_STATUS status)
        => status.BatteryLifePercent is 255 ? null : status.BatteryLifePercent;

    /// <summary>返回 0（正常）/ 20 / 10，表示当前落在哪个提醒档。</summary>
    private static int WarnLevelOf(NativeMethods.SYSTEM_POWER_STATUS status)
    {
        if (status.ACLineStatus == 1) return 0;          // 充着电不提醒
        if (PercentOf(status) is not { } pct) return 0;

        if (pct <= CriticalBattery) return CriticalBattery;
        if (pct <= LowBattery) return LowBattery;
        return 0;
    }

    /// <summary>Battery0–Battery10 是连号的 E850–E85A。</summary>
    private const int Battery0 = 0xE850;

    /// <summary>BatteryCharging0–BatteryCharging9 是连号的 E85B–E864。</summary>
    private const int BatteryCharging0 = 0xE85B;

    /// <summary>
    /// 按电量取档。只用上面两段**连号**区间，不碰 BatteryCharging10 ——
    /// 那个码位在 MDL2 与 Fluent 两套字体里对不上，充满时回落到静态满电池即可。
    /// 字形码位属于编译期无声、运行期渲染成豆腐块的那类东西，宁可少用几个。
    /// </summary>
    private static string GlyphFor(NativeMethods.SYSTEM_POWER_STATUS status)
    {
        // 没有电池（台式机）就拿满电池当电源指示
        if ((status.BatteryFlag & 128) != 0) return ((char)(Battery0 + 10)).ToString();

        var pct = PercentOf(status) ?? 100;
        var step = Math.Clamp((int)Math.Round(pct / 10.0), 0, 10);

        if (status.ACLineStatus != 1) return ((char)(Battery0 + step)).ToString();

        return step >= 10
            ? ((char)(Battery0 + 10)).ToString()
            : ((char)(BatteryCharging0 + step)).ToString();
    }
}
