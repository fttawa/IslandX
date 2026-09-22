using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using IslandX.Core;
using Windows.Devices.Geolocation;

// 天气链路的可行性探针。在往产品里加 WeatherProvider **之前**先跑它，
// 因为这条链路上有两个不受我们控制的环节，任何一个不通，功能就无从谈起：
//
//   1. 系统定位。非打包的桌面应用能否拿到坐标，取决于系统隐私设置里
//      「允许桌面应用访问你的位置」。关掉的话 RequestAccessAsync 直接 Denied，
//      而这一点从代码里看不出来，只能实机试。
//   2. 天气数据源。选的是 Open-Meteo：免费、**不需要 API key**、有 15 分钟粒度的
//      降水预报。不需要 key 这一条是硬要求 —— 需要注册的源没法开箱即用。
//
// ⚠ 这个探针会把坐标发到 api.open-meteo.com。输出里的经纬度**降到 2 位小数**
//   （约 1km），够验证"取到的是不是本地天气"，又不至于把精确住址打进日志。

Console.OutputEncoding = System.Text.Encoding.UTF8;

// 判据自检先跑。它不需要网络也不需要定位，却能覆盖最容易出错的那一环 ——
// "该提醒时没提醒"在界面上的表现是**什么都没发生**，事后根本无从归因。
// 放在最前面还有个用处：链路探测挂了的时候，至少知道判据本身是好的。
// 歌词翻译的判据也在这里跑。放进"天气"探针略显别扭，但它是唯一一个
// **引用了产品代码**的自检宿主 —— 另起一个项目只为多跑十几条断言不划算，
// 而把判据复制一份到别处正是这套自检要避免的事。
if (args.Contains("--lyrics"))
{
    return LyricSelfTest() ? 0 : 2;
}

if (args.Contains("--sessions"))
{
    return SessionSelfTest() ? 0 : 2;
}

if (args.Contains("--phira"))
{
    return PhiraSelfTest() ? 0 : 2;
}

// 判据自检管不到的那一半（Phira）：拿**真存档**过一遍解析器。
// 自检喂的是我手写的样本，而样本编码的是我以为的格式 —— 这一环在歌词上已经栽过一次
// （tlyric 格式判断错，自检全绿而线上一行译文都没有）。
//
// 只打印曲名与成绩。存档里的 me / tokens 一概不读 —— 解析器本身就只看 charts[]。
if (args.Contains("--startmenu"))
{
    // IShellLink 的 vtable 顺序写错了会**静默失败**（调用跑到相邻的槽上），
    // 所以必须做一次真正的往返：写 → 读回目标 → 删 → 确认没了。
    //
    // ⚠ 这个探针是独立 exe，所以写出来的快捷方式指向的是**探针自己**。
    //
    // 而且它写的是**真实的开始菜单目录**，用户可能已经在那里注册过 IslandX 了。
    // 第一版测完直接删，把人家真正的快捷方式一起删掉了 ——
    // **会破坏被测对象的探针比没有探针更糟**。所以先备份原有的目标路径，
    // 测完原样写回去。
    Console.WriteLine("=== 开始菜单注册往返（会真的写一次，测完恢复原状）===");

    var lnkPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "IslandX.lnk");

    byte[]? backup = System.IO.File.Exists(lnkPath)
        ? System.IO.File.ReadAllBytes(lnkPath)
        : null;

    Console.WriteLine($"  原有快捷方式      : {(backup is null ? "无" : $"有（{backup.Length} 字节，测完原样写回）")}");

    var before = StartMenu.IsRegistered;
    Console.WriteLine($"  起始状态          : {(before ? "已注册" : "未注册")}");

    var ok = StartMenu.Set(true);
    Console.WriteLine($"  写入              : {(ok ? "成功" : "失败")}");

    var link = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "IslandX.lnk");
    Console.WriteLine($"  文件是否存在      : {System.IO.File.Exists(link)}");
    Console.WriteLine($"  IsRegistered 回读 : {StartMenu.IsRegistered}");
    Console.WriteLine($"  应指向            : {Environment.ProcessPath}");

    var removed = StartMenu.Set(false);
    Console.WriteLine($"  删除              : {(removed ? "成功" : "失败")}");
    Console.WriteLine($"  清理后仍存在      : {System.IO.File.Exists(link)}");

    // 把原有的那个还回去。整份字节写回，不重新生成 ——
    // 重新生成出来的未必和用户原来那个一模一样（图标、窗口状态之类都在里面）
    if (backup is not null)
    {
        System.IO.File.WriteAllBytes(lnkPath, backup);
        Console.WriteLine($"  已恢复原有快捷方式: {System.IO.File.Exists(lnkPath)}");
    }

    var clean = ok && (backup is not null ? System.IO.File.Exists(link) : !System.IO.File.Exists(link));
    Console.WriteLine();
    Console.WriteLine(clean ? "OK 往返通过，原状已恢复" : "FAIL 往返失败");
    return clean ? 0 : 2;
}

if (args.Contains("--zh"))
{
    Console.WriteLine("=== 繁体 → 简体（LCMapStringEx）===");

    string[] samples =
    [
        "我還年輕 我還年輕", "老王樂隊", "山海", "草東沒有派對", "醜奴兒",
        "已经是简体了", "Bohemian Rhapsody",
        "月が綺麗ねと言われたい！",   // 日文：会被逐字映射，正是"不能无条件转"的例子
    ];

    foreach (var x in samples)
    {
        var y = ChineseText.ToSimplified(x);
        var mark = ChineseText.DiffersWhenSimplified(x) ? "变了" : "没变";
        Console.WriteLine($"  {mark}  「{x}」 → 「{y}」");
    }

    return 0;
}

if (args.Contains("--phiraread"))
{
    var i = Array.IndexOf(args, "--phiraread");
    var path = i + 1 < args.Length ? args[i + 1] : null;

    if (path is null)
    {
        var proc = System.Diagnostics.Process.GetProcessesByName("phira-main").FirstOrDefault();
        var root = proc?.MainModule?.FileName is { } exe
            ? System.IO.Path.GetDirectoryName(exe) : null;
        path = root is null ? null : System.IO.Path.Combine(root, "data", "data.json");
    }

    if (path is null || !System.IO.File.Exists(path))
    {
        Console.WriteLine("找不到存档。Phira 没在运行的话，把 data.json 的路径当参数传进来。");
        return 2;
    }

    Console.WriteLine($"=== 真存档：{path} ===");

    var text = System.IO.File.ReadAllText(path);
    var records = PhiraRecords.Read(text);

    Console.WriteLine($"解析出 {records.Count} 条成绩\n");

    foreach (var r in records.Values.OrderByDescending(r => r.Score).Take(8))
    {
        Console.WriteLine($"  {r.Name}");
        Console.WriteLine($"    {PhiraRecords.Describe(r)}   [key={r.Key}]");
    }

    // 这一条是真正的判据：解析器如果把 charts[] 的形状看错了，
    // 上面会打印 "0 条" 而不是报错 —— 静默为零正是最难归因的那种失败
    if (records.Count == 0)
    {
        Console.WriteLine("\n✗ 一条都没解析出来。存档里有 charts[] 却读不到，说明形状判断错了。");
        return 2;
    }

    // 顺带验一下 Diff：把最高分那条改低，再 Diff 回去应当报出它
    var top = records.Values.OrderByDescending(r => r.Score).First();
    var lowered = new Dictionary<string, PhiraRecord>(records)
    {
        [top.Key] = top with { Score = top.Score - 1000, FullCombo = false },
    };

    var back = PhiraRecords.Diff(lowered, records);
    Console.WriteLine($"\n模拟刷新最高分那条 → Diff 报出 {back.Length} 条"
        + (back.Length == 1 ? $"：{back[0].Name} {PhiraRecords.Describe(back[0])}" : ""));

    return back.Length == 1 ? 0 : 2;
}

