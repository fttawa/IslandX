using System.Globalization;
using System.Text.Json;

namespace IslandX.Core;

/// <summary>一格 15 分钟的降水预报。</summary>
public readonly record struct PrecipSlot(DateTime Time, double Millimeters, int Probability);

/// <summary>一条气象预警。</summary>
public readonly record struct WeatherAlert(string Id, string Title, string IssueTime);

/// <summary>
/// 当前所在的行政区，用于把全国预警筛成本地的。
/// </summary>
/// <param name="Province">省级名称，如"辽宁省"。必填 —— 不同省有同名的县。</param>
/// <param name="Locals">市 / 区 / 县级名称，如 ["沈阳市", "浑南区"]。</param>
public readonly record struct Region(string Province, IReadOnlyList<string> Locals)
{
    public bool IsValid => !string.IsNullOrWhiteSpace(Province);

    /// <summary>给人看的形式（探针、日志）。**不要拿它写配置** —— 见 ToConfigText。</summary>
    public override string ToString()
        => Locals.Count == 0 ? Province : $"{Province}/{string.Join("·", Locals)}";

    /// <summary>
    /// 写进 <c>config.json</c> 的形式：**无分隔符拼接**，如「辽宁省沈阳市浑南区」。
    ///
    /// 和 <see cref="ToString"/> 刻意不同。配置文本要能被
    /// <see cref="Weather.ParseRegionText"/> 原样解析回来，而那个函数是按行政后缀
    /// （省/市/区/县…）切的 —— 把 ToString 的 "/" 和 "·" 写进去，
    /// 切出来的第一段就会变成 "/沈阳市"，带着个斜杠去和预警标题比对，永远匹配不上。
    /// 两个方向由自检的往返用例守着（见 WeatherProbe）。
    /// </summary>
    public string ToConfigText() => Province + string.Concat(Locals);
}

/// <summary>对一串降水预报的解读：要不要提醒、几时下、下多久。</summary>
public sealed record RainOutlook
{
    /// <summary>此刻是否正在下。</summary>
    public bool RainingNow { get; init; }

    /// <summary>距开始下雨还有多久。<c>null</c> = 预报窗口内不会下（或已经在下）。</summary>
    public TimeSpan? StartsIn { get; init; }

    /// <summary>这场雨预计持续多久。窗口尾部仍在下时是**下限**（见 <see cref="DurationIsFloor"/>）。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>
    /// 持续时长是不是被预报窗口截断的下限。
    /// 截断时该说"至少还要 1 小时"而不是"1 小时后停" —— 后者是我们不知道的事。
    /// </summary>
    public bool DurationIsFloor { get; init; }

    /// <summary>窗口内的峰值降水量（mm / 15 分钟）。</summary>
    public double PeakMm { get; init; }

    /// <summary>柱状图数据，已按 <see cref="ChartCeilingMm"/> 归一化到 0–1。</summary>
    public double[] Bars { get; init; } = [];

    /// <summary>柱状图每格代表的时长，用于画横轴刻度。</summary>
    public TimeSpan SlotSpan { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>预报窗口一共覆盖多长时间。文案里那句"未来 N 小时"按它算，不写死。</summary>
    public TimeSpan Window { get; init; }
}

/// <summary>
/// 降水预报的解析与解读。
///
/// 和 <see cref="Lyrics"/> 一样刻意做成**纯函数**：这套东西最容易出的错是
/// "该提醒的时候没提醒"，而那在界面上的表现是**什么都没发生** ——
/// 分不清是定位错了、请求失败了、判据没触发、还是去重把它吃掉了。
/// 拆成纯函数就能单独喂样本断言，不必等一场真的雨。
/// </summary>
public static class Weather
{
    /// <summary>
    /// 算"在下雨"的最低降水量（mm / 15 分钟）。
    ///
    /// 0.1mm/15min ≈ 0.4mm/h，是能明显感觉到、会想拿伞的程度。
    /// 再低就是空气里的湿意，为它弹一次提醒只会让人以后忽略这个提醒。
    /// </summary>
    public const double WetMm = 0.1;

