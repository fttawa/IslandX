namespace IslandX.Core;

/// <summary>
/// 一个媒体会话在"该挑哪个"这件事上的全部可用特征。
///
/// 刻意不引用 WinRT 类型：选取逻辑因此可以直接喂样本断言，
/// 而不必先凑出一个真的双会话环境（那个环境不好造，这个功能也正是因此拖了很久）。
/// </summary>
/// <param name="Index">在 <c>GetSessions()</c> 里的下标，用于把结论映射回真会话。</param>
/// <param name="AppId">SourceAppUserModelId。同一个进程注册的多个会话，这一项相同。</param>
/// <param name="HasTimeline">是否上报播放位置。没有它进度条和歌词都无从谈起。</param>
/// <param name="HasGenre">Genre 字段是否有内容。InfLink 把网易云歌曲 ID 塞在这里。</param>
/// <param name="HasTitle">有没有曲目名。全空的会话等于没有信息。</param>
public readonly record struct MediaSessionInfo(
    int Index,
    string AppId,
    bool HasTimeline,
    bool HasGenre,
    bool HasTitle);

/// <summary>
/// 在多个媒体会话里挑一个接管。
///
/// 为什么需要挑：**同一个播放器可能同时挂着两个会话**。网易云原生的 SMTC
/// 不给 timeline 也不给歌曲 ID，而 InfLink 插件注册的那个两样都有；
/// InfLink 靠正则匹配去禁用原生上报，网易云一更新就可能失配，于是两个并存。
/// 这时 <c>GetCurrentSession()</c> 只返回其中一个，**挑哪个取决于谁最近更新** ——
/// 实测表现为播放时读到没 timeline 的、暂停时才读到有的，进度条和歌词随之忽有忽无。
/// </summary>
public static class MediaSessionPick
{
    /// <summary>
    /// 会话的信息量评分。位权重刻意拉开，形成严格的字典序：
    /// 有 timeline 的一定压过没有的，同为有/无时再比 Genre，最后比标题。
    /// </summary>
    public static int Score(MediaSessionInfo s)
        => (s.HasTimeline ? 4 : 0) + (s.HasGenre ? 2 : 0) + (s.HasTitle ? 1 : 0);

    /// <summary>
    /// 挑一个会话，返回它在 <paramref name="sessions"/> 里的下标；没有可挑的返回 -1。
    ///
    /// **只在同一个 AppId 内部改选**，绝不跨应用。这条限制是安全底线：
    /// "哪个应用是当前的"是用户的意图，由系统的 <c>GetCurrentSession()</c> 判断，
    /// 我们没有比它更好的依据 —— 浏览器在放视频、音乐软件暂停着，该显示哪个不该我们猜。
    /// 要修的只是同一个应用内部的重复会话，而那些会话来自同一个进程、AppId 天然相同。
    /// </summary>
    /// <param name="currentIndex">
    /// <c>GetCurrentSession()</c> 对应的下标；它不在列表里（或为 null）时传 -1，
    /// 那时退化为在全部会话里挑分最高的。
    /// </param>
    public static int Choose(IReadOnlyList<MediaSessionInfo> sessions, int currentIndex)
    {
        if (sessions.Count == 0) return -1;

        // 系统没给当前会话：只能自己挑，那就挑信息量最大的
        if (currentIndex < 0 || currentIndex >= sessions.Count) return BestOf(sessions, null, -1);

        // 同源的候选里挑最好的。平分时留在 current 上 ——
        // 分数一样说明两个会话一样好，换过去只会白白重订阅一次
        var current = sessions[currentIndex];
        return BestOf(sessions, current.AppId, currentIndex);
    }

    private static int BestOf(IReadOnlyList<MediaSessionInfo> sessions, string? appId, int preferIndex)
    {
        var best = preferIndex >= 0 ? preferIndex : -1;
        var bestScore = best >= 0 ? Score(sessions[best]) : int.MinValue;

        for (var i = 0; i < sessions.Count; i++)
        {
            if (i == best) continue;
            if (appId is not null && !string.Equals(sessions[i].AppId, appId, StringComparison.OrdinalIgnoreCase))
                continue;

            var score = Score(sessions[i]);

            // 严格大于才换。相等时保持原选择，避免在两个等价会话之间来回跳
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }

        return best;
    }
}