// 判据自检管不到的那一半：真的去网易云拉一首，看 tlyric 有没有一路带到 LyricLine。
// 自检喂的是我手写的样本，而样本编码的是我以为的格式 —— 格式判断错的话，
// 自检全绿而线上一行译文都没有，这个坑本轮已经踩过（详见 PLAN.md 的教训）。
//
// ⚠ 会把曲名发到 music.163.com。
if (args.Contains("--lyricfetch"))
{
    var i = Array.IndexOf(args, "--lyricfetch");
    var title = i + 1 < args.Length ? args[i + 1] : "Hey There Delilah";
    var artist = i + 2 < args.Length ? args[i + 2] : "";

    Console.WriteLine($"=== 实拉歌词：「{title}」{artist} ===");

    using var svc = new LyricsService();
    var done = new ManualResetEventSlim();
    svc.TryGet(title, artist, null, out var lines, done.Set);

    if (lines is null && !done.Wait(TimeSpan.FromSeconds(15)))
    {
        Console.WriteLine("  超时，没拉到");
        return 2;
    }

    svc.TryGet(title, artist, null, out lines);

    if (lines is null || lines.Length == 0)
    {
        Console.WriteLine($"  没有歌词（LastError={svc.LastError}）");
        return 2;
    }

    var translated = lines.Count(l => l.Translation is not null);
    Console.WriteLine($"  {lines.Length} 行，其中 {translated} 行带译文");

    foreach (var l in lines.Take(6))
        Console.WriteLine($"    [{l.Time.TotalSeconds,6:F2}] {l.Text}\n             ↳ {l.Translation ?? "（无译文）"}");

    // 只要有一行配上，就说明 tlyric → Parse → LyricLine.Translation 整条通了。
    // 中文歌本来就该是 0 行，所以这里不把 0 当失败，只是照实说
    Console.WriteLine(translated > 0
        ? "  ✓ 译文链路通"
        : "  · 这首没有译文（中文歌正常如此；外语歌为 0 才说明链路断了）");

    return 0;
}

if (args.Contains("--selftest") || args.Length == 0)
{
    if (!SelfTest()) return 2;
    Console.WriteLine();

    if (!LyricSelfTest()) return 2;
    Console.WriteLine();

    if (!SessionSelfTest()) return 2;
    Console.WriteLine();

    if (!PhiraSelfTest()) return 2;
    Console.WriteLine();
}

if (args.Contains("--selftest")) return 0;

// 字形码位必须**看过再用**。Segoe Fluent Icons 的天气图标散落在 E9xx–EAxx，
// 靠记忆挑一个的结果通常是岛体上出现一个方框或者完全不相干的图标，
// 而那只有把程序跑起来才会发现。渲染成对照图，一眼挑。
if (args.Contains("--glyphs"))
{
    // --glyphs <输出png> [起始码位hex] [结束码位hex]
    var outPath = args.Length > 1 ? args[1] : "glyphs.png";
    var lo = args.Length > 2 ? Convert.ToInt32(args[2], 16) : 0xE700;
    var hi = args.Length > 3 ? Convert.ToInt32(args[3], 16) : 0xE8FF;
    RenderGlyphSheet(outPath, lo, hi);
    return 0;
}

Console.WriteLine("=== 1. 系统定位（Windows.Devices.Geolocation）===");

double lat, lon;

try
{
    var access = await Geolocator.RequestAccessAsync();
    Console.WriteLine($"权限     : {access}");

    if (access != GeolocationAccessStatus.Allowed)
    {
        Console.WriteLine();
        Console.WriteLine("定位不可用。若要启用：设置 → 隐私和安全性 → 位置，");
        Console.WriteLine("打开「位置服务」与「让桌面应用访问你的位置」。");
        Console.WriteLine("产品侧需要有手动填经纬度的回落路径。");
        return 1;
    }

    // DesiredAccuracy 用 Default 而不是 High：天气只需要城市级精度，
    // High 会去开 GPS/更精确的定位源，更慢也更耗电，还要更高的权限门槛
    var locator = new Geolocator { DesiredAccuracyInMeters = 3000 };

    var started = DateTime.Now;
    var position = await locator.GetGeopositionAsync();
    var elapsed = (DateTime.Now - started).TotalMilliseconds;

    var point = position.Coordinate.Point.Position;
    lat = point.Latitude;
    lon = point.Longitude;

    Console.WriteLine($"耗时     : {elapsed:F0}ms");
    Console.WriteLine($"坐标     : {lat:F2}, {lon:F2}   （已降到 2 位小数）");
    Console.WriteLine($"精度     : ±{position.Coordinate.Accuracy:F0}m");
    Console.WriteLine($"来源     : {position.Coordinate.PositionSource}");

    // CivicAddress：如果系统能直接给出省/市，就不必再找反向地理编码服务。
    // 桌面上通常是空的（要看定位提供程序），但值一试 —— 少一个第三方依赖
    var civic = position.CivicAddress;
    Console.WriteLine(
        $"行政区   : 省={Blank(civic?.State)} 市={Blank(civic?.City)} "
        + $"国={Blank(civic?.Country)} 邮编={Blank(civic?.PostalCode)}");
}
catch (Exception ex)
{
    Console.WriteLine($"定位失败 : {ex.GetType().Name} — {ex.Message}");
    return 1;
}

Console.WriteLine();
Console.WriteLine("=== 2. Open-Meteo 降水预报（无需 API key）===");

// minutely_15 是 15 分钟粒度；取 8 段 = 未来 2 小时，正好够画一条柱状图。
// precipitation 是降水量(mm)，precipitation_probability 是概率(%)。
// 两个都要：只看概率会把"70% 概率的毛毛雨"报成大事，只看量又会漏掉不确定性。
var url =
    $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString("F4", CultureInfo.InvariantCulture)}"
    + $"&longitude={lon.ToString("F4", CultureInfo.InvariantCulture)}"
    + "&minutely_15=precipitation,precipitation_probability"
    + "&current=temperature_2m,precipitation,weather_code"
    + "&forecast_minutely_15=12&timezone=auto";

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    http.DefaultRequestHeaders.Add("User-Agent", "IslandX-WeatherProbe");

    var started = DateTime.Now;
    var json = await http.GetStringAsync(url);
    var elapsed = (DateTime.Now - started).TotalMilliseconds;

    Console.WriteLine($"请求耗时 : {elapsed:F0}ms   响应 {json.Length} 字节");

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    if (root.TryGetProperty("timezone", out var tz))
        Console.WriteLine($"时区     : {tz.GetString()}");

    if (root.TryGetProperty("current", out var current))
    {
        var temp = current.TryGetProperty("temperature_2m", out var t) ? t.GetDouble() : double.NaN;
        var code = current.TryGetProperty("weather_code", out var c) ? c.GetInt32() : -1;
        var now = current.TryGetProperty("precipitation", out var p) ? p.GetDouble() : 0;
        Console.WriteLine($"当前     : {temp:F1}°C  降水 {now:F2}mm  天气码 {code}（{Describe(code)}）");
    }

    if (root.TryGetProperty("minutely_15", out var minutely))
    {
        var times = minutely.GetProperty("time");
        var precip = minutely.GetProperty("precipitation");
        var prob = minutely.GetProperty("precipitation_probability");

        Console.WriteLine();
        Console.WriteLine("未来 3 小时（15 分钟一格）：");
        Console.WriteLine("  时刻     降水mm  概率  强度");

        for (var i = 0; i < times.GetArrayLength(); i++)
        {
            var time = times[i].GetString() ?? "";
            var mm = precip[i].ValueKind == JsonValueKind.Number ? precip[i].GetDouble() : 0;
            var pct = prob[i].ValueKind == JsonValueKind.Number ? prob[i].GetInt32() : 0;

            // 柱状图的雏形：产品里画的就是这个，先用字符看一眼数据合不合理
            var bars = (int)Math.Round(Math.Min(mm, 4) / 4 * 20);
            var clock = time.Length >= 16 ? time[11..16] : time;

            Console.WriteLine($"  {clock}    {mm,5:F2}   {pct,3}%  {new string('█', bars)}");
        }
    }

    Console.WriteLine();
    Console.WriteLine("✓ 数据源可用");
}
catch (Exception ex)
{
    Console.WriteLine($"请求失败 : {ex.GetType().Name} — {ex.Message}");
    return 1;
}

