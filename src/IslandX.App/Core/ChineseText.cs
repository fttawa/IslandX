using System.Runtime.InteropServices;

namespace IslandX.Core;

/// <summary>
/// 繁体 → 简体转换。为「Spotify 的歌词搜不到」加的。
///
/// Spotify 按**原始曲库**给元数据，华语歌常常是繁体：
/// <c>我還年輕 我還年輕 / 老王樂隊</c>、<c>山海 / 草東沒有派對</c>。
/// 而网易云索引的是简体（<c>我还年轻 我还年轻 / 老王乐队</c>）——
/// 拿繁体串去搜，**一条结果都没有**（实测）。
///
/// 用 Windows 自带的 <c>LCMapStringEx</c>，不引第三方依赖 ——
/// 这是个 Win32 应用，而 OpenCC 那类库为这一个用途太重了。
/// 它是**逐字映射**（不做词组级转换），对曲名人名这种用途足够。
///
/// ⚠ 不要拿它去无条件"规范化"所有文本：日文汉字也会被映射
/// （<c>綺麗</c> → <c>绮丽</c>），那会把本来能搜到的日文歌搜坏。
/// 所以调用方的策略是**先按原样搜，搜不到再用转换后的重试**，
/// 而不是一上来就转 —— 见 <see cref="LyricsService"/>。
/// </summary>
public static partial class ChineseText
{
    /// <summary>把繁体字映射成简体字。</summary>
    private const uint LCMAP_SIMPLIFIED_CHINESE = 0x02000000;

    /// <summary>用系统默认区域。转换表本身与区域无关，这里只是要一个合法的名字。</summary>
    private const string LocaleSystemDefault = "!x-sys-default-locale";

    [LibraryImport("kernel32.dll", EntryPoint = "LCMapStringEx", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int LCMapStringEx(
        string lpLocaleName,
        uint dwMapFlags,
        string lpSrcStr,
        int cchSrc,
        [Out] char[]? lpDestStr,
        int cchDest,
        IntPtr lpVersionInformation,
        IntPtr lpReserved,
        IntPtr sortHandle);

    /// <summary>
    /// 串里有没有汉字（含日文汉字）。没有就不必转，直接省掉一次系统调用 ——
    /// 英文歌占多数，而它们走这条路径完全是浪费。
    /// </summary>
    public static bool HasHan(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        foreach (var c in text)
        {
            // CJK 统一表意文字基本区 + 扩展 A + 兼容表意文字。
            // 够覆盖曲名人名，不为罕用扩展区再多写几段
            if (c is >= '一' and <= '鿿') return true;
            if (c is >= '㐀' and <= '䶿') return true;
            if (c is >= '豈' and <= '﫿') return true;
        }

        return false;
    }

    /// <summary>
    /// 转成简体。没有汉字、或者系统调用失败时**原样返回** ——
    /// 这是个尽力而为的辅助手段，失败了应该退回原串继续走，不该抛。
    /// </summary>
    public static string ToSimplified(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!HasHan(text)) return text;

        try
        {
            // 先问需要多大。传 cchDest=0 时返回所需长度，这是 LCMapStringEx 的约定
            var need = LCMapStringEx(
                LocaleSystemDefault, LCMAP_SIMPLIFIED_CHINESE,
                text, text.Length, null, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            if (need <= 0) return text;

            var buffer = new char[need];

            var written = LCMapStringEx(
                LocaleSystemDefault, LCMAP_SIMPLIFIED_CHINESE,
                text, text.Length, buffer, need, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            return written <= 0 ? text : new string(buffer, 0, written);
        }
        catch
        {
            return text;
        }
    }

    /// <summary>
    /// 转换后是不是真的变了。用来决定"值不值得再发一次请求" ——
    /// 简体原文转出来还是自己，再搜一遍纯属浪费。
    /// </summary>
    public static bool DiffersWhenSimplified(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;

        return !string.Equals(text, ToSimplified(text), StringComparison.Ordinal);
    }
}