    /// <summary>
    /// 同时要求的最低概率。降水量本身已含概率权重，但两个都看才拦得住
    /// "30% 概率下一场大雨"被平均成一个中等数值的情况 —— 那种应该等它更确定再说。
    /// </summary>
    public const int MinProbability = 50;

    /// <summary>柱状图的满格降水量（mm / 15 分钟）。约 8mm/h，暴雨级别。</summary>
    public const double ChartCeilingMm = 2.0;

    /// <summary>
    /// 解读一串预报。<paramref name="now"/> 显式传入而不是取 <c>DateTime.Now</c> ——
    /// 纯函数才能喂固定样本断言。
    /// </summary>
    public static RainOutlook Read(IReadOnlyList<PrecipSlot> slots, DateTime now)
    {
        if (slots.Count == 0) return new RainOutlook();

        var slotSpan = TimeSpan.FromTicks(SlotTicks(slots));

        // 先丢掉**已经完全过去**的格子。数据源可能从当天 00:00 起返回一整天，
        // 那时不过滤的话"第一格"是凌晨，"正在下雨"和"还有多久"全部算错，
        // 而错得很隐蔽 —— 数字看着都合理，只是差了几个小时。
        var from = 0;
        while (from < slots.Count && slots[from].Time + slotSpan <= now) from++;
        if (from >= slots.Count) return new RainOutlook { SlotSpan = slotSpan };

        var window = new PrecipSlot[slots.Count - from];
        for (var i = 0; i < window.Length; i++) window[i] = slots[from + i];

        var bars = new double[window.Length];
        var peak = 0.0;

        for (var i = 0; i < window.Length; i++)
        {
            var mm = Math.Max(0, window[i].Millimeters);
            bars[i] = Math.Clamp(mm / ChartCeilingMm, 0, 1);
            if (mm > peak) peak = mm;
        }

        var covers = slotSpan * window.Length;

        // 第一格就是"此刻所在的时段"。它是否算在下雨，决定了后面是
        // "还有多久开始下"还是"还要下多久"，两句话的语气完全不同。
        var rainingNow = IsWet(window[0]);

        var firstWet = -1;
        for (var i = 0; i < window.Length; i++)
        {
            if (!IsWet(window[i])) continue;
            firstWet = i;
            break;
        }

        if (firstWet < 0)
        {
            // 窗口内都不下。柱状图仍然带上 —— 量没到门槛不等于形状没有信息
            return new RainOutlook
            {
                Bars = bars,
                PeakMm = peak,
                SlotSpan = slotSpan,
                Window = covers,
            };
        }

        // 从第一格湿的往后数连续几格。中间断一格就当这场雨结束：
        // 15 分钟的间歇在体感上就是"停了"，把前后接成一场会报出虚高的时长
        var end = firstWet;
        while (end + 1 < window.Length && IsWet(window[end + 1])) end++;

        // 起始时刻用**格子自己的时间戳**，不是 firstWet × 格长：
        // 第一格的起点通常落在当前时刻之前（格子对齐到 :00/:15/:30/:45），
        // 按格数乘间隔会系统性地把"还有多久"报晚最多一格。
        var startsIn = rainingNow ? (TimeSpan?)null : window[firstWet].Time - now;
        if (startsIn is { Ticks: < 0 }) startsIn = TimeSpan.Zero;

        return new RainOutlook
        {
            RainingNow = rainingNow,
            StartsIn = startsIn,
            Duration = slotSpan * (end - firstWet + 1),
            DurationIsFloor = end == window.Length - 1,
            PeakMm = peak,
            Bars = bars,
            SlotSpan = slotSpan,
            Window = covers,
        };
    }

    private static bool IsWet(PrecipSlot slot)
        => slot.Millimeters >= WetMm && slot.Probability >= MinProbability;