Console.WriteLine();
Console.WriteLine("=== 3. 灾害预警：中国气象局（无需 API key）===");

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    http.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
    // Referer 是关键：缺了大概率被拒
    http.DefaultRequestHeaders.Add("Referer", "https://www.nmc.cn/");
    http.DefaultRequestHeaders.Add("Accept", "application/json,text/plain,*/*");
    http.DefaultRequestHeaders.Add("Accept-Language", "zh-CN,zh;q=0.9");

    var started = DateTime.Now;
    var json = await http.GetStringAsync("https://www.nmc.cn/rest/findAlarm?pageNo=1&pageSize=40");
    var elapsed = (DateTime.Now - started).TotalMilliseconds;

    using var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;

    Console.WriteLine($"请求耗时 : {elapsed:F0}ms   code={root.GetProperty("code")}");

    var page = root.GetProperty("data").GetProperty("page");
    Console.WriteLine($"全国在效 : {page.GetProperty("count")} 条，共 {page.GetProperty("totalPage")} 页");

    // 这是**全国**列表，要用的是本地那几条 —— 关键在于怎么筛。
    // alertid 的前 6 位是国标行政区划码，比在 title 里做字符串匹配可靠得多：
    // 「辽宁省沈阳市…」这种前缀在不同层级里长度不一，切错就全错了。
    Console.WriteLine();
    Console.WriteLine("前 10 条（alertid 前 6 位 = 国标区划码）：");

    foreach (var item in page.GetProperty("list").EnumerateArray().Take(10))
    {
        var id = item.GetProperty("alertid").GetString() ?? "";
        var title = item.GetProperty("title").GetString() ?? "";
        var region = id.Length >= 6 ? id[..6] : "??????";
        Console.WriteLine($"  {region}  {title}");
    }

    if (root.GetProperty("data").TryGetProperty("provinceAlarms", out var prov)
        && prov.ValueKind == JsonValueKind.Array)
    {
        Console.WriteLine();
        Console.WriteLine($"省级预警 : {prov.GetArrayLength()} 条（单独一组，覆盖整省）");
        foreach (var item in prov.EnumerateArray().Take(3))
            Console.WriteLine($"  {item.GetProperty("title").GetString()}");
    }

    Console.WriteLine();
    Console.WriteLine("✓ 无需 API key，接口可用");
}
catch (Exception ex)
{
    Console.WriteLine($"请求失败 : {ex.GetType().Name} — {ex.Message}");
}

Console.WriteLine();
Console.WriteLine("=== 4. 坐标 → 行政区划：怎么把全国列表筛成本地 ===");
Console.WriteLine("手上只有经纬度，而预警是按区划组织的。试一下反向地理编码：");

try
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    // Nominatim 要求带能识别的 UA，否则会被封
    http.DefaultRequestHeaders.Add("User-Agent", "IslandX-WeatherProbe/1.0");

    var geoUrl = $"https://nominatim.openstreetmap.org/reverse?lat={lat.ToString("F4", CultureInfo.InvariantCulture)}"
        + $"&lon={lon.ToString("F4", CultureInfo.InvariantCulture)}"
        + "&format=json&zoom=10&accept-language=zh-CN";

    var started = DateTime.Now;
    var json = await http.GetStringAsync(geoUrl);
    var elapsed = (DateTime.Now - started).TotalMilliseconds;

    using var doc = JsonDocument.Parse(json);
    Console.WriteLine($"请求耗时 : {elapsed:F0}ms");

    if (doc.RootElement.TryGetProperty("address", out var addr))
    {
        foreach (var field in new[] { "state", "city", "county", "town", "district" })
        {
            if (addr.TryGetProperty(field, out var v))
                Console.WriteLine($"  {field,-10}: {v.GetString()}");
        }
    }

    if (doc.RootElement.TryGetProperty("display_name", out var dn))
        Console.WriteLine($"  完整地址  : {dn.GetString()}");
}
catch (Exception ex)
{
    Console.WriteLine($"反向地理编码失败 : {ex.GetType().Name} — {ex.Message}");
}

return 0;

static string Blank(string? s) => string.IsNullOrWhiteSpace(s) ? "(空)" : s;

// ===== 字形对照表 =====

static void RenderGlyphSheet(string path, int first, int last)
{
    const int Columns = 16;
    const double Cell = 56;

    var count = last - first + 1;
    var rows = (int)Math.Ceiling(count / (double)Columns);

    var visual = new System.Windows.Media.DrawingVisual();
    using (var dc = visual.RenderOpen())
    {
        dc.DrawRectangle(
            System.Windows.Media.Brushes.Black, null,
            new System.Windows.Rect(0, 0, Columns * Cell, rows * Cell));

        var icon = new System.Windows.Media.Typeface(
            new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            System.Windows.FontStyles.Normal,
            System.Windows.FontWeights.Normal,
            System.Windows.FontStretches.Normal);

        var label = new System.Windows.Media.Typeface("Consolas");

        for (var i = 0; i < count; i++)
        {
            var code = first + i;
            var x = (i % Columns) * Cell;
            var y = (i / Columns) * Cell;

            var glyph = new System.Windows.Media.FormattedText(
                char.ConvertFromUtf32(code),
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                icon, 22, System.Windows.Media.Brushes.White, 1.0);

            dc.DrawText(glyph, new System.Windows.Point(x + (Cell - glyph.Width) / 2, y + 6));

            var text = new System.Windows.Media.FormattedText(
                code.ToString("X4"),
                CultureInfo.InvariantCulture,
                System.Windows.FlowDirection.LeftToRight,
                label, 9, System.Windows.Media.Brushes.Gray, 1.0);

            dc.DrawText(text, new System.Windows.Point(x + (Cell - text.Width) / 2, y + 38));
        }
    }

    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
        (int)(Columns * Cell), (int)(rows * Cell), 96, 96,
        System.Windows.Media.PixelFormats.Pbgra32);
    bitmap.Render(visual);

    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));

    using var stream = File.Create(path);
    encoder.Save(stream);

    Console.WriteLine($"已渲染 U+{first:X4}–U+{last:X4} 共 {count} 个字形 → {path}");
}

// ===== 判据自检 =====
//
// 每个用例针对一个**具体的错法**，不是为了凑覆盖率。注释写的是"错了会怎样"，
// 因为这些错在界面上全都长成同一个样子：什么都没发生。

