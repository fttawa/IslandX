namespace IslandX.Core;

/// <summary>一个可开关的 Provider（"显示哪些事件"里的一项）。</summary>
public sealed record ProviderToggle(string Id, string Label, Action<bool> Set);

/// <summary>
/// 设置的统一接线面：**托盘菜单与设置窗口共用同一个对象**。
///
/// 这一点是刻意的。两处各自接一遍的话，加一个开关就得改两处，
/// 而漏改的那一处不会有编译错误 —— 只会表现为"从设置页改了，托盘里没跟着变"。
///
/// 当前值不存快照，一律**实时从 <see cref="Config"/> 读**。
/// 原来托盘是在构造时抓一次快照的，那在只有托盘一个入口时看不出问题；
/// 有了设置页之后，从一处改完另一处的勾就会停在旧值上。
/// 所以托盘的勾选状态改成在菜单弹出时重读（见 TrayIconHost 的 Opening）。
/// </summary>
public sealed class SettingsBridge
{
    /// <summary>唯一的真值来源。所有 setter 都写它，所有读都从它来。</summary>
    public required AppConfig Config { get; init; }

    // ===== 岛体动作 =====

    public required Action ToggleExpanded { get; init; }
    public required Action Exit { get; init; }
    public required Action<bool> SetDotMode { get; init; }

    // ===== 开关。每个 setter 负责"让它立刻生效 + 写回配置" =====

    public required Action<bool> SetPulse { get; init; }
    public required Action<bool> SetLyrics { get; init; }
    public required Action<LyricTranslationMode> SetLyricTranslation { get; init; }
    public required Action<bool> SetWeather { get; init; }

    /// <summary>手填坐标与行政区。传 null 表示"清空，回到自动定位"。</summary>
    public required Action<double?, double?, string?> SetLocation { get; init; }

    /// <summary>Phira 成绩提醒。全程本地，但要读另一个程序的存档。</summary>
    public required Action<bool> SetPhira { get; init; }

    // ===== 热键 =====

    /// <summary>实际生效的标签；null 表示注册全失败，此刻没有可用热键。</summary>
    public required Func<HotkeyKind, string?> HotkeyLabel { get; init; }

    /// <summary>换一组热键。返回生效的标签；返回 null 表示没换成，原来那组仍有效。</summary>
    public required Func<HotkeyKind, string, string?> SetHotkey { get; init; }

    /// <summary>
    /// 热键真被按到时触发。设置页用它做"按一下试试"——
    /// <c>RegisterHotKey</c> 报成功不代表按下去有反应（会被低级键盘钩子截走），
    /// 只有当场按一下才算验过。
    /// </summary>
    public required Action<Action<HotkeyKind>> SubscribeHotkeyFired { get; init; }
    public required Action<Action<HotkeyKind>> UnsubscribeHotkeyFired { get; init; }

    // ===== 系统 =====

    /// <summary>返回是否设置成功 —— 写注册表可能被策略挡住，失败要让 UI 回弹。</summary>
    public required Func<bool, bool> SetAutoStart { get; init; }
    public required Func<bool> AutoStartEnabled { get; init; }

    public required Action OpenConfigFolder { get; init; }

    /// <summary>可逐项开关的 Provider。</summary>
    public IReadOnlyList<ProviderToggle> Providers { get; init; } = [];
}