    /// <summary>
    /// 一格的时长。从相邻两格的时间戳实测，而不是写死 15 分钟 ——
    /// 数据源改了粒度（或者哪天换了源）的话，写死的那个数会静默地把时长算错。
    /// </summary>
    private static long SlotTicks(IReadOnlyList<PrecipSlot> slots)
    {
        if (slots.Count < 2) return TimeSpan.FromMinutes(15).Ticks;

        var delta = (slots[1].Time - slots[0].Time).Ticks;
        return delta > 0 ? delta : TimeSpan.FromMinutes(15).Ticks;
    }

    /// <summary>
    /// 解析 Open-Meteo 的 <c>minutely_15</c> 响应。
    /// 缺字段、长度对不齐、值为 null 都当作"没有预报"返回空，不抛异常 ——
    /// 天气拿不到不该让 Provider 崩掉。
    /// </summary>
    public static PrecipSlot[] ParseMinutely(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("minutely_15", out var block)) return [];

            if (!block.TryGetProperty("time", out var times)
                || !block.TryGetProperty("precipitation", out var precip)
                || !block.TryGetProperty("precipitation_probability", out var prob))
            {
                return [];
            }

            var count = Math.Min(times.GetArrayLength(),
                Math.Min(precip.GetArrayLength(), prob.GetArrayLength()));

            var result = new List<PrecipSlot>(count);

            for (var i = 0; i < count; i++)
            {
                // 时间是本地时区的无偏移字符串（请求带了 timezone=auto）
                if (!DateTime.TryParse(times[i].GetString(), CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var time))
                {
                    continue;
                }

                var mm = precip[i].ValueKind == JsonValueKind.Number ? precip[i].GetDouble() : 0;
                var pct = prob[i].ValueKind == JsonValueKind.Number ? prob[i].GetInt32() : 0;

                result.Add(new PrecipSlot(time, mm, pct));
            }

            return [.. result];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 把解读结果写成一句人话。
    ///
    /// 时长在窗口尾部被截断时说"至少"，不说一个确切的结束时间 ——
    /// 那是我们不知道的事，报出来就是编。
    /// </summary>
    public static (string Title, string Detail) Describe(RainOutlook outlook)
    {
        var level = outlook.PeakMm switch
        {
            >= 1.5 => "大雨",
            >= 0.6 => "中雨",
            _ => "小雨",
        };

        if (outlook.RainingNow)
        {
            var tail = outlook.DurationIsFloor
                ? $"至少还要 {Humanize(outlook.Duration)}"
                : $"预计还有 {Humanize(outlook.Duration)}";
            return ($"正在下{level}", tail);
        }

        if (outlook.StartsIn is not { } startsIn)
            return ("暂无降水", $"未来 {Humanize(outlook.Window)}");

        var when = startsIn <= TimeSpan.FromMinutes(5) ? "马上" : $"{Humanize(startsIn)}后";
        var lasts = outlook.DurationIsFloor
            ? $"持续至少 {Humanize(outlook.Duration)}"
            : $"持续约 {Humanize(outlook.Duration)}";

        return ($"{when}有{level}", lasts);
    }

    /// <summary>
    /// 柱状图里该高亮的区间 —— 光看柱子高低分不清哪几格算作"这一场"。
    /// <c>(-1, -1)</c> 表示没有该高亮的区间。
    ///
    /// ⚠ 这个函数原先隐含一个前提：**只在"正在下"或"即将下"时调用**，
    /// 因为播报路径只在那两种情况下才发活动。于是 <c>StartsIn!.Value</c>
    /// 那个 <c>!</c> 是成立的。
    ///
    /// 加了"主动查看"之后前提就破了 —— 不下雨也要给答案，而那时
    /// <c>RainingNow=false</c> 且 <c>StartsIn=null</c>，`.Value` 直接抛
    /// <c>InvalidOperationException</c>。抛在 UI 线程上就是整个应用卡死，
    /// 而且**只在天晴时**复现（开发期一直在下雨，从没碰到）。
    ///
    /// 所以现在自己判断，不再依赖调用方的前提 —— 并且**从 Provider 搬到了这里**，
    /// 好让自检能直接喂样本。它原先是 <c>WeatherProvider</c> 的 private 方法，
    /// 于是 30 项天气自检一条都覆盖不到它。
    /// </summary>
    public static (int From, int To) HighlightRange(RainOutlook outlook)
    {
        if (outlook.Bars.Length == 0 || outlook.SlotSpan <= TimeSpan.Zero) return (-1, -1);

        // 既不在下、也没有开始时刻 —— 窗口内压根没有雨，没什么可高亮
        if (!outlook.RainingNow && outlook.StartsIn is null) return (-1, -1);

        var from = outlook.RainingNow
            ? 0
            : (int)Math.Floor(outlook.StartsIn!.Value / outlook.SlotSpan);

        var count = (int)Math.Round(outlook.Duration / outlook.SlotSpan);

        from = Math.Clamp(from, 0, outlook.Bars.Length - 1);
        var to = Math.Clamp(from + Math.Max(1, count) - 1, from, outlook.Bars.Length - 1);

        return (from, to);
    }