static bool SelfTest()
{
    Console.WriteLine("=== 0. 判据自检（Weather.Read，不联网）===");

    var t0 = new DateTime(2026, 8, 25, 12, 0, 0);
    var pass = 0;
    var fail = 0;

    void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine($"  ✓ {name}"); }
        else { fail++; Console.WriteLine($"  ✗ {name} —— {detail}"); }
    }

    // 造一串格子：mm 数组按 15 分钟一格铺开，概率统一给 80（够过门槛）
    static PrecipSlot[] Slots(DateTime start, params double[] mm)
    {
        var result = new PrecipSlot[mm.Length];
        for (var i = 0; i < mm.Length; i++)
            result[i] = new PrecipSlot(start.AddMinutes(15 * i), mm[i], 80);
        return result;
    }

    // 全干：不该报任何降水。错了就是天天弹"要下雨"，用户很快学会无视它
    var dry = Weather.Read(Slots(t0, 0, 0, 0, 0), t0);
    Check("全干不报雨", dry.StartsIn is null && !dry.RainingNow, $"StartsIn={dry.StartsIn}");

    // 第三格开始下：应报"30 分钟后"。
    // 若按 firstWet×格长 算会得到 30 分钟没错，但下一个用例能拆穿那种算法
    var soon = Weather.Read(Slots(t0, 0, 0, 0.5, 0.5), t0);
    Check("30 分钟后开始",
        soon.StartsIn == TimeSpan.FromMinutes(30) && !soon.RainingNow,
        $"StartsIn={soon.StartsIn}");

    // 格子边界不对齐当前时刻：数据源给的格子是 :00/:15/:30，而现在是 12:07。
    // 按格数乘间隔会算成 30 分钟（错），按时间戳算是 23 分钟（对）。
    // 错了的表现是"说好半小时后下，结果 20 分钟就下了" —— 每次都晚报最多一格
    var offset = Weather.Read(Slots(t0, 0, 0, 0.5, 0.5), t0.AddMinutes(7));
    Check("起始时刻按时间戳而非格数",
        offset.StartsIn == TimeSpan.FromMinutes(23),
        $"StartsIn={offset.StartsIn}（按格数算会是 30 分钟）");

    // 已经过去的格子要丢掉。数据源可能从当天 00:00 起返回一整天 ——
    // 不过滤的话"第一格"是凌晨，正在下雨/还有多久全部错，且错得很隐蔽
    var stale = Weather.Read(Slots(t0.AddHours(-3), 9, 9, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), t0);
    Check("丢弃已过去的格子",
        !stale.RainingNow && stale.StartsIn is null,
        $"RainingNow={stale.RainingNow} StartsIn={stale.StartsIn}");

    // 正在下：第一格就湿。此时该说"还要下多久"，不是"多久后开始"
    var now = Weather.Read(Slots(t0, 0.5, 0.5, 0, 0), t0);
    Check("正在下雨时不报开始时刻",
        now.RainingNow && now.StartsIn is null && now.Duration == TimeSpan.FromMinutes(30),
        $"RainingNow={now.RainingNow} Duration={now.Duration}");

    // 中间断一格算两场。接成一场会报出虚高的时长（"持续 1 小时"其实中间停了）
    var gap = Weather.Read(Slots(t0, 0.5, 0, 0.5, 0.5), t0);
    Check("间歇断开不接成一场",
        gap.RainingNow && gap.Duration == TimeSpan.FromMinutes(15),
        $"Duration={gap.Duration}");

    // 下到窗口尾部：时长是下限，文案该说"至少"而不是给个确切的停雨时间
    var tail = Weather.Read(Slots(t0, 0, 0.5, 0.5, 0.5), t0);
    Check("窗口截断时标记为下限", tail.DurationIsFloor, $"IsFloor={tail.DurationIsFloor}");
    var (_, tailDetail) = Weather.Describe(tail);
    Check("截断文案用「至少」", tailDetail.Contains("至少"), $"文案：{tailDetail}");

    // 低概率不报。降水量够但只有 30% 概率 —— 那种该等它更确定再说
    var unsure = new[]
    {
        new PrecipSlot(t0, 0, 20),
        new PrecipSlot(t0.AddMinutes(15), 2.0, 30),
        new PrecipSlot(t0.AddMinutes(30), 2.0, 30),
    };
    var lowProb = Weather.Read(unsure, t0);
    Check("低概率不报", lowProb.StartsIn is null, $"StartsIn={lowProb.StartsIn}");

    // 毛毛雨不报。0.05mm/15min 是空气里的湿意，为它弹提醒只会让人以后忽略提醒
    var drizzle = Weather.Read(Slots(t0, 0, 0.05, 0.05), t0);
    Check("微量降水不报", drizzle.StartsIn is null, $"StartsIn={drizzle.StartsIn}");

    // 柱状图归一化到 0–1，且不能被单场暴雨顶爆
    var chart = Weather.Read(Slots(t0, 0, 1.0, 9.9), t0);
    Check("柱状图归一化在 0–1",
        chart.Bars.Length == 3 && chart.Bars.All(b => b is >= 0 and <= 1) && chart.Bars[2] == 1,
        $"Bars=[{string.Join(", ", chart.Bars.Select(b => b.ToString("F2")))}]");

    // 空输入不抛异常
    var empty = Weather.Read([], t0);
    Check("空输入不抛", empty.Bars.Length == 0 && empty.StartsIn is null, "—");

    // ===== 预警筛选 =====
    //
    // 全国一次 1300+ 条，筛错的后果分两种，都很实在：
    // 报出几百公里外的雷暴（噪音），或者本地暴雨没报（漏掉真事）。

    var local = new Region("辽宁省", ["沈阳市", "浑南区"]);
    var fresh = t0.AddMinutes(-30).ToString("yyyy/MM/dd HH:mm");

    var nation = new[]
    {
        new WeatherAlert("a1", "辽宁省大连市庄河市气象台发布冰雹橙色预警信号", fresh),
        new WeatherAlert("a2", "辽宁省沈阳市气象台发布暴雨黄色预警信号", fresh),
        new WeatherAlert("a3", "辽宁省沈阳市浑南区气象台发布雷电黄色预警信号", fresh),
        new WeatherAlert("a4", "辽宁省铁岭市铁岭县气象台发布雷雨大风黄色预警信号", fresh),
        new WeatherAlert("a5", "吉林省沈阳市气象台发布大风蓝色预警信号", fresh),   // 构造的跨省同名
        // 下辖县，距市区一百多公里 —— 真实数据里就是它把 5 条无关预警带了进来
        new WeatherAlert("a6", "辽宁省沈阳市康平县气象台发布暴雨黄色预警信号", fresh),
    };

    var matched = Weather.MatchAlerts(nation, local, t0);
    Check("只留本地两条",
        matched.Length == 2 && matched.All(a => a.Id is "a2" or "a3"),
        $"命中 [{string.Join(",", matched.Select(a => a.Id))}]");

    // 同省但异地：实测本机所在省内 13 条预警全在省内其它地级市。
    // 只匹配省名的话这些全会弹出来 —— 几百公里外的雷暴与本地无关
    Check("同省异地不报", matched.All(a => a.Id != "a1"), "大连那条被带进来了");

    // 跨省同名：不同省有同名市县，只按市名匹配必然误报
    Check("跨省同名不报", matched.All(a => a.Id != "a5"), "吉林省那条被带进来了");

    // **市辖县不算本地**。这一条是真实数据打出来的：只做包含匹配的话，
    // 「沈阳市康平县」因为含"沈阳市"而命中，而它在一百多公里外。
    // 判据得是"发布单位的最后一级"等于我所在的市/区县
    Check("市辖县不报", matched.All(a => a.Id != "a6"), "康平县那条被带进来了");

    Check("发布单位取到最后一级",
        Weather.LastAdminLevel("辽宁省沈阳市康平县气象台发布暴雨黄色预警信号") == "康平县"
        && Weather.LastAdminLevel("辽宁省沈阳市气象台发布暴雨黄色预警信号") == "沈阳市",
        "行政级别切错");

    // **过期预警不报**。nmc.cn 的列表里带着十几个小时前的条目，
    // 而去重是按 id 的 —— 不过滤则每次重启都把隔夜预警当新消息连弹一串
    var staleAlerts = new[]
    {
        new WeatherAlert("s1", "辽宁省沈阳市气象台发布暴雨黄色预警信号",
            t0.AddHours(-14).ToString("yyyy/MM/dd HH:mm")),
    };
    Check("过期预警不报", Weather.MatchAlerts(staleAlerts, local, t0).Length == 0, "隔夜的被当成新的");

    // 没有时间戳的不当作过期 —— 宁可多报一条，也别因为数据源少给一个字段就整块失灵
    var noTime = new[] { new WeatherAlert("n1", "辽宁省沈阳市气象台发布暴雨黄色预警信号", "") };
    Check("缺时间戳仍放行", Weather.MatchAlerts(noTime, local, t0).Length == 1, "被误判为过期");

    // 省级预警覆盖整省，只需省名命中
    var provincial = new[]
    {
        new WeatherAlert("p1", "辽宁省气象台发布暴雨蓝色预警", fresh),
        new WeatherAlert("p2", "陕西省气象台发布山洪灾害黄色预警", fresh),
    };
    var prov = Weather.MatchAlerts(provincial, local, t0, provinceWide: true);
    Check("省级预警按省命中", prov.Length == 1 && prov[0].Id == "p1",
        $"命中 {prov.Length} 条");

    // 标题拆分：岛上要一眼看到"下什么、多严重"，而不是发布单位
    var (kind, org) = Weather.SplitAlertTitle("辽宁省沈阳市气象台发布暴雨黄色预警信号");
    Check("标题拆成类型 + 单位",
        kind == "暴雨黄色" && org == "辽宁省沈阳市",
        $"kind=「{kind}」 org=「{org}」");

    // ---- 行政区文本的往返 ----
    //
    // 设置页把检测到的 Region 写进 config.json，产品启动时再解析回来。
    // 两个方向必须严格互逆，而它们**长得很像却不是同一个东西**：
    // ToString() 给的是 "辽宁省/沈阳市·浑南区"（给人看），
    // 配置文本必须是 "辽宁省沈阳市浑南区"（无分隔符，按后缀切）。
    // 拿 ToString() 去写配置的话，切出来第一段是 "/沈阳市"，带个斜杠去比对预警，
    // 永远匹配不上 —— 而表现只是"设置页显示填对了，却收不到任何预警"。
    var rt = new[]
    {
        new Region("辽宁省", ["沈阳市", "浑南区"]),
        new Region("辽宁省", ["沈阳市"]),
        new Region("上海市", []),
        new Region("内蒙古自治区", ["呼伦贝尔市", "鄂温克族自治旗"]),
        new Region("新疆维吾尔自治区", ["伊犁哈萨克自治州"]),
    };

    foreach (var region in rt)
    {
        var text = region.ToConfigText();
        var back = Weather.ParseRegionText(text);

        Check($"行政区往返：{region}",
            back is { } b && b.Province == region.Province && b.Locals.SequenceEqual(region.Locals),
            $"「{text}」解析回来是 {(back is null ? "null" : back.ToString())}");
    }

    // 反过来也要挡住：ToString() 的形式**不该**能解析成同一个东西 ——
    // 这条用例是为了让"以后有人图省事改用 ToString"立刻失败，而不是悄悄坏掉
    var viaToString = Weather.ParseRegionText(rt[0].ToString());
    Check("ToString 的形式不可用作配置文本",
        viaToString is null || viaToString.Value.Locals.Any(x => x.Contains('/') || x.Contains('·'))
            || viaToString.Value.Province.Contains('/'),
        $"意外地也能正确解析：{viaToString}（那说明两种表示混用不会被发现）");

    // 空与无效输入
    Check("空文本解析为 null",
        Weather.ParseRegionText(null) is null && Weather.ParseRegionText("") is null
        && Weather.ParseRegionText("没有行政后缀") is null,
        "空/无效输入没返回 null");

    // ---- 行政区层级顺序 ----
    //
    // Nominatim 给的顺序是任意的：实测 city 字段给的是区名、市名要从
    // display_name 里捞，于是拿到 [区, 市] 这种粗细颠倒的顺序。匹配时是集合、顺序无所谓，
    // 所以这个问题一直没显形 —— 直到设置页把它拼成文本给用户看，
    // "辽宁省浑南区沈阳市"读起来是一句乱码。
    var messy = GeoLocation.ParseRegion(
        """{"address":{"state":"辽宁省","city":"浑南区"},"display_name":"某街道, 浑南区, 沈阳市, 辽宁省, 中国"}""");

    Check("行政区按层级从粗到细排",
        messy is { } m2 && m2.ToConfigText() == "辽宁省沈阳市浑南区",
        $"得到「{(messy is null ? "null" : messy.Value.ToConfigText())}」");

    // 级别：红/橙压过一切，蓝/黄不必
    Check("红橙判为严重",
        Weather.IsSevere("发布暴雨红色预警信号")
        && Weather.IsSevere("发布冰雹橙色预警信号")
        && !Weather.IsSevere("发布大风蓝色预警信号"),
        "级别判断错");

    // ---- 柱状图高亮区间 ----
    //
    // 这一组是**事后补的**：HighlightRange 原先是 WeatherProvider 的 private 方法，
    // 隐含前提"只在正在下或即将下时调用"（播报路径确实如此），里面有个
    // StartsIn!.Value。加了"主动查看"之后不下雨也要给答案，那个 ! 就炸了 ——
    // 抛在 UI 线程上表现为**整个岛体卡死**，而且只在天晴时复现。
    //
    // 搬到 Weather 里就是为了让这几条能喂进来。教训：
    // **一个函数的隐含前提，会被新调用方悄悄打破，而编译器不会说话。**

    var drySlots = new[]
    {
        new PrecipSlot(t0, 0, 0),
        new PrecipSlot(t0.AddMinutes(15), 0, 5),
        new PrecipSlot(t0.AddMinutes(30), 0, 10),
        new PrecipSlot(t0.AddMinutes(45), 0, 0),
    };

    var dryOutlook = Weather.Read(drySlots, t0);
    Check("不下雨时 Outlook 没有开始时刻",
        dryOutlook is { RainingNow: false, StartsIn: null },
        $"RainingNow={dryOutlook.RainingNow} StartsIn={dryOutlook.StartsIn}");

    // 这一条就是那个卡死的直接判据 —— 抛异常的话 Check 都走不到
    var dryRange = (From: -2, To: -2);
    var threw = false;
    try { dryRange = Weather.HighlightRange(dryOutlook); }
    catch (Exception ex) { threw = true; Console.WriteLine($"    抛了：{ex.GetType().Name}"); }

    Check("不下雨时求高亮区间不抛", !threw, threw ? "抛异常了" : "没抛");
    Check("不下雨时没有高亮区间", dryRange is { From: -1, To: -1 },
        $"得到 {dryRange.From}-{dryRange.To}");

    // 正在下：从第 0 格开始
    var nowSlots = new[]
    {
        new PrecipSlot(t0, 0.4, 80),
        new PrecipSlot(t0.AddMinutes(15), 0.3, 70),
        new PrecipSlot(t0.AddMinutes(30), 0, 10),
        new PrecipSlot(t0.AddMinutes(45), 0, 0),
    };

    var nowRange = Weather.HighlightRange(Weather.Read(nowSlots, t0));
    Check("正在下雨时从第 0 格起高亮", nowRange.From == 0 && nowRange.To == 1,
        $"得到 {nowRange.From}-{nowRange.To}");

    // 稍后下：高亮从那一格开始，且不越界
    var laterSlots = new[]
    {
        new PrecipSlot(t0, 0, 0),
        new PrecipSlot(t0.AddMinutes(15), 0, 10),
        new PrecipSlot(t0.AddMinutes(30), 0.5, 80),
        new PrecipSlot(t0.AddMinutes(45), 0.5, 80),
    };

    var laterOutlook = Weather.Read(laterSlots, t0);
    var laterRange = Weather.HighlightRange(laterOutlook);
    Check("稍后下雨时高亮落在那几格",
        laterRange.From == 2 && laterRange.To == 3,
        $"得到 {laterRange.From}-{laterRange.To}");

    Check("高亮区间不越出柱子范围",
        laterRange.To < laterOutlook.Bars.Length && laterRange.From >= 0,
        $"To={laterRange.To} 柱数={laterOutlook.Bars.Length}");

    // 空预报：也不该抛
    var emptyThrew = false;
    try { Weather.HighlightRange(new RainOutlook()); }
    catch { emptyThrew = true; }
    Check("空 Outlook 求高亮不抛", !emptyThrew, emptyThrew ? "抛了" : "没抛");

    Console.WriteLine($"  {pass} 项通过，{fail} 项失败");
    return fail == 0;
}

