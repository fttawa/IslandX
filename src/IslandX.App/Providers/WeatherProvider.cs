using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using IslandX.Contracts;
using IslandX.Core;

namespace IslandX.Providers;

/// <summary>
/// 降水提醒与灾害预警。
///
/// 两条数据链路，都**不需要 API key**，开箱即用：
///
/// - **降水**：Open-Meteo，15 分钟粒度的分钟级预报。
/// - **预警**：中国气象局（nmc.cn）。它一次给全国 1300+ 条，
///   要靠行政区名筛成本地的 —— 而系统定位只给坐标，所以中间还要一次反向地理编码。
///
/// ⚠ 会把坐标发到第三方，所以整个 Provider 默认关闭（<c>AppConfig.WeatherEnabled</c>）。
/// 坐标在发出前降到 2 位小数（见 <see cref="GeoLocation.Coordinate.Rounded"/>）。
/// </summary>
public sealed class WeatherProvider : IIslandProvider
{
    private const string ProviderId = "weather";

    /// <summary>预报刷新间隔。分钟级预报本身也就 15 分钟一格，再勤没有意义。</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMinutes(10);

    /// <summary>位置重解析间隔。人会移动，但不必每次刷天气都去问一遍系统定位。</summary>
    private static readonly TimeSpan RelocateEvery = TimeSpan.FromHours(6);

    /// <summary>预报窗口：4 小时（16 格）。再长的话柱子太细，也超出"马上要下雨"的关切范围。</summary>
    private const int ForecastSlots = 16;

    /// <summary>降水提醒在岛上停留多久。比音量、剪贴板长 —— 这条信息是要读的，不是扫一眼。</summary>
    private static readonly TimeSpan RainDwell = TimeSpan.FromSeconds(12);

    /// <summary>灾害预警停留更久 —— 这条信息的后果比"要下雨"重得多。</summary>
    private static readonly TimeSpan AlertDwell = TimeSpan.FromSeconds(25);

    /// <summary>红色 / 橙色预警再久一些。级别的差异体现在这里，而不在优先级上。</summary>
    private static readonly TimeSpan SevereAlertDwell = TimeSpan.FromSeconds(45);

    /// <summary>
    /// 中国气象局的预警接口。一次给全国 1300+ 条，pageSize 给大一点一次拉完 ——
    /// 分页要 35 次请求，反而更慢更吵。
    /// </summary>
    private const string NmcBaseUrl = "https://www.nmc.cn";
    private const string NmcAlarmUrl = NmcBaseUrl + "/rest/findAlarm?pageNo=1&pageSize=2000";

    private readonly System.Threading.Timer _timer;
    private HttpClient? _http;
    private bool _running;

    private GeoLocation.Coordinate? _where;
    private DateTime _locatedAt = DateTime.MinValue;

    /// <summary>
    /// 已经播报过的那场雨的开始时刻。用来去重 ——
    /// 预报每 10 分钟刷一次，而一场雨会在好几次刷新里持续存在，
    /// 不去重的话同一场雨每 10 分钟弹一次，比不提醒更烦人。
    /// </summary>
    private DateTime? _announcedRain;

    /// <summary>
    /// 最近一次拉到的降水解读。**不下雨时也留着** —— 报不报警是一回事，
    /// 用户主动点开天气想知道的恰恰是"雨几时到 / 几时停"，
    /// 而"未来四小时都不下"同样是个答案。原先这条路径直接 return，什么都不留。
    /// </summary>
    private RainOutlook? _outlook;

    /// <summary>上面那份解读是什么时候拉的。太旧就不该当成"现在的天气"。</summary>
    private DateTime _outlookAt = DateTime.MinValue;

    /// <summary>已播报过的预警 id。同一条预警会一直挂在接口上直到解除。</summary>
    private readonly HashSet<string> _announcedAlerts = [];

    /// <summary>缓存的行政区与解析时刻。</summary>
    private Region? _region;
    private DateTime _regionAt = DateTime.MinValue;

