using IslandX.Interop;

namespace IslandX.Core;

/// <summary>
/// 热键文本 ↔ <c>RegisterHotKey</c> 参数的互转。
///
/// 之所以需要它：全局热键被抢的情况在不同机器上差别极大（显卡驱动、输入法、录屏工具都会抢），
/// 内置候选列表兜不住 —— 本机 Ctrl+Alt+D 与备选 Ctrl+Alt+J 都被低级键盘钩子截了，
/// <c>RegisterHotKey</c> 报成功但按下去没反应。最终只能让用户自己指定。
/// </summary>
public static class Hotkey
{
    /// <summary>
    /// 解析形如 "Ctrl+Alt+I"、"Ctrl+Shift+F9"、"Win+Alt+Space" 的文本。
    /// 分隔符两侧的空格与大小写都不敏感；无修饰键的组合一律拒绝 ——
    /// 单个字母做全局热键会把这个键在整个系统里吞掉。
    /// </summary>
    public static bool TryParse(string? text, out uint modifiers, out uint vk, out string label)
    {
        modifiers = 0;
        vk = 0;
        label = "";

        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return false;

        var names = new List<string>();
        uint mods = 0;
        uint key = 0;

        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control":
                    mods |= NativeMethods.MOD_CONTROL;
                    names.Add("Ctrl");
                    continue;

                case "alt":
                    mods |= NativeMethods.MOD_ALT;
                    names.Add("Alt");
                    continue;

                case "shift":
                    mods |= NativeMethods.MOD_SHIFT;
                    names.Add("Shift");
                    continue;

                case "win" or "windows" or "meta":
                    mods |= NativeMethods.MOD_WIN;
                    names.Add("Win");
                    continue;
            }

            // 非修饰键部分只允许出现一个
            if (key != 0) return false;
            if (!TryParseKey(part, out key, out var keyName)) return false;
            names.Add(keyName);
        }

        if (mods == 0 || key == 0) return false;

        modifiers = mods;
        vk = key;
        label = string.Join("+", names);
        return true;
    }

    private static bool TryParseKey(string name, out uint vk, out string label)
    {
        vk = 0;
        label = "";

        // 单个字母 / 数字
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = c;              // VK_A..VK_Z 与 VK_0..VK_9 恰好等于其 ASCII 码
                label = c.ToString();
                return true;
            }

            return false;
        }

        // F1–F24
        if ((name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var fn) && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);   // VK_F1 = 0x70
            label = "F" + fn;
            return true;
        }

        (vk, label) = name.ToLowerInvariant() switch
        {
            "space" => (0x20u, "Space"),
            "enter" or "return" => (0x0Du, "Enter"),
            "tab" => (0x09u, "Tab"),
            "esc" or "escape" => (0x1Bu, "Esc"),
            "backspace" => (0x08u, "Backspace"),
            "insert" or "ins" => (0x2Du, "Insert"),
            "delete" or "del" => (0x2Eu, "Delete"),
            "home" => (0x24u, "Home"),
            "end" => (0x23u, "End"),
            "pageup" or "pgup" => (0x21u, "PageUp"),
            "pagedown" or "pgdn" => (0x22u, "PageDown"),
            "up" => (0x26u, "Up"),
            "down" => (0x28u, "Down"),
            "left" => (0x25u, "Left"),
            "right" => (0x27u, "Right"),
            _ => (0u, ""),
        };

        return vk != 0;
    }
}