// ===== 歌词翻译自检 =====
//
// 和天气那组是同一个道理：这里的每种错法在岛上都长成同一个样子 ——
// 第二行不出现、或者出现的是隔壁那句。都看不出是解析、配对、还是模式映射错了。
//
// 覆盖的是**配对**与**模式映射**两段纯逻辑，不碰网络：
// 语言判断压根不在我们这边（数据源只给外语歌 tlyric，中文歌那个字段是空的）。
static bool LyricSelfTest()
{
    Console.WriteLine("=== 歌词翻译自检（Lyrics.Parse / Present，不联网）===");

    var pass = 0;
    var fail = 0;

    void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine($"  ✓ {name}"); }
        else { fail++; Console.WriteLine($"  ✗ {name} —— {detail}"); }
    }

    const string english = """
        [00:07.410]Hey there Delilah
        [00:11.220]What's it like in New York City
        [00:15.030]I'm a thousand miles away
        """;

    // ---- 1. 中文歌：tlyric 是空串，不是缺字段。全行都该没有译文 ----
    // 错了的表现：中文歌莫名其妙冒出第二行（多半是把原文自己复制了一遍）
    var chinese = Lyrics.Parse("[00:01.000]故事的小黄花\n[00:05.000]从出生那年就飘着", "");
    Check("中文歌无译文",
        chinese.Length == 2 && chinese.All(l => l.Translation is null),
        $"{chinese.Length} 行，译文 = {string.Join("/", chinese.Select(l => l.Translation ?? "null"))}");

    // ---- 2. 时间戳完全一致（实测网易云就是这样）----
    var exact = Lyrics.Parse(english, """
        [00:07.410]嘿 迪丽拉
        [00:11.220]纽约那边怎么样
        [00:15.030]我在千里之外
        """);
    Check("时间戳一致时逐行配上",
        exact.Length == 3 && exact[0].Translation == "嘿 迪丽拉" && exact[2].Translation == "我在千里之外",
        string.Join(" | ", exact.Select(l => $"{l.Time.TotalSeconds:F2}→{l.Translation ?? "null"}")));

    // ---- 3. 译文差 200ms：投稿者不同，时间戳未必分毫不差 ----
    // 收窄成"必须相等"的话，这种歌整首一行都配不上，而那与"这首没翻译"无从区分
    var near = Lyrics.Parse(english, """
        [00:07.610]嘿 迪丽拉
        [00:11.020]纽约那边怎么样
        [00:15.230]我在千里之外
        """);
    Check("±200ms 仍算同一句",
        near.All(l => l.Translation is not null),
        string.Join(" | ", near.Select(l => l.Translation ?? "null")));

    // ---- 4. 差 900ms：超出容差就该判为没有，而不是硬配 ----
    // 硬配的后果比不配严重得多：整首歌的译文错开一句，用户看到的是驴唇不对马嘴
    var far = Lyrics.Parse("[00:07.410]Hey there Delilah", "[00:08.310]纽约那边怎么样");
    Check("超容差不硬配", far[0].Translation is null, $"配到了「{far[0].Translation}」");

    // ---- 5. 占位符 - ：那一句不需要翻译（"Oh oh oh" 之类）----
    // 不滤掉的话第二行会显示一个孤零零的减号
    var dash = Lyrics.Parse("[00:07.410]Oh oh oh\n[00:11.220]What's it like", "[00:07.410]-\n[00:11.220]那边怎么样");
    Check("- 占位符不显示",
        dash[0].Translation is null && dash[1].Translation == "那边怎么样",
        $"[0]=「{dash[0].Translation ?? "null"}」 [1]=「{dash[1].Translation ?? "null"}」");

    // ---- 6. 译文只覆盖部分句子 ----
    // 有译文的配上、没有的留空，且**不能串到邻句去**
    var partial = Lyrics.Parse(english, "[00:15.030]我在千里之外");
    Check("部分句有译文时不串行",
        partial[0].Translation is null && partial[1].Translation is null
        && partial[2].Translation == "我在千里之外",
        string.Join(" | ", partial.Select(l => l.Translation ?? "null")));

    // ---- 7. 副歌复用：一行挂多个时间戳，每个都要各自配自己的译文 ----
    // 只按第一个时间戳配的话，副歌第二次出现时第二行是空的
    var chorus = Lyrics.Parse(
        "[00:20.000][01:20.000]Hey there Delilah",
        "[00:20.000]嘿 迪丽拉\n[01:20.000]嘿 迪丽拉（重复）");
    Check("副歌两个时间戳各配各的",
        chorus.Length == 2
        && chorus[0].Translation == "嘿 迪丽拉"
        && chorus[1].Translation == "嘿 迪丽拉（重复）",
        string.Join(" | ", chorus.Select(l => $"{l.Time.TotalSeconds:F0}s→{l.Translation ?? "null"}")));

    // ---- 8. 三种显示模式 ----
    var withT = new LyricLine(TimeSpan.Zero, "Hey there Delilah", "嘿 迪丽拉");
    var noT = new LyricLine(TimeSpan.Zero, "故事的小黄花");

    var both = Lyrics.Present(withT, LyricTranslationMode.Both);
    Check("Both = 原文 + 译文",
        both.Main == "Hey there Delilah" && both.Sub == "嘿 迪丽拉",
        $"Main=「{both.Main}」 Sub=「{both.Sub}」");

    var onlyT = Lyrics.Present(withT, LyricTranslationMode.TranslationOnly);
    Check("TranslationOnly = 只有译文，且是单行",
        onlyT.Main == "嘿 迪丽拉" && onlyT.Sub is null,
        $"Main=「{onlyT.Main}」 Sub=「{onlyT.Sub}」");

    var onlyO = Lyrics.Present(withT, LyricTranslationMode.OriginalOnly);
    Check("OriginalOnly = 只有原文",
        onlyO.Main == "Hey there Delilah" && onlyO.Sub is null,
        $"Main=「{onlyO.Main}」 Sub=「{onlyO.Sub}」");

    // 最要紧的一条：选了「只显示译文」的用户去听中文歌，不能整个空掉。
    // 空掉在岛上和"前奏没唱到"、"歌词没拉到"一模一样，用户只会觉得歌词坏了
    var fallback = Lyrics.Present(noT, LyricTranslationMode.TranslationOnly);
    Check("只显示译文遇上中文歌 → 回落原文而非空白",
        fallback.Main == "故事的小黄花" && fallback.Sub is null,
        $"Main=「{fallback.Main ?? "null"}」");

    // 双行模式下没有译文就自然退化成单行，不该冒出一个空的第二行
    var degrade = Lyrics.Present(noT, LyricTranslationMode.Both);
    Check("双行模式无译文时退化为单行",
        degrade.Main == "故事的小黄花" && degrade.Sub is null,
        $"Sub=「{degrade.Sub}」");

    // 前奏期间没有当前行，三种模式都该给两个 null
    Check("没有当前行时两行都空",
        Lyrics.Present(null, LyricTranslationMode.Both) is (null, null)
        && Lyrics.Present(null, LyricTranslationMode.TranslationOnly) is (null, null),
        "前奏期间返回了非空");

    // ---- 繁简：Spotify 的元数据是原始曲库的形状 ----
    //
    // 实测：Spotify 给的是「我還年輕 我還年輕 / 老王樂隊」，而网易云索引的是简体，
    // 拿繁体串去搜**一条结果都没有**。这一组盯的就是那条转换与它的边界。

    Check("繁体转简体", ChineseText.ToSimplified("我還年輕 我還年輕") == "我还年轻 我还年轻",
        $"得到「{ChineseText.ToSimplified("我還年輕 我還年輕")}」");

    Check("艺术家也转", ChineseText.ToSimplified("老王樂隊") == "老王乐队",
        $"得到「{ChineseText.ToSimplified("老王樂隊")}」");

    Check("已经是简体的不变", ChineseText.ToSimplified("我还年轻") == "我还年轻", "");
    Check("英文原样返回", ChineseText.ToSimplified("Bohemian Rhapsody") == "Bohemian Rhapsody", "");
    Check("空串不抛", ChineseText.ToSimplified(null) == "" && ChineseText.ToSimplified("") == "", "");

    Check("英文不含汉字", !ChineseText.HasHan("Shape of You"), "");
    Check("中文含汉字", ChineseText.HasHan("山海"), "");

    // 这一条**记录的是限制，不是期望的行为**：LCMapStringEx 逐字映射，
    // 日文汉字一样会被改。它就是"不能一上来就转、只能当兜底重试"的依据 ——
    // 哪天这条断言变红了，说明转换行为变了，那套顺序也得重新想
    Check("⚠ 日文汉字也会被改（所以只能当兜底）",
        ChineseText.ToSimplified("月が綺麗ね") == "月が绮丽ね",
        $"得到「{ChineseText.ToSimplified("月が綺麗ね")}」");

    // ---- 查询变体阶梯 ----
    var v1 = LyricsService.QueryVariants("我還年輕 我還年輕", "老王樂隊").ToArray();
    Check("繁体给两级变体", v1.Length == 2, $"得到 {v1.Length} 级");
    Check("第一级永远是原样", v1[0] == ("我還年輕 我還年輕", "老王樂隊"), $"得到 {v1[0]}");
    Check("第二级是简体", v1.Length > 1 && v1[1] == ("我还年轻 我还年轻", "老王乐队"),
        v1.Length > 1 ? $"得到 {v1[1]}" : "只有一级");

    var v2 = LyricsService.QueryVariants("我还年轻", "老王乐队").ToArray();
    Check("简体只给一级（不白发请求）", v2.Length == 1, $"得到 {v2.Length} 级");

    var v3 = LyricsService.QueryVariants("Shape of You", "Ed Sheeran").ToArray();
    Check("英文只给一级", v3.Length == 1, $"得到 {v3.Length} 级");

    // 山海是两边同形的，但艺术家不同形 —— 这时也该给第二级
    var v4 = LyricsService.QueryVariants("山海", "草東沒有派對").ToArray();
    Check("只有艺术家是繁体时也给第二级", v4.Length == 2, $"得到 {v4.Length} 级");

    Console.WriteLine($"  {pass} 项通过，{fail} 项失败");
    return fail == 0;
}

