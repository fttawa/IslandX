using System.Net.Http;
using System.Text.Json;

namespace IslandX.Core;

/// <summary>
/// 歌词获取。GSMTC 只给标题/艺人，歌词得另找来源 —— 这里走网易云的公开接口。
///
/// 两条匹配路径：
/// 1. **精确**：SMTC 的 Genre 字段里带 <c>NCM-{id}</c> 时直接按 ID 取词。
///    这是 InfLink 插件塞进去的（见 README），装了它才有。
/// 2. **搜索**：拿标题 + 艺人去搜，取第一个标题对得上的结果。
///    同名翻唱、Live 版、remix 都可能配错，所以要做标题校验，宁可不给也别给错的。
///
/// ⚠ 这个类会**联网**，把当前播放的曲目名发到 music.163.com。
/// 所以歌词功能默认关闭，由用户显式开启（见 AppConfig.LyricsEnabled）。
/// </summary>
public sealed class LyricsService : IDisposable
{
    private const string SearchApi = "https://music.163.com/api/search/get";
    private const string LyricApi = "https://music.163.com/api/song/lyric";

    /// <summary>InfLink 塞在 Genre 里的前缀。</summary>
    private const string NcmPrefix = "NCM-";

    private readonly HttpClient _http;

    /// <summary>已解析的歌词，按曲目键缓存。null 值也缓存 —— 表示"查过了，没有"，别反复请求。</summary>
    private readonly Dictionary<string, LyricLine[]?> _cache = [];

    /// <summary>正在飞的请求，避免同一首歌被并发拉多次。</summary>
    private readonly HashSet<string> _inFlight = [];

    private readonly object _gate = new();