    /// <summary>
    /// 手填的行政区，形如「辽宁省沈阳市」。填了就跳过反向地理编码。
    /// 用于两种情况：不愿意让坐标经过第三方地理服务，或者反查结果不准。
    /// </summary>
    public string? ManualRegion { get; set; }

    public WeatherProvider()
        => _timer = new System.Threading.Timer(_ => _ = TickAsync(), null, Timeout.Infinite, Timeout.Infinite);

    public string Id => ProviderId;

    public event Action<IslandActivity?>? ActivityChanged;

    /// <summary>
    /// 链路状态：位置 / 预报 / 预警 各自到哪一步了。
    ///
    /// 不加 <c>#if DEBUG</c>，理由和歌词的 <c>DebugLyricState</c> 一样：
    /// "没提醒我下雨"有六种原因（功能没开、没定到位、请求失败、判据没触发、
    /// 去重吃掉了、真的不下雨），在界面上全都表现为**什么都没发生**，
    /// 肉眼零区分度，线上也需要能问出来。
    /// </summary>
    internal static string DebugState = "off";

    /// <summary>
    /// 预警链路单列一个字段。两条链路共用一个的话，**后跑的会把先跑的覆盖掉** ——
    /// 实测预警查完立刻被降水的状态盖住，等于预警那条完全不可观测。
    /// </summary>
    internal static string DebugAlertState = "off";

#if DEBUG
    /// <summary>
    /// 调试用：<c>ISLANDX_WXDEMO=&lt;秒&gt;</c> 时用**假数据**立即发一条降水提醒并停留指定秒数
    /// （0 = 不自动撤下）。
    ///
    /// 存在的理由是这个功能的触发条件**不受控**：得真的快下雨了才看得到。
    /// 而视觉这一层（柱状图的高亮区间、文案换行、展开态排版）需要反复看、反复调 ——
    /// 等一场真的雨来调 UI 是不现实的。假数据只替换"从哪来"，
    /// 走的仍是完整的 Weather.Read → 活动 → 渲染链路。
    /// </summary>
    private static readonly int DemoSeconds =
        int.TryParse(Environment.GetEnvironmentVariable("ISLANDX_WXDEMO"), out var d) && d >= 0 ? d : -1;
#endif

    public async Task StartAsync()
    {
        _running = true;

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.Add("User-Agent", "IslandX");

#if DEBUG
        if (DemoSeconds >= 0)
        {
            EmitDemo();
            return;
        }
#endif

        await TickAsync();
        _timer.Change(RefreshEvery, RefreshEvery);
    }

#if DEBUG
    /// <summary>
    /// 造一场雨（峰值 1.6mm → 判为大雨，连续 5 格 → 75 分钟），
    /// 之后走的是真实的 <see cref="Weather.Read"/> → 活动 → 渲染链路。
    /// </summary>
    private void EmitDemo()
    {
        var now = DateTime.Now;
        var slotSpan = TimeSpan.FromMinutes(15);

        // 对齐到 15 分钟边界，和真实数据源的格子一样 —— 不对齐的话
        // "起始时刻按时间戳算"那条路径就没被走到，等于没测
        var first = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute / 15 * 15, 0);

        var mm = new[] { 0.0, 0.0, 0.0, 0.35, 0.9, 1.6, 1.1, 0.4, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0, 0.0 };
        var slots = new PrecipSlot[mm.Length];
        for (var i = 0; i < mm.Length; i++)
            slots[i] = new PrecipSlot(first + (slotSpan * i), mm[i], mm[i] > 0 ? 85 : 20);

        var outlook = Weather.Read(slots, now);
        var (title, detail) = Weather.Describe(outlook);
        var (from, to) = Weather.HighlightRange(outlook);

        DebugState = $"demo/{title}/{outlook.PeakMm:F2}mm/hl{from}-{to}";

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = "weather:demo",
            ProviderId = ProviderId,
            Priority = ActivityPriority.High,
            Glyph = "",   // 云 E753
            Title = title,
            Subtitle = detail,
            Chart = outlook.Bars,
            ChartCaption = $"未来 {Math.Round(outlook.Window.TotalHours)} 小时 · 每格 {outlook.SlotSpan.TotalMinutes:F0} 分钟",
            ChartFrom = from,
            ChartTo = to,
            AutoDismissAfter = DemoSeconds > 0 ? TimeSpan.FromSeconds(DemoSeconds) : null,
            WantsMainSlot = true,
        });
    }