// ===== 媒体会话选取自检 =====
//
// 挑错会话的后果是"进度条和歌词忽有忽无"，在界面上就是个玄学现象 ——
// 而且它只在同一播放器挂两个会话时才发生，那个环境不好造。
// 所以判据做成了不碰 WinRT 的纯函数，这里直接喂特征。
static bool SessionSelfTest()
{
    Console.WriteLine("=== 会话选取自检（MediaSessionPick，不碰 WinRT）===");

    var pass = 0;
    var fail = 0;

    void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine($"  ✓ {name}"); }
        else { fail++; Console.WriteLine($"  ✗ {name} —— {detail}"); }
    }

    // 造会话：(下标, AppId, 有timeline, 有Genre, 有标题)
    static MediaSessionInfo S(int i, string app, bool tl, bool genre = false, bool title = true)
        => new(i, app, tl, genre, title);

    // ---- 1. 一个会话：原样接管 ----
    // 错了就是最常见的情形反而挑不出来
    Check("单会话直接接管",
        MediaSessionPick.Choose([S(0, "cloudmusic.exe", true)], 0) == 0,
        "单会话都挑错了");

    // ---- 2. 空列表 ----
    Check("空列表返回 -1", MediaSessionPick.Choose([], -1) == -1, "没有返回 -1");

    // ---- 3. 核心场景：网易云原生 + InfLink 并存 ----
    // 系统把"当前"给了残缺的那个（谁最近更新就给谁），我们要换到有 timeline 的。
    // 不换的话进度条和歌词整个失效，而且是间歇性的 —— 最难查的那种
    var ncm = new[]
    {
        S(0, "cloudmusic.exe", tl: false),                    // 原生：没位置、没 ID
        S(1, "cloudmusic.exe", tl: true, genre: true),        // InfLink：两样都有
    };
    Check("同源双会话挑信息量大的",
        MediaSessionPick.Choose(ncm, 0) == 1,
        $"挑了 {MediaSessionPick.Choose(ncm, 0)}，应为 1");

    // 反过来系统已经给对了，也不能挑回去
    Check("系统已给对时不乱换",
        MediaSessionPick.Choose(ncm, 1) == 1,
        $"挑了 {MediaSessionPick.Choose(ncm, 1)}");

    // ---- 4. 绝不跨应用 ----
    // "哪个应用是当前的"是用户意图，系统比我们清楚。
    // 越界的后果很难受：在浏览器放视频时，岛体跳去显示暂停着的音乐软件
    var mixed = new[]
    {
        S(0, "chrome.exe", tl: false),                        // 当前，但信息少
        S(1, "cloudmusic.exe", tl: true, genre: true),        // 别的应用，信息多
    };
    Check("不跨应用改选",
        MediaSessionPick.Choose(mixed, 0) == 0,
        $"挑了 {MediaSessionPick.Choose(mixed, 0)}，跨到别的应用去了");

    // ---- 5. 系统没给当前会话 ----
    // 这时没有"用户意图"可尊重了，只能自己挑分最高的。挑不出来就整个不显示
    Check("无当前会话时全局挑最好",
        MediaSessionPick.Choose(mixed, -1) == 1,
        $"挑了 {MediaSessionPick.Choose(mixed, -1)}");

    // ---- 6. 平分时留在原处 ----
    // 严格大于才换。相等就换的话，两个等价会话之间每次事件都来回跳，
    // 每跳一次都要退订/重订 + 刷新一遍
    var tie = new[]
    {
        S(0, "app.exe", tl: true, genre: true),
        S(1, "app.exe", tl: true, genre: true),
    };
    Check("平分不换", MediaSessionPick.Choose(tie, 1) == 1,
        $"挑了 {MediaSessionPick.Choose(tie, 1)}，应留在 1");

    // ---- 7. timeline 压过一切 ----
    // 有 Genre 没位置的会话对歌词毫无用处：不知道唱到哪，一句都显示不出来
    var tlWins = new[]
    {
        S(0, "app.exe", tl: false, genre: true),
        S(1, "app.exe", tl: true, genre: false),
    };
    Check("timeline 权重压过 Genre",
        MediaSessionPick.Choose(tlWins, 0) == 1,
        $"挑了 {MediaSessionPick.Choose(tlWins, 0)}");

    // ---- 8. 三会话，同源两个 + 外来一个 ----
    var three = new[]
    {
        S(0, "chrome.exe", tl: true, genre: true, title: true),   // 分最高，但不同源
        S(1, "cloudmusic.exe", tl: false),                        // 当前
        S(2, "cloudmusic.exe", tl: true, genre: true),            // 同源且更好
    };
    Check("三会话时只在同源里挑",
        MediaSessionPick.Choose(three, 1) == 2,
        $"挑了 {MediaSessionPick.Choose(three, 1)}，应为 2");

    // ---- 9. 全空会话不该被挑中 ----
    // 有些播放器会注册一个什么都没有的占位会话
    var empty = new[]
    {
        S(0, "app.exe", tl: false, genre: false, title: false),
        S(1, "app.exe", tl: false, genre: false, title: true),
    };
    Check("有标题的压过全空的",
        MediaSessionPick.Choose(empty, 0) == 1,
        $"挑了 {MediaSessionPick.Choose(empty, 0)}");

    // ---- 10. currentIndex 越界不能崩 ----
    // 上游按 AppId 找不到 current 时会传 -1，但防一手传错的下标
    Check("越界下标退化为全局挑",
        MediaSessionPick.Choose(ncm, 99) == 1,
        $"挑了 {MediaSessionPick.Choose(ncm, 99)}");

    // ---- 11. AppId 大小写不敏感 ----
    // SMTC 的 AppUserModelId 大小写不保证稳定
    var caseMix = new[]
    {
        S(0, "CloudMusic.exe", tl: false),
        S(1, "cloudmusic.exe", tl: true, genre: true),
    };
    Check("AppId 比较不分大小写",
        MediaSessionPick.Choose(caseMix, 0) == 1,
        $"挑了 {MediaSessionPick.Choose(caseMix, 0)}，大小写把同源判成了异源");

    Console.WriteLine($"  {pass} 项通过，{fail} 项失败");
    return fail == 0;
}

