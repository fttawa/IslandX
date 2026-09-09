using IslandX.Contracts;

namespace IslandX.Core;

/// <summary>
/// 活动仲裁器：决定此刻哪个活动占据岛体。
/// 逻辑刻意保持为纯粹的"输入 → 输出"，方便后续补单元测试。
/// </summary>
public sealed class ActivityScheduler
{
    private readonly List<IIslandProvider> _providers = [];
    private readonly Dictionary<string, IslandActivity> _current = [];
    private readonly Dictionary<string, long> _updateOrder = [];

    /// <summary>瞬时活动的自动撤下计时器，每个 Provider 至多一个。</summary>
    private readonly Dictionary<string, Timer> _dismissTimers = [];

    private long _sequence;

    private ActivitySet _winner = ActivitySet.Empty;

    /// <summary>被钉住的 Provider Id。非 null 时压过正常仲裁。</summary>
    private string? _pinnedProvider;

    /// <summary>主动查看时现合成的那条活动。有它就用它 —— 它才是"用户问的问题"的答案。</summary>
    private IslandActivity? _pinnedPeek;

    /// <summary>当前占据岛体的活动发生变化。可能在后台线程触发，UI 侧需自行 marshal。</summary>
    public event Action<ActivitySet>? WinnerChanged;

    public void Register(IIslandProvider provider)
    {
        _providers.Add(provider);
        provider.ActivityChanged += activity => OnProviderActivity(provider.Id, activity);
    }

    /// <param name="shouldStart">
    /// 按 Provider Id 决定是否启动；null 表示全部启动。
    /// 用于让被用户关掉的 Provider 压根不要启动，而不是"启动完再停一遍"。
    /// </param>
    public async Task StartAllAsync(Func<string, bool>? shouldStart = null)
    {
        foreach (var provider in _providers)
        {
            if (shouldStart is not null && !shouldStart(provider.Id)) continue;

            try
            {
                await provider.StartAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[scheduler] {provider.Id} 启动失败: {ex.Message}");
            }
        }
    }

    public void StopAll()
    {
        foreach (var provider in _providers)
        {
            try { provider.Stop(); } catch { /* 退出路径不抛异常 */ }
        }

        lock (_current)
        {
            foreach (var timer in _dismissTimers.Values) timer.Dispose();
            _dismissTimers.Clear();
        }
    }

    /// <summary>
    /// 手动撤下某个 Provider 的活动。运行时禁用 Provider 时用 ——
    /// <c>Stop()</c> 只是让它不再发新活动，已经在显示的那条还得单独清掉。
    /// </summary>
    public void Clear(string providerId) => OnProviderActivity(providerId, null);

    private void OnProviderActivity(string providerId, IslandActivity? activity)
    {
        // 只在**明显等了**才记。媒体开着歌词每 110ms 推一次，
        // 每次都记会把日志刷成噪音，而锁竞争恰恰是卡死最经典的诱因 ——
        // 平时 0ms，一旦有人长时间持锁这里就会跳出来
        var wait = System.Diagnostics.Stopwatch.StartNew();

        lock (_current)
        {
            if (wait.ElapsedMilliseconds >= 40)
                Diag.Log($"sched: {providerId} 等 _current 锁 {wait.ElapsedMilliseconds}ms");

            if (activity is null)
            {
                _current.Remove(providerId);
                _updateOrder.Remove(providerId);
            }
            else
            {
                // 顺序号只在**实质变化**时刷新，不是每次推送都刷。
                //
                // 媒体开着歌词时每 110ms 推一次（进度、当前句），每次都刷的话它永远是
                // "最新的那个"，于是任何同优先级的活动一发出就被它挤掉 ——
                // 而那个活动才是刚发生的事。这个 bug 只在"有一路高频推送"时才显形，
                // 加天气提醒之前根本碰不到。
                var changed = !_current.TryGetValue(providerId, out var prev)
                    || !IsSameVisual(prev, activity);

                _current[providerId] = activity;
                if (changed) _updateOrder[providerId] = ++_sequence;
            }

            ScheduleDismiss(providerId, activity?.AutoDismissAfter);

            var next = Arbitrate();

            // 只有"实质变化"才通知 UI，否则进度每 500ms 刷新会导致无谓的视觉抖动
            if (IsSameVisual(_winner.Resident, next.Resident)
                && IsSameVisual(_winner.Transient, next.Transient))
            {
                _winner = next;   // 仍要更新引用，保证进度值是最新的
                return;
            }

            _winner = next;
        }

        WinnerChanged?.Invoke(_winner);
    }