    public LyricsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        // 不带 Referer 时接口会返回 -460「网络太拥挤」
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) IslandX");
        _http.DefaultRequestHeaders.Add("Referer", "https://music.163.com");
    }

    /// <summary>最近一次失败原因，供探针诊断。</summary>
    public string LastError { get; private set; } = "-";

    /// <summary>缓存命中数 / 网络请求数 / 匹配失败数。</summary>
    public int CacheHits { get; private set; }
    public int Fetches { get; private set; }
    public int Misses { get; private set; }

    /// <summary>
    /// 取歌词。**不阻塞**：缓存里有就同步返回，没有就返回 false 并在后台去拉，
    /// 拉到之后通过 <paramref name="onReady"/> 回调。
    ///
    /// 之所以不做成 async —— 调用方是每 500ms 跑一次的发布循环，
    /// 让它 await 网络请求会把整条发布链路的节奏交给网络延迟。
    /// </summary>
    public bool TryGet(string title, string artist, string? genreTag,
        out LyricLine[]? lines, Action? onReady = null)
    {
        var key = KeyOf(title, artist, genreTag);
        lines = null;

        if (string.IsNullOrWhiteSpace(title))
        {
            return true;   // 没有曲目信息，不算"还没拿到"
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out lines))
            {
                CacheHits++;
                return true;
            }

            if (!_inFlight.Add(key)) return false;   // 已经在拉了
        }

        _ = FetchAsync(key, title, artist, genreTag, onReady);
        return false;
    }

    private async Task FetchAsync(string key, string title, string artist, string? genreTag, Action? onReady)
    {
        LyricLine[]? parsed = null;

        try
        {
            Fetches++;

            var id = ExtractNcmId(genreTag) ?? await SearchIdAsync(title, artist);
            if (id is not null)
            {
                var (lrc, translation) = await FetchLyricAsync(id);
                parsed = Lyrics.Parse(lrc, translation);
                if (parsed.Length == 0) parsed = null;
            }

            if (parsed is null) Misses++;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name;
            Misses++;
        }
        finally
        {
            lock (_gate)
            {
                // 连 null 一起缓存：查过没有的歌不该每 500ms 再问一遍
                _cache[key] = parsed;
                _inFlight.Remove(key);

                // 缓存无上限会随播放列表一直长，够用就好
                if (_cache.Count > 200) _cache.Clear();
            }

            onReady?.Invoke();
        }
    }

    /// <summary>从 Genre 里抠出网易云歌曲 ID。InfLink 的格式是 <c>NCM-123456</c>。</summary>
    private static string? ExtractNcmId(string? genreTag)
    {
        if (string.IsNullOrWhiteSpace(genreTag)) return null;

        var idx = genreTag.IndexOf(NcmPrefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var span = genreTag.AsSpan(idx + NcmPrefix.Length);
        var end = 0;
        while (end < span.Length && char.IsAsciiDigit(span[end])) end++;

        return end == 0 ? null : span[..end].ToString();
    }

    /// <summary>
    /// 搜歌曲 ID。**按变体阶梯逐个试**，命中即止 —— 见 <see cref="QueryVariants"/>。
    /// </summary>
    private async Task<string?> SearchIdAsync(string title, string artist)
    {
        foreach (var (t, a) in QueryVariants(title, artist))
        {
            var id = await SearchOnceAsync(t, a);
            if (id is not null) return id;
        }

        return null;
    }

    /// <summary>
    /// 搜索用的查询变体，按"最接近原样"到"最宽松"排列。命中一个就不再往下试。
    ///
    /// **为什么需要变体**：Spotify 按原始曲库给元数据，华语歌常常是繁体
    /// （实测 <c>我還年輕 我還年輕 / 老王樂隊</c>），而网易云索引的是简体 ——
    /// 拿繁体串去搜一条结果都没有。
    ///
    /// **为什么不一上来就转**：<c>LCMapStringEx</c> 是逐字映射，日文汉字也会被改
    /// （<c>月が綺麗ね…</c> → <c>月が绮丽ね…</c>，实测），那会把本来能搜到的日文歌搜坏。
    /// 所以顺序是"原样优先、转换兜底"，而不是"统一规范化"。
    ///
    /// 阶梯只放**有实测依据**的两级。曾想过再加"去掉艺术家"和"剥掉
    /// <c>- Remastered 2011</c> 这类后缀"，但实测那些形状本来就搜得到
    /// （Bohemian Rhapsody / Shape of You / Viva La Vida 都命中），
    /// 没有证据的一级只会白白多发一次请求。
    /// </summary>
    internal static IEnumerable<(string Title, string Artist)> QueryVariants(string title, string artist)
    {
        yield return (title, artist);

        var st = ChineseText.ToSimplified(title);
        var sa = ChineseText.ToSimplified(artist);

        // 转出来一模一样就别再发一次
        if (!string.Equals(st, title, StringComparison.Ordinal)
            || !string.Equals(sa, artist, StringComparison.Ordinal))
        {
            yield return (st, sa);
        }
    }

    private async Task<string?> SearchOnceAsync(string title, string artist)
    {
        var keyword = Uri.EscapeDataString($"{title} {artist}".Trim());
        var json = await _http.GetStringAsync($"{SearchApi}?s={keyword}&type=1&limit=5");

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("result", out var result)) return null;
        if (!result.TryGetProperty("songs", out var songs) || songs.ValueKind != JsonValueKind.Array) return null;

        foreach (var song in songs.EnumerateArray())
        {
            var name = song.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (!TitleMatches(title, name)) continue;

            if (song.TryGetProperty("id", out var idEl))
            {
                return idEl.ValueKind == JsonValueKind.Number
                    ? idEl.GetInt64().ToString()
                    : idEl.GetString();
            }
        }

        return null;
    }

    /// <summary>
    /// 拉一首歌的原文与译文。
    ///
    /// <c>tv=-1</c> 那个参数一直都在，只是以前没解析 <c>tlyric</c> —— 译文本来就在同一个响应里，
    /// 不需要任何翻译服务，也不多一次请求。
    /// </summary>
    private async Task<(string? Lyric, string? Translation)> FetchLyricAsync(string id)
    {
        var json = await _http.GetStringAsync($"{LyricApi}?id={id}&lv=1&kv=1&tv=-1");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return (Read(root, "lrc"), Read(root, "tlyric"));

        static string? Read(JsonElement root, string field)
            => root.TryGetProperty(field, out var block)
            && block.TryGetProperty("lyric", out var text)
                ? text.GetString()
                : null;
    }

    /// <summary>
    /// 标题是否算对得上。搜索结果第一条经常是同名翻唱或 remix，
    /// 直接采信会给出完全不相干的词 —— 那比没有歌词更糟。
    /// 归一化后要求一方包含另一方：容忍 "(Explicit)"、"- Live" 这类后缀，
    /// 但拒绝毫不相干的曲名。
    /// </summary>
    private static bool TitleMatches(string wanted, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;

        var a = Normalize(wanted);
        var b = Normalize(candidate);
        if (a.Length == 0 || b.Length == 0) return false;

        return a.Contains(b, StringComparison.Ordinal)
            || b.Contains(a, StringComparison.Ordinal);
    }

    /// <summary>
    /// 比对用的规范形式：去掉标点空白、转小写、**并统一到简体**。
    ///
    /// 最后那一步是必须的。变体阶梯会用简体去搜，网易云返回的自然也是简体曲名，
    /// 而传进来的 <paramref name="value"/> 是 Spotify 的繁体原文 ——
    /// 不统一的话搜到了也会被判成"不是这首"，等于阶梯白加。
    ///
    /// 两边都做同样的映射，所以日文汉字被一起改掉不影响比对的一致性。
    /// </summary>
    private static string Normalize(string value)
    {
        var buffer = new char[value.Length];
        var len = 0;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c)) buffer[len++] = char.ToLowerInvariant(c);
        }

        return ChineseText.ToSimplified(new string(buffer, 0, len));
    }

    private static string KeyOf(string title, string artist, string? genreTag)
        => ExtractNcmId(genreTag) is { } id ? $"ncm:{id}" : $"q:{title}{artist}";

    public void Dispose() => _http.Dispose();
}