// ===== Phira 成绩自检 =====
//
// 这一组的每种错法在界面上分别是"什么都没发生"和"莫名弹了一屏"，
// 两种都不是事后能归因的现象 —— 尤其后者，弹出来了撤不回，只能等它一条条走完。
static bool PhiraSelfTest()
{
    Console.WriteLine("=== Phira 成绩自检（PhiraRecords，不读真存档）===");

    var pass = 0;
    var fail = 0;

    void Check(string name, bool ok, string detail)
    {
        if (ok) { pass++; Console.WriteLine($"  ✓ {name}"); }
        else { fail++; Console.WriteLine($"  ✗ {name} —— {detail}"); }
    }

    // 造一份存档。**故意带上 me / tokens** —— 解析器绝不能碰它们。
    // 不用原始字符串字面量：JSON 里全是花括号，和插值语法互相打架，
    // 嵌一层就编译不过（试过）。老老实实拼接反而清楚。
    static string Save(params string[] charts) =>
        "{ \"me\": { \"id\": 1, \"name\": \"someone\", \"email\": \"secret@example.com\" },"
        + " \"tokens\": [\"ACCESS_SECRET\", \"REFRESH_SECRET\"],"
        + " \"config\": { \"aggressive\": true },"
        + " \"charts\": [" + string.Join(",", charts) + "] }";

    static string Chart(string path, string name, string level,
        int? score = null, double acc = 0, bool fc = false)
    {
        var head = "{ \"local_path\": \"" + path + "\", \"name\": \"" + name
            + "\", \"level\": \"" + level + "\", ";

        if (score is null) return head + "\"record\": null }";

        return head + "\"record\": { \"score\": " + score
            + ", \"accuracy\": " + acc.ToString(CultureInfo.InvariantCulture)
            + ", \"fullCombo\": " + (fc ? "true" : "false") + " } }";
    }

    // ---- 1. 只解析 charts，不碰凭据 ----
    // 这一条不是功能正确性，是**隐私边界**。解析器多读一个字段，
    // 那个字段就可能出现在探针、日志或活动标题里
    var withSecrets = PhiraRecords.Read(Save(Chart("a", "曲一", "IN Lv.14", 900000, 0.95)));
    Check("只取 charts[]，凭据字段不进结果",
        withSecrets.Count == 1
        && !withSecrets.Values.Any(r =>
            r.Name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || r.Key.Contains("SECRET", StringComparison.Ordinal)),
        "解析结果里混进了 charts 以外的东西");

    // ---- 2. 没打过的谱面不算成绩 ----
    var mixed = PhiraRecords.Read(Save(
        Chart("a", "曲一", "IN Lv.14", 900000, 0.95),
        Chart("b", "曲二", "AT Lv.16")));
    Check("record 为 null 的不计入", mixed.Count == 1 && mixed.ContainsKey("a"),
        $"取到 {mixed.Count} 条");

    // ---- 3. 启动基线：一样的存档 diff 不出东西 ----
    // 错了就是每次存档一写、把全部历史成绩重弹一遍
    Check("相同快照无变化", PhiraRecords.Diff(mixed, mixed).Length == 0, "凭空报出了变化");

    // ---- 4. 第一次打某首 ----
    var after = PhiraRecords.Read(Save(
        Chart("a", "曲一", "IN Lv.14", 900000, 0.95),
        Chart("b", "曲二", "AT Lv.16", 800000, 0.88)));
    var first = PhiraRecords.Diff(mixed, after);
    Check("首次游玩报一条", first.Length == 1 && first[0].Key == "b", $"报了 {first.Length} 条");

    // ---- 5. 刷新分数 ----
    var better = PhiraRecords.Read(Save(Chart("a", "曲一", "IN Lv.14", 960000, 0.98)));
    Check("分数提高要报", PhiraRecords.Diff(mixed, better).Length == 1, "没报");

    // ---- 6. 分数变低不报 ----
    // Phira 自己只在更优时才写，但存档可能被还原或替换 —— 那不是一次游玩
    var worse = PhiraRecords.Read(Save(Chart("a", "曲一", "IN Lv.14", 500000, 0.60)));
    Check("分数变低不报", PhiraRecords.Diff(mixed, worse).Length == 0, "把回退当成了刷新");

    // ---- 7. 分数没变但拿到了 FC ----
    // 三个量各自独立提高，只看分数会漏掉"同分但 FC 了"这种真实进步
    var gotFc = PhiraRecords.Read(Save(Chart("a", "曲一", "IN Lv.14", 900000, 0.95, fc: true)));
    Check("同分拿到 FC 也要报", PhiraRecords.Diff(mixed, gotFc).Length == 1, "漏了 FC");

    // ---- 8. 一次变太多不报 ----
    // 打一局只会改一首。一次变十几首是导入谱面 / 换账号 / 恢复存档，
    // 那时连弹十几条比不弹更糟 —— 而且撤不回来
    var many = PhiraRecords.Read(Save(
        Chart("a", "曲一", "IN Lv.14", 990000, 0.99),
        Chart("b", "曲二", "AT Lv.16", 990000, 0.99),
        Chart("c", "曲三", "IN Lv.12", 990000, 0.99),
        Chart("d", "曲四", "IN Lv.13", 990000, 0.99)));
    var burst = PhiraRecords.Diff(mixed, many);
    Check($"一次变 4 首（>{PhiraRecords.MaxBurst}）一条都不报", burst.Length == 0,
        $"报了 {burst.Length} 条");

    // ---- 9. 评级阈值照抄 Phira 自己的 icon_index ----
    // 自己发明一套评级会和游戏结算画面对不上，而那比不显示评级更糟
    (int Score, bool Fc, string Rank)[] ranks =
    [
        (699999, false, "F"), (700000, false, "C"), (819999, false, "C"),
        (820000, false, "B"), (879999, false, "B"),
        (880000, false, "A"), (919999, false, "A"),
        (920000, false, "S"), (959999, false, "S"),
        (960000, false, "V"), (999999, false, "V"),
        (960000, true, "FC"), (999999, true, "FC"),
        (1000000, true, "φ"), (1000000, false, "φ"),
    ];

    var bad = ranks.Where(x => PhiraRecords.Rank(x.Score, x.Fc) != x.Rank).ToArray();
    Check("评级阈值与 prpr/judge.rs::icon_index 一致", bad.Length == 0,
        string.Join(", ", bad.Select(x => $"{x.Score}/{x.Fc}→{PhiraRecords.Rank(x.Score, x.Fc)}")));

    // ---- 10. 副标题 ----
    var line = PhiraRecords.Describe(new PhiraRecord("a", "曲一", "IN Lv.14", 886636, 0.9463, false));
    // 886636 落在 [880000, 920000) → A。第一版这里写的是 V，被自检抓了出来 ——
    // V 是 ≥960000 才有的。样本写错和代码写错在这里长得一模一样，
    // 所以阈值那一条（上面第 9 项）必须独立于这一条存在
    Check("副标题格式", line == "IN Lv.14 · 886,636 · 94.63% · A", $"得到「{line}」");

    // ---- 10b. 难度标签是上传者手打的自由文本 ----
    // 真存档里就有 "IN  Lv.14"（两个空格）。它原样进副标题，多出来的空隙在岛上看得见
    var messy = new PhiraRecord("k", "曲", "  IN   Lv.14 ", 886636, 0.9463, false);
    Check("难度标签压掉多余空白",
        PhiraRecords.Describe(messy) == "IN Lv.14 · 886,636 · 94.63% · A",
        $"得到「{PhiraRecords.Describe(messy)}」");

    // 但不改写内容 —— 真存档里还有 "LV.13"、"HD"、"BT+CBD Lv.?"，
    // 把它们"规范"成 IN Lv.13 就是在猜，而猜错的难度标签比原样照搬更糟
    var odd = new PhiraRecord("k", "曲", "BT+CBD Lv.?", 990550, 0.9895, true);
    Check("怪难度标签原样保留",
        PhiraRecords.Describe(odd) == "BT+CBD Lv.? · 990,550 · 98.95% · FC",
        $"得到「{PhiraRecords.Describe(odd)}」");

    // ---- 11. 畸形 JSON 不该整个失灵 ----
    // 存档里混进一条坏数据，不该让整个功能哑掉
    var broken = PhiraRecords.Read(
        "{ \"charts\": [ {\"local_path\":\"a\"}, \"不是对象\","
        + " {\"local_path\":\"b\",\"name\":\"好的\",\"level\":\"IN\","
        + "\"record\":{\"score\":900000,\"accuracy\":0.9,\"fullCombo\":false}} ] }");
    Check("跳过坏条目、保留好条目", broken.Count == 1 && broken.ContainsKey("b"),
        $"取到 {broken.Count} 条");

    // ---- 12. 没有 charts 字段 ----
    Check("缺 charts 返回空而不是抛",
        PhiraRecords.Read("{\"me\":{\"id\":1}}").Count == 0, "没有优雅退化");

    Console.WriteLine($"  {pass} 项通过，{fail} 项失败");
    return fail == 0;
}

// WMO 天气码，只译出与降水相关的那些 —— 其余对"要不要提醒下雨"没有意义
static string Describe(int code) => code switch
{
    0 => "晴",
    1 or 2 or 3 => "多云",
    45 or 48 => "雾",
    51 or 53 or 55 => "毛毛雨",
    56 or 57 => "冻毛毛雨",
    61 => "小雨",
    63 => "中雨",
    65 => "大雨",
    66 or 67 => "冻雨",
    71 or 73 or 75 => "雪",
    77 => "雪粒",
    80 or 81 or 82 => "阵雨",
    85 or 86 => "阵雪",
    95 => "雷暴",
    96 or 99 => "雷暴伴冰雹",
    _ => $"未知({code})",
};
