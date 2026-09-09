using System.Globalization;
using System.Text.RegularExpressions;

namespace IslandX.Core;

/// <summary>
/// 一行带时间戳的歌词。<paramref name="Translation"/> 为 null 表示这一句没有译文
/// （中文歌整首都没有；外语歌的纯感叹句也可能单独缺）。
/// </summary>
public readonly record struct LyricLine(TimeSpan Time, string Text, string? Translation = null);

/// <summary>
/// LRC 解析与按播放位置定位当前行。
///
/// 刻意做成纯函数：歌词这套东西最容易出错的地方是"时间戳算错半拍"，
/// 而那在界面上看只是"歌词跟不上"，分不清是解析、定位、还是播放器的进度不准。
/// 拆成纯函数就能单独喂样本断言。
/// </summary>
public static partial class Lyrics
{
    /// <summary>
    /// 时间标签。分钟不限位数（有些长音频超过 99 分），秒两位，
    /// 小数 1–3 位全收：网易云给 3 位（[00:12.345]），多数 .lrc 是 2 位，
    /// 偶尔也有 1 位（[00:15.5]）—— 位数由 <see cref="TryReadTime"/> 换算，
    /// 收窄成 {2,3} 会把一位小数的整行静默丢掉。
    /// </summary>
    [GeneratedRegex(@"\[(\d+):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.CultureInvariant)]
    private static partial Regex TimeTag();

    /// <summary>
    /// 制作信息行。LRC 里它们和歌词混在一起、也带时间戳，
    /// 但"作词 : 某某"显示在岛体上没有任何意义，滤掉。
    /// </summary>
    [GeneratedRegex(
        @"^\s*(作词|作曲|编曲|制作|制作人|出品|监制|录音|混音|母带|吉他|贝斯|鼓|键盘|弦乐|和声|统筹|策划|发行|词|曲"
        + @"|lyricist|composer|arranger|producer|mixing|mastering|recorded|guitar|bass|drums|keyboard)\s*[:：]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreditLine();

    /// <summary>
    /// 解析 LRC 文本。按时间升序返回；无有效时间戳则返回空数组。
    ///
    /// 一行可能挂多个时间戳（副歌复用同一句），要展开成多行 ——
    /// 只取第一个的话副歌第二次出现就没词了。
    ///
    /// <paramref name="translation"/> 是同一首歌的译文 LRC（网易云的 <c>tlyric</c>），
    /// 按时间戳与原文配对。
    ///
    /// **语言判断不在这里做**，也不需要做：数据源只对外语歌提供译文，
    /// 中文歌的 tlyric 直接是空的（实测周杰伦《晴天》长度 0，
    /// 而英/日/韩三首分别有 55/42/62 行实质译文）。所以"非简体中文才翻译"
    /// 这件事是数据源的判断，我们只管有就用。
    /// </summary>
    public static LyricLine[] Parse(string? lrc, string? translation = null)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return [];

        var translations = ParseTranslations(translation);
        var result = new List<LyricLine>();

        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue;

            // 正文在最后一个时间标签之后
            var last = matches[^1];
            var text = line[(last.Index + last.Length)..].Trim();

            if (text.Length == 0) continue;
            if (CreditLine().IsMatch(text)) continue;

            foreach (Match m in matches)
            {
                if (!TryReadTime(m, out var time)) continue;

                result.Add(new LyricLine(time, text, MatchTranslation(translations, time)));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return [.. result];
    }

    /// <summary>
    /// 译文时间戳与原文对齐的容差。
    ///
    /// 实测两边完全一致（都是 <c>[00:07.410]</c>），但不能赌这一点 ——
    /// 译文常由另一个投稿者上传，差几十毫秒就会让整首歌一行都配不上，
    /// 而那表现为"这首歌没有翻译"，与真的没有翻译无法区分。
    /// </summary>
    private static readonly TimeSpan MatchTolerance = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 解析译文 LRC，按时间排好序备查。
    /// 译文里的 <c>-</c> 是占位符（那一句不需要翻译，比如 "Hey Hey Hey"），当作没有。
    /// </summary>
    private static LyricLine[] ParseTranslations(string? translation)
    {
        if (string.IsNullOrWhiteSpace(translation)) return [];

        var result = new List<LyricLine>();

        foreach (var rawLine in translation.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var matches = TimeTag().Matches(line);
            if (matches.Count == 0) continue;

            var last = matches[^1];
            var text = line[(last.Index + last.Length)..].Trim();

            if (text.Length == 0 || text == "-") continue;

            foreach (Match m in matches)
            {
                if (TryReadTime(m, out var time)) result.Add(new LyricLine(time, text));
            }
        }

        result.Sort((a, b) => a.Time.CompareTo(b.Time));
        return [.. result];
    }

    /// <summary>找与 <paramref name="time"/> 最接近的译文，超出容差则没有。</summary>
    private static string? MatchTranslation(LyricLine[] translations, TimeSpan time)
    {
        if (translations.Length == 0) return null;

        // 二分找插入点，再比较左右两个候选 —— 逐个扫在长歌词上是 O(n²)
        int lo = 0, hi = translations.Length - 1, best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (translations[mid].Time <= time) { best = mid; lo = mid + 1; }
            else hi = mid - 1;
        }

        var candidate = translations[best];
        var delta = Abs(candidate.Time - time);

        if (best + 1 < translations.Length)
        {
            var next = translations[best + 1];
            if (Abs(next.Time - time) < delta) { candidate = next; delta = Abs(next.Time - time); }
        }

        return delta <= MatchTolerance ? candidate.Text : null;

        static TimeSpan Abs(TimeSpan value) => value < TimeSpan.Zero ? -value : value;
    }

    private static bool TryReadTime(Match m, out TimeSpan time)
    {
        time = default;

        if (!int.TryParse(m.Groups[1].ValueSpan, out var minutes)) return false;
        if (!int.TryParse(m.Groups[2].ValueSpan, out var seconds)) return false;

        var fraction = 0.0;
        if (m.Groups[3].Success)
        {
            var digits = m.Groups[3].ValueSpan;
            if (int.TryParse(digits, out var raw))
            {
                // 2 位是厘秒、3 位是毫秒，按位数定权重而不是硬编码除以 100
                fraction = raw / Math.Pow(10, digits.Length);
            }
        }

        time = TimeSpan.FromSeconds((minutes * 60) + seconds + fraction);
        return true;
    }

    /// <summary>
    /// 定位 <paramref name="position"/> 时刻该显示的行。
    /// 返回最后一个时间 ≤ position 的行；还没到第一行时返回 null。
    ///
    /// 用二分而不是线性扫：这个函数每帧都可能被调，而长歌词有几百行。
    /// </summary>
    public static LyricLine? LineAt(LyricLine[] lines, TimeSpan position)
    {
        if (lines.Length == 0 || position < lines[0].Time) return null;

        int lo = 0, hi = lines.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lines[mid].Time <= position) lo = mid;
            else hi = mid - 1;
        }

        return lines[lo];
    }

    /// <summary>
    /// 按显示模式把一行歌词摊成岛体要画的两行。
    /// <c>Main</c> 是主行，<c>Sub</c> 为 null 表示单行。
    ///
    /// 单独摘成纯函数，是因为这里唯一真正会出错的地方 ——
    /// 「只显示译文」遇上没有译文的中文歌 —— 在界面上表现为**歌词整个空掉**，
    /// 和"前奏还没唱到"、"歌词没拉到"长得一模一样，事后根本分不出是哪一种。
    /// 摊成函数就能直接喂样本断言（见 WeatherProbe --lyrics）。
    ///
    /// 渲染层因此完全不知道有"翻译"这回事：它只画第一行和可选的第二行。
    /// </summary>
    public static (string? Main, string? Sub) Present(LyricLine? line, LyricTranslationMode mode)
    {
        if (line is not { } hit) return (null, null);

        return mode switch
        {
            LyricTranslationMode.OriginalOnly => (hit.Text, null),
            // 没有译文就回落到原文，而不是留空
            LyricTranslationMode.TranslationOnly => (hit.Translation ?? hit.Text, null),
            // 双行：译文为 null 时自动退化成单行，不必特判
            _ => (hit.Text, hit.Translation),
        };
    }
}
