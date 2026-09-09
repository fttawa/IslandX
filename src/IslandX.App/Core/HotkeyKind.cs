namespace IslandX;

/// <summary>两组全局热键的身份。用枚举而不是 bool，因为将来还会有第三组。</summary>
public enum HotkeyKind
{
    /// <summary>展开 / 收起。</summary>
    Expand,

    /// <summary>切换灵动点模式。</summary>
    Dot,
}