    /// <summary>
    /// 安排（或续期、或取消）某个 Provider 的自动撤下。调用方必须已持有 _current 锁。
    ///
    /// 续期而非另起一个：连续调音量会连着发好几次活动，
    /// 每次都新建定时器的话，第一个到期时就会把还在显示的活动撤掉。
    /// </summary>
    private void ScheduleDismiss(string providerId, TimeSpan? ttl)
    {
        if (ttl is not { } delay)
        {
            if (_dismissTimers.Remove(providerId, out var stale)) stale.Dispose();
            return;
        }

        if (_dismissTimers.TryGetValue(providerId, out var timer))
        {
            timer.Change(delay, Timeout.InfiniteTimeSpan);
            return;
        }

        _dismissTimers[providerId] = new Timer(
            _ => OnProviderActivity(providerId, null),
            null,
            delay,
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 常驻与瞬时各选一个赢家，两者都有则并列（Split）。
    ///
    /// 常驻一路刻意**排除 Low 优先级**（也就是兜底时钟）：时钟和"刚插上充电器"
    /// 并列显示没有意义，该让位就让位。所以只有真正的常驻活动（媒体）才配得上 Split。
    /// </summary>
    private ActivitySet Arbitrate()
    {
        // 钉住优先。瞬时那一路也一并压掉 —— 用户点开的是"给我看这个"，
        // 这时右边再滑出一个音量小圆只会分散注意
        if (_pinnedProvider is { } pinned)
        {
            var shown = _pinnedPeek ?? _current.GetValueOrDefault(pinned);
            if (shown is not null) return new ActivitySet(shown, null);
        }

        IslandActivity? resident = null, transient = null;
        long residentOrder = -1, transientOrder = -1;

        foreach (var (providerId, activity) in _current)
        {
            var order = _updateOrder.GetValueOrDefault(providerId);

            // 瞬时活动通常去右侧小圆，但**可以要求占主位**（WantsMainSlot）——
            // 降水提醒有标题、时长、柱状图三层信息，塞进 32px 的圆里等于没显示。
            // 这类活动照样带 AutoDismissAfter，几秒后自动撤下、主位还给媒体。
            var wantsMain = activity.AutoDismissAfter is null || activity.WantsMainSlot;

            if (wantsMain)
            {
                if (activity.Priority <= ActivityPriority.Low) continue;
                if (Beats(activity, order, resident, residentOrder))
                {
                    resident = activity;
                    residentOrder = order;
                }
            }
            else if (Beats(activity, order, transient, transientOrder))
            {
                transient = activity;
                transientOrder = order;
            }
        }

        // 一个常驻的都没有（只剩时钟）时，让时钟顶上主体位置
        resident ??= FallbackResident();

        return new ActivitySet(resident, transient);
    }

    private static bool Beats(IslandActivity candidate, long order, IslandActivity? best, long bestOrder)
        => best is null
        || candidate.Priority > best.Priority
        || (candidate.Priority == best.Priority && order > bestOrder);

    /// <summary>兜底：优先级最低的那一路（时钟）。仅在没有任何正经常驻活动时启用。</summary>
    private IslandActivity? FallbackResident()
    {
        IslandActivity? best = null;
        long bestOrder = -1;

        foreach (var (providerId, activity) in _current)
        {
            if (activity.AutoDismissAfter is not null) continue;

            var order = _updateOrder.GetValueOrDefault(providerId);
            if (!Beats(activity, order, best, bestOrder)) continue;

            best = activity;
            bestOrder = order;
        }

        return best;
    }

    /// <summary>
    /// 判断两个活动在视觉上是否等价。进度条独立于此单独更新，
    /// 所以进度差异不算变化 —— 否则媒体每 500ms 一次刷新会不停重播内容转场。
    /// </summary>
    private static bool IsSameVisual(IslandActivity? a, IslandActivity? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        return a.Id == b.Id
            && a.Title == b.Title
            && a.Subtitle == b.Subtitle
            && a.Glyph == b.Glyph
            && a.IsPlaying == b.IsPlaying
            // 歌词每几秒换一句，必须算进来 —— 不然"只有歌词变了"会被判成视觉无变化，
            // 更新压根传不到 UI，歌词就永远停在第一句
            && a.Lyric == b.Lyric
            // 第二行（译文）同样要算 —— 漏了的话"只有译文变了"会被判成视觉无变化，
            // 双行模式下译文就永远停在第一句
            && a.LyricSub == b.LyricSub
            && ReferenceEquals(a.Artwork, b.Artwork)
            // 柱状图按引用比。Provider 每次拉取都产生新数组，但它只在"值得提醒"时
            // 才发活动，所以这里不会造成无谓的重播
            && ReferenceEquals(a.Chart, b.Chart);
    }

    /// <summary>
    /// 临时钉住某个 Provider 的活动，压过正常仲裁。<paramref name="providerId"/> 传 null 解除。
    ///
    /// 之所以钉 **Provider Id** 而不是钉一条具体活动：媒体正在播歌词时每 110ms 推一次，
    /// 钉死一条活动的话歌词会当场冻住。按 Id 查的是**实时**那条，钉住期间照常走字。
    ///
    /// <paramref name="peek"/> 是**主动查看时现合成**的那条活动，有它就优先用它。
    ///
    /// 顺序是 peek 优先、实时活动兜底，**不能反过来**：天气那一路的实时活动是
    /// 气象预警（它自己会弹出来），而用户点开天气想知道的是"雨几时到 / 几时停"——
    /// 那份降水预报不主动问就看不到。反过来的话，一挂预警天气视图就只剩预警，
    /// 而雷雨预警期间恰恰是最想看降水时间线的时候。
    ///
    /// 媒体、时钟这类没有合成器的传 null，于是用实时那条（歌词照常走字）。
    /// </summary>
    public void Pin(string? providerId, IslandActivity? peek = null)
    {
        // Pin 是**在 UI 线程上**拿这把锁的，而 Provider 们在后台线程上拿它。
        // UI 线程等一把后台线程持有的锁 —— 这是"右键就卡住"最可能的形状
        var wait = System.Diagnostics.Stopwatch.StartNew();
        Diag.Log($"sched: Pin({providerId ?? "null"}) 要锁");

        lock (_current)
        {
            Diag.Log($"sched: Pin 拿到锁，等了 {wait.ElapsedMilliseconds}ms");

            if (_pinnedProvider == providerId && ReferenceEquals(_pinnedPeek, peek)) return;

            _pinnedProvider = providerId;
            _pinnedPeek = peek;
            _winner = Arbitrate();
        }

        Diag.Log("sched: Pin 放锁，开始通知 UI");
        WinnerChanged?.Invoke(_winner);
        Diag.Log("sched: Pin 通知完毕");
    }

    /// <summary>某个 Provider 此刻的活动，没有则 null。</summary>
    public IslandActivity? CurrentOf(string providerId)
    {
        lock (_current) return _current.GetValueOrDefault(providerId);
    }

    /// <summary>取当前主体活动的最新快照（含实时进度）。</summary>
    public IslandActivity? Peek()
    {
        lock (_current) return _winner.Main;
    }

    /// <summary>取当前的完整活动组合。</summary>
    public ActivitySet PeekSet()
    {
        lock (_current) return _winner;
    }
}