    // ===== 气象预警 =====

    /// <summary>
    /// 从**全国**预警列表里筛出本地的。
    ///
    /// 中国气象局的接口一次给全国 1300+ 条（分省市县各级），而要用的只有本地那几条。
    /// 筛法是两级都必须命中：
    ///
    /// - **省名必须匹配**。不同省有同名的县（"城关区"之类），只按市县名匹配必然误报。
    /// - 省名之外，还要命中至少一个本地名（市 / 区 / 县）。
    ///   只匹配省的话会报出几百公里外的预警 —— 实测本机所在省内有 13 条预警，
    ///   全部在省内其它地级市，与本地无关。
    ///
    /// 省级预警（覆盖整省，如"辽宁省气象台发布山洪灾害预警"）走
    /// <paramref name="provinceWide"/>，只需省名命中 —— 那种确实与本地有关。
    /// </summary>
    public static WeatherAlert[] MatchAlerts(
        IReadOnlyList<WeatherAlert> alerts,
        Region region,
        DateTime now,
        bool provinceWide = false)
    {
        if (!region.IsValid || alerts.Count == 0) return [];

        var result = new List<WeatherAlert>();

        foreach (var alert in alerts)
        {
            var title = alert.Title;
            if (string.IsNullOrWhiteSpace(title)) continue;
            if (!title.Contains(region.Province, StringComparison.Ordinal)) continue;
            if (IsStale(alert, now)) continue;

            if (provinceWide)
            {
                result.Add(alert);
                continue;
            }

            // **发布单位的最后一级**必须是我所在的市或区县，不能只是"标题里出现过"。
            //
            // 只做包含匹配的话，「X省Y市Z县气象台」也会因为含"Y市"而命中 ——
            // 而那些下辖县距市区动辄一两百公里。
            // 实测这条规则之前一次放进来 5 条全是外县的预警。
            var issuer = LastAdminLevel(title);
            if (issuer is null) continue;

            foreach (var local in region.Locals)
            {
                if (string.IsNullOrWhiteSpace(local)) continue;
                if (!string.Equals(issuer, local, StringComparison.Ordinal)) continue;

                result.Add(alert);
                break;
            }
        }

        return [.. result];
    }

    /// <summary>
    /// 预警是不是已经过期。
    ///
    /// nmc.cn 的列表里带着十几个小时前发布的条目，而去重是按 id 的 ——
    /// 不过滤的话**每次重启都会把一堆隔夜预警当新消息连着弹出来**。
    /// 实测启动时一次弹了 5 条昨晚 21:50–23:50 发布的。
    /// </summary>
    private static bool IsStale(WeatherAlert alert, DateTime now)
    {
        if (!TryParseIssueTime(alert.IssueTime, out var issued)) return false;

        var age = now - issued;
        return age > MaxAlertAge || age < -TimeSpan.FromHours(1);
    }

    /// <summary>
    /// 预警的最长有效期。多数气象预警的时效在数小时内，12 小时之外的不该再当"新消息"。
    /// 未来 1 小时以上的时间戳也丢掉 —— 那是数据出了问题，不是预告。
    /// </summary>
    public static readonly TimeSpan MaxAlertAge = TimeSpan.FromHours(12);