#endif

    public void Stop()
    {
        _running = false;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);

        _http?.Dispose();
        _http = null;

        _announcedRain = null;
        _announcedAlerts.Clear();
        _region = null;
        DebugState = "off";
        DebugAlertState = "off";
    }

    /// <summary>手填坐标，来自配置。为 null 时走系统定位。</summary>
    public double? ManualLatitude { get; set; }
    public double? ManualLongitude { get; set; }

    /// <summary>
    /// 丢掉缓存的位置与行政区，下一轮重新解析。
    ///
    /// 设置页改完位置要立刻生效。不清缓存的话得等最长 <see cref="RelocateEvery"/>
    /// （6 小时）——用户会以为设置没起作用，而这件事从界面上完全看不出来。
    /// 同时清掉已播报记录：换了地方，旧地方那条"已经提醒过了"不该再拦着新地方的提醒。
    /// </summary>
    public void InvalidateLocation()
    {
        _where = null;
        _locatedAt = DateTime.MinValue;
        _region = null;
        _regionAt = DateTime.MinValue;

        _announcedRain = null;
        _announcedAlerts.Clear();
    }

    private async Task TickAsync()
    {
        if (!_running || _http is null) return;

        try
        {
            var here = await EnsureLocationAsync();
            if (here is null)
            {
                DebugState = $"noloc/{GeoLocation.LastError}";
                return;
            }

            // 预警发了就**不再发降水**。
            //
            // 这不是偏好，是架构约束：仲裁器的 _current 按 providerId 索引，
            // **一个 Provider 同时只能有一个活动** —— 后发的直接覆盖先发的。
            // 之前预警先发、降水后发，结果 5 条预警全被降水盖掉、压根进不了仲裁，
            // 表面看像"优先级没生效"，其实它们根本没走到那一步。
            //
            // 顺序反过来也不对（会变成降水永远被预警盖）。正确做法是让 Provider
            // 自己决定这一轮发什么：有气象预警时，"要下雨了"明显更次要，
            // 何况暴雨预警本身就含着这个信息。
            //
            // 注意这条约束管的是**发什么**，不是**取不取数据**。降水预报照常拉、
            // 照常记进 _outlook，只是有预警时不播报 —— 右键工具栏点「天气」要的
            // 就是那份数据，而"雷雨预警期间雨几时停"恰恰是最想知道的时候。
            // 原先这里是 if (!alerted) await CheckRainAsync(...)，预警一挂上
            // 天气视图就整个变灰。
            var alerted = await CheckAlertsAsync(here.Value);
            await CheckRainAsync(here.Value, announce: !alerted);
        }
        catch (Exception ex)
        {
            // 天气拿不到不该让 Provider 崩掉，更不该影响岛体的其它功能
            DebugState = $"err/{ex.GetType().Name}";
            System.Diagnostics.Debug.WriteLine($"[weather] 刷新失败: {ex.Message}");
        }
    }

    private async Task<GeoLocation.Coordinate?> EnsureLocationAsync()
    {
        if (_where is not null && DateTime.Now - _locatedAt < RelocateEvery) return _where;

        var resolved = await GeoLocation.TryResolveAsync(ManualLatitude, ManualLongitude);
        if (resolved is null) return _where;   // 解析失败时沿用旧坐标，总比什么都没有强

        _where = resolved.Value.Rounded();
        _locatedAt = DateTime.Now;
        return _where;
    }

    // ===== 降水 =====

    /// <param name="announce">
    /// false = 只更新数据不播报。有气象预警时就是这样 —— 见 <see cref="TickAsync"/>。
    /// </param>
    private async Task CheckRainAsync(GeoLocation.Coordinate here, bool announce)
    {
        var url =
            $"https://api.open-meteo.com/v1/forecast?latitude={Fmt(here.Latitude)}"
            + $"&longitude={Fmt(here.Longitude)}"
            + "&minutely_15=precipitation,precipitation_probability"
            + $"&forecast_minutely_15={ForecastSlots}&timezone=auto";

        var json = await _http!.GetStringAsync(url);
        var slots = Weather.ParseMinutely(json);

        if (slots.Length == 0)
        {
            DebugState = $"{here}/noforecast";
            return;
        }

        var outlook = Weather.Read(slots, DateTime.Now);

        // 先无条件记下，再决定要不要报警 —— 这两件事是独立的
        _outlook = outlook;
        _outlookAt = DateTime.Now;

        // 不下雨：把去重标记清掉，下一场雨才报得出来。
        // 漏了这一步的表现是"第一场雨报了，之后再也不报" —— 而那要等到第二场雨
        // 才会暴露，几乎不可能在开发期发现
        if (outlook.StartsIn is null && !outlook.RainingNow)
        {
            _announcedRain = null;
            DebugState = $"{here}/dry/{slots.Length}slot";
            return;
        }

        // 这场雨的起点。正在下的时候用"此刻"，否则用预计开始的时刻
        var startAt = outlook.RainingNow
            ? DateTime.Now.Date + TimeSpan.FromHours(DateTime.Now.Hour)   // 按小时对齐，避免每次刷新都算出新起点
            : DateTime.Now + outlook.StartsIn!.Value;

        // 被预警压着时到此为止：数据已经记下（工具栏的天气视图要用），但不播报。
        //
        // **不能顺手把这场雨记进 _announcedRain** —— 那是"已经报过了"的意思。
        // 记了的话，预警撤销之后这场雨就再也报不出来了，
        // 而那要等到"预警结束但雨还在下"才会显形，几乎不可能在开发期撞见
        if (!announce)
        {
            DebugState = $"{here}/rain@{startAt:HH:mm}/muted";
            return;
        }

        // 与上次播报的是同一场吗？半小时内算同一场 —— 预报会小幅漂移，
        // 严格相等的话每次刷新都会被判成新的一场
        if (_announcedRain is { } last && Math.Abs((startAt - last).TotalMinutes) < 30)
        {
            DebugState = $"{here}/dedup@{last:HH:mm}";
            return;
        }

        _announcedRain = startAt;

        var (title, detail) = Weather.Describe(outlook);
        var (from, to) = Weather.HighlightRange(outlook);

        DebugState = $"{here}/rain@{startAt:HH:mm}/{outlook.PeakMm:F2}mm";

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = $"weather:rain:{startAt:HHmm}",
            ProviderId = ProviderId,
            // High 而不是 Normal：媒体也是 Normal，同级时按谁更新得晚决定，
            // 而媒体开着歌词是每 110ms 推一次的 —— 降水提醒会被它瞬间挤掉。
            // 语义上也说得通：临时地，"要下雨了"确实比"正在播什么歌"重要。
            Priority = ActivityPriority.High,
            Glyph = "",           // 云 E753
            Title = title,
            Subtitle = detail,
            Chart = outlook.Bars,
            ChartCaption = $"未来 {Math.Round(outlook.Window.TotalHours)} 小时 · 每格 {outlook.SlotSpan.TotalMinutes:F0} 分钟",
            ChartFrom = from,
            ChartTo = to,
            AutoDismissAfter = RainDwell,
            WantsMainSlot = true,   // 三层信息塞不进 Split 小圆
        });
    }

    /// <summary>
    /// 主动查看时用的那条活动 —— 右键工具栏点「天气」走这里。
    ///
    /// 和 <see cref="CheckRainAsync"/> 发的那条的区别：那条是**它来找你**
    /// （只在"要下雨了"时才发，几秒后自动撤下）；这条是**你去找它**，
    /// 所以不下雨也要给个答案，而且不自动撤下 —— 什么时候收由指针决定。
    ///
    /// 拿不到数据时返回 null，工具栏据此把按钮置灰，而不是弹一条空的。
    /// </summary>
    public IslandActivity? BuildPeek()
    {
        if (_outlook is not { } outlook) return null;
        if (outlook.Bars.Length == 0) return null;

        // 15 分钟粒度的预报，过了半小时就不能再当"现在"讲了
        if (DateTime.Now - _outlookAt > TimeSpan.FromMinutes(30)) return null;

        var (title, detail) = Weather.Describe(outlook);
        var (from, to) = Weather.HighlightRange(outlook);

        return new IslandActivity
        {
            Id = "weather:peek",
            ProviderId = ProviderId,
            Priority = ActivityPriority.High,
            Glyph = "",
            Title = title,
            Subtitle = detail,
            Chart = outlook.Bars,
            ChartCaption = $"未来 {Math.Round(outlook.Window.TotalHours)} 小时 · 每格 {outlook.SlotSpan.TotalMinutes:F0} 分钟",
            ChartFrom = from,
            ChartTo = to,
            WantsMainSlot = true,

            // 没有 AutoDismissAfter：主动看的东西不该自己跑掉。
            // 它由工具栏钉住、指针离开岛体时解除
        };
    }

    // ===== 灾害预警（中国气象局，无需 API key）=====

    /// <summary>
    /// 拉全国预警，按行政区筛成本地的。
    ///
    /// 接口一次给全国 1300+ 条（约 380KB，实测 690ms）。分页拉要 35 次请求，
    /// 一次拉完反而更省 —— 何况这是 10 分钟一次的后台刷新。
    /// </summary>
    /// <returns>是否发出了预警活动。发了的话这一轮就不再发降水提醒。</returns>
    private async Task<bool> CheckAlertsAsync(GeoLocation.Coordinate here)
    {
        var region = await EnsureRegionAsync(here);
        if (region is not { IsValid: true } where)
        {
            DebugAlertState = $"{here}/noregion/{GeoLocation.LastError}";
            return false;
        }

        string json;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NmcAlarmUrl);

            // 这四个头缺一不可，尤其 Referer —— 缺了大概率被拒
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0");
            request.Headers.TryAddWithoutValidation("Referer", NmcBaseUrl + "/");
            request.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");

            using var response = await _http!.SendAsync(request);
            response.EnsureSuccessStatusCode();
            json = await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            DebugAlertState = $"{here}/alertfail/{ex.GetType().Name}";
            return false;
        }

        var (all, provinceWide) = ParseNmc(json);
        if (all.Length == 0 && provinceWide.Length == 0)
        {
            DebugAlertState = $"{where}/noalert";
            return false;
        }

        // 市县级要求「省名 + 本地名」都命中；省级只要省名命中 —— 那种确实覆盖本地。
        // 只按省筛的话会报出几百公里外的预警：实测本机所在省内 13 条预警
        // 全部在省内其它地级市
        var now = DateTime.Now;
        var mine = Weather.MatchAlerts(all, where, now)
            .Concat(Weather.MatchAlerts(provinceWide, where, now, provinceWide: true))
            .ToArray();

        var live = new HashSet<string>(mine.Select(a => a.Id));

        // 本轮新出现的（没播报过的）
        var fresh = mine.Where(a => !_announcedAlerts.Contains(a.Id)).ToArray();

        // 全部标记为已播报，包括**不打算显示的那些** ——
        // 否则它们下一轮又会被当成"新的"，同一批预警反复弹。
        foreach (var alert in fresh) _announcedAlerts.Add(alert.Id);

        // 解除的预警要从去重集合里摘掉，否则同一个区划再次发布时不会提醒；
        // 集合无上限地长下去也是个泄漏
        _announcedAlerts.IntersectWith(live);

        if (fresh.Length == 0)
        {
            DebugAlertState = $"{where}/{mine.Length}alert(dedup)/{all.Length}nation";
            return false;
        }

        // **只播报最严重的那一条**。同时挂 4 条预警是常事（实测沈阳新民市就是），
        // 逐条弹要占掉一百多秒，而其中最重的那条才是真正要人看到的。
        // 何况一个 Provider 同时只能有一个活动，逐条发也只有最后一条留得下。
        var top = fresh.OrderByDescending(a => Weather.IsSevere(a.Title) ? 1 : 0)
                       .ThenByDescending(a => a.IssueTime, StringComparer.Ordinal)
                       .First();

        var (kind, _) = Weather.SplitAlertTitle(top.Title);
        var severe = Weather.IsSevere(top.Title);

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = $"weather:alert:{top.Id}",
            ProviderId = ProviderId,
            Priority = ActivityPriority.Critical,
            Glyph = "",       // 警告三角 E7BA
            Title = kind,
            Subtitle = Shorten(top.Title),
            // 红/橙停留更久 —— 后果更重，值得多占一会儿。
            // 级别的差异体现在这里，不在优先级上（优先级一律 Critical）
            AutoDismissAfter = severe ? SevereAlertDwell : AlertDwell,
            WantsMainSlot = true,
        });

        DebugAlertState = fresh.Length > 1
            ? $"{where}/alert:{kind}(+{fresh.Length - 1})/{mine.Length}in{all.Length}"
            : $"{where}/alert:{kind}/{mine.Length}in{all.Length}";

        return true;
    }

    /// <summary>
    /// 解析 nmc.cn 的响应。市县级在 <c>data.page.list</c>，省级单独在
    /// <c>data.provinceAlarms</c> —— 两组的筛选规则不同，所以分开返回。
    /// </summary>
    private static (WeatherAlert[] Local, WeatherAlert[] Province) ParseNmc(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return ([], []);

            var list = data.TryGetProperty("page", out var page)
                && page.TryGetProperty("list", out var items)
                    ? ReadAlerts(items)
                    : [];

            var province = data.TryGetProperty("provinceAlarms", out var pa)
                ? ReadAlerts(pa)
                : [];

            return (list, province);
        }
        catch
        {
            // 拿不到预警不该让整个 Provider 崩掉，降水提醒还得继续工作
            return ([], []);
        }

        static WeatherAlert[] ReadAlerts(JsonElement array)
        {
            if (array.ValueKind != JsonValueKind.Array) return [];

            var result = new List<WeatherAlert>(array.GetArrayLength());

            foreach (var item in array.EnumerateArray())
            {
                var id = item.TryGetProperty("alertid", out var a) ? a.GetString() : null;
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
                var time = item.TryGetProperty("issuetime", out var i) ? i.GetString() : null;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;

                result.Add(new WeatherAlert(id, title, time ?? ""));
            }

            return [.. result];
        }
    }

    /// <summary>
    /// 当前行政区。手填优先；否则反查一次并缓存 —— 位置本来就 6 小时才重解析一次。
    /// </summary>
    private async Task<Region?> EnsureRegionAsync(GeoLocation.Coordinate here)
    {
        if (!string.IsNullOrWhiteSpace(ManualRegion))
        {
            var parsed = Weather.ParseRegionText(ManualRegion);
            if (parsed is not null) return parsed;
        }

        if (_region is { IsValid: true } cached && DateTime.Now - _regionAt < RelocateEvery)
            return cached;

        var resolved = await GeoLocation.TryResolveRegionAsync(_http!, here);
        if (resolved is null) return _region;   // 反查失败时沿用旧值，总比没有强

        _region = resolved;
        _regionAt = DateTime.Now;
        return _region;
    }

    /// <summary>
    /// 预警标题整条太长（「辽宁省沈阳市气象台发布暴雨黄色预警信号」），
    /// 副标题只放发布单位那一半，类型与级别已经在标题里了。
    /// </summary>
    private static string? Shorten(string title)
    {
        var (_, where) = Weather.SplitAlertTitle(title);
        if (string.IsNullOrWhiteSpace(where)) return null;

        return where.Length > 22 ? where[..22] + "…" : where;
    }

    /// <summary>坐标格式化。必须用不变文化 —— 中文环境下小数点仍是 '.'，但别赌这个。</summary>
    private static string Fmt(double value) => value.ToString("F2", CultureInfo.InvariantCulture);
}