    /// <summary>nmc.cn 的时间格式是 <c>2026/08/24 23:50</c>。</summary>
    private static bool TryParseIssueTime(string text, out DateTime value)
        => DateTime.TryParseExact(
            text, ["yyyy/MM/dd HH:mm", "yyyy/MM/dd HH:mm:ss", "yyyy-MM-dd HH:mm"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out value);

    /// <summary>
    /// 发布单位的最后一级行政区。「辽宁省沈阳市康平县气象台发布…」→「康平县」。
    /// 取不到返回 null。
    /// </summary>
    /// <summary>
    /// 解析手填的行政区文本，如「辽宁省沈阳市浑南区」。按行政后缀切，第一段作省。
    ///
    /// 原来长在 WeatherProvider 里。搬到这里是因为设置页也要用它 ——
    /// 一份在产品里跑、一份在设置页做校验的话，两边迟早对不上，
    /// 而对不上的表现是"设置页说填对了、实际匹配不到任何预警"。
    /// </summary>
    public static Region? ParseRegionText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var parts = new List<string>();
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is not ('省' or '市' or '区' or '县' or '旗' or '盟' or '州')) continue;

            var part = text[start..(i + 1)].Trim();
            if (part.Length > 1) parts.Add(part);
            start = i + 1;
        }

        if (parts.Count == 0) return null;

        return new Region(parts[0], parts.Count > 1 ? parts[1..] : []);
    }

    public static string? LastAdminLevel(string title)
    {
        var idx = title.IndexOf("发布", StringComparison.Ordinal);
        var issuer = idx > 0 ? title[..idx] : title;

        var end = -1;
        for (var i = issuer.Length - 1; i >= 0; i--)
        {
            if (issuer[i] is not ('省' or '市' or '区' or '县' or '旗' or '盟' or '州')) continue;
            end = i;
            break;
        }

        if (end < 0) return null;

        // 往前找上一个行政后缀，两者之间就是这一级的名字
        var start = 0;
        for (var i = end - 1; i >= 0; i--)
        {
            if (issuer[i] is not ('省' or '市' or '区' or '县' or '旗' or '盟' or '州')) continue;
            start = i + 1;
            break;
        }

        var name = issuer[start..(end + 1)].Trim();
        return name.Length > 1 ? name : null;
    }

    /// <summary>
    /// 把预警标题拆成「类型 + 级别」与「发布单位」。
    ///
    /// 原标题形如「辽宁省沈阳市气象台发布暴雨黄色预警信号」，
    /// 整条放岛上太长，而真正要一眼看到的是**下什么、多严重**。
    /// </summary>
    public static (string Kind, string Where) SplitAlertTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return ("气象预警", "");

        var idx = title.IndexOf("发布", StringComparison.Ordinal);
        if (idx < 0) return (title, "");

        var where = title[..idx];
        var kind = title[(idx + 2)..];

        // 去掉"预警信号"这个后缀，"暴雨黄色"比"暴雨黄色预警信号"更适合当标题
        kind = kind.Replace("预警信号", "", StringComparison.Ordinal)
                   .Replace("预警", "", StringComparison.Ordinal)
                   .Trim();

        // 发布单位去掉"气象台"，留下地名
        where = where.Replace("气象台", "", StringComparison.Ordinal).Trim();

        return (kind.Length == 0 ? "气象预警" : kind, where);
    }

    /// <summary>
    /// 预警级别。中国气象预警按颜色分四级，蓝 &lt; 黄 &lt; 橙 &lt; 红。
    /// 红、橙用 Critical 压过一切；蓝、黄用 High，与降水提醒同级。
    /// </summary>
    public static bool IsSevere(string title)
        => title.Contains('红', StringComparison.Ordinal)
        || title.Contains('橙', StringComparison.Ordinal);

    private static string Humanize(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);

        if (minutes < 60) return $"{minutes} 分钟";
        if (minutes % 60 == 0) return $"{minutes / 60} 小时";

        return $"{minutes / 60} 小时 {minutes % 60} 分钟";
    }
}
