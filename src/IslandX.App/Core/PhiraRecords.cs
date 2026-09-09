using System.Text.Json;

namespace IslandX.Core;

/// <summary>一首本地谱面的成绩快照。</summary>
/// <param name="Key">稳定标识，用 <c>local_path</c>（真存档里形如 "download/66612"，
/// 本地导入的在 charts/custom 下）—— 它对下载谱和自制谱都在，而且天然唯一。</param>
/// <param name="Name">曲名。</param>
/// <param name="Level">难度标签，如 "IN Lv.14"。</param>
/// <param name="Score">分数，0–1000000。</param>
/// <param name="Accuracy">准确率，0–1。</param>
/// <param name="FullCombo">是否 Full Combo。</param>
public readonly record struct PhiraRecord(
    string Key,
    string Name,
    string Level,
    int Score,
    double Accuracy,
    bool FullCombo);

/// <summary>
/// 读 Phira 的本地存档 <c>data/data.json</c>，找出"刚刷新的成绩"。
///
/// **单方面联动**：Phira 那边不做任何改动。查证过它没有 SMTC、没有 Discord RPC、
/// 没有本地端口、窗口标题恒为 "Phira"，唯一会随游玩变化的就是这个存档文件。
///
/// ⚠ 存档里还有 <c>me.email</c> 与 <c>tokens</c>。这里**只解析 charts[]**，
/// 其余字段一律不读、不留、不外发 —— 见 <see cref="Read"/>。
///
/// 形状是拿**真存档**验过的（探针 --phiraread，27 条成绩全部解析出来）：
/// <c>charts</c> 是数组，每项有 <c>local_path</c> / <c>name</c> / <c>level</c>
/// 和可为 null 的 <c>record</c>{<c>score</c>, <c>accuracy</c>, <c>fullCombo</c>}。
/// 存档里另有一个 <c>local_records</c>，本机是空的、形状未经证实，故不碰 ——
/// 照猜的形状去读，读错了的表现是静默无反应，事后无从归因。
///
/// 纯函数 + 快照对比，因此可以直接喂样本断言：
/// "该报没报"和"多报了"在界面上分别是"什么都没发生"和"莫名弹了一堆"，
/// 都不是事后能归因的现象。
/// </summary>
public static class PhiraRecords
{
    /// <summary>
    /// 一次写入里最多认几条成绩。
    ///
    /// 打一局只会改一首。一次变了好几首说明不是游玩 —— 多半是导入谱面、
    /// 换账号或者恢复存档。那种情况下连弹十几条通知比不弹更糟。
    /// </summary>
    public const int MaxBurst = 3;

    /// <summary>
    /// 解析存档，取出所有**已有成绩**的谱面。没有成绩的（刚下载没打过）不算。
    ///
    /// 任何一条读失败就跳过那一条，不整个放弃 —— 存档里混进一条畸形数据，
    /// 不该让整个功能失灵。文件被写到一半时 JSON 解析会抛，由调用方重试。
    /// </summary>
    public static Dictionary<string, PhiraRecord> Read(string json)
    {
        var result = new Dictionary<string, PhiraRecord>();

        using var doc = JsonDocument.Parse(json);

        // 只取 charts。me / tokens / config 一概不碰
        if (!doc.RootElement.TryGetProperty("charts", out var charts)
            || charts.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var chart in charts.EnumerateArray())
        {
            if (chart.ValueKind != JsonValueKind.Object) continue;

            if (!chart.TryGetProperty("record", out var record)
                || record.ValueKind != JsonValueKind.Object)
            {
                continue;   // 还没打过
            }

            var key = Str(chart, "local_path");
            if (string.IsNullOrEmpty(key)) continue;

            result[key] = new PhiraRecord(
                key,
                Str(chart, "name"),
                Str(chart, "level"),
                Int(record, "score"),
                Num(record, "accuracy"),
                Bool(record, "fullCombo"));
        }

        return result;
    }

    /// <summary>
    /// 找出从 <paramref name="before"/> 到 <paramref name="after"/> 新增或提高的成绩。
    ///
    /// 只认"变好"：Phira 的 <c>SimpleRecord::update</c> 本身就只在更优时才写，
    /// 但存档可能被还原或替换，那时分数会变低 —— 那不是一次游玩，不该报。
    /// </summary>
    public static PhiraRecord[] Diff(
        IReadOnlyDictionary<string, PhiraRecord> before,
        IReadOnlyDictionary<string, PhiraRecord> after)
    {
        var changed = new List<PhiraRecord>();

        foreach (var (key, now) in after)
        {
            if (!before.TryGetValue(key, out var was))
            {
                changed.Add(now);   // 这首第一次打
                continue;
            }

            if (IsBetter(was, now)) changed.Add(now);
        }

        // 一次变了太多不是游玩。宁可一条不报，也不要连弹十几条 ——
        // 报错的那种"多"没法撤回，用户只能等它们一条条走完
        if (changed.Count > MaxBurst) return [];

        // 分高的排前面：真有两条同时进来时，先让更值得看的那条占主位
        changed.Sort((a, b) => b.Score.CompareTo(a.Score));
        return [.. changed];
    }

    private static bool IsBetter(PhiraRecord was, PhiraRecord now)
        => now.Score > was.Score
        || now.Accuracy > was.Accuracy + 1e-6
        || (now.FullCombo && !was.FullCombo);

    /// <summary>
    /// 评级。阈值与图标名都照抄 Phira 自己的 <c>prpr/src/judge.rs::icon_index</c>
    /// 与 <c>resource.rs</c> 里的 <c>rank/*.png</c> 顺序 ——
    /// 自己发明一套评级标准会和游戏结算画面对不上，而那比不显示评级更糟。
    /// </summary>
    public static string Rank(int score, bool fullCombo) => (score, fullCombo) switch
    {
        ( < 700000, _) => "F",
        ( < 820000, _) => "C",
        ( < 880000, _) => "B",
        ( < 920000, _) => "A",
        ( < 960000, _) => "S",
        (1000000, _) => "φ",
        (_, false) => "V",
        (_, true) => "FC",
    };

    /// <summary>岛体副标题：「IN Lv.14 · 886636 · 94.63% · V」。</summary>
    public static string Describe(PhiraRecord record)
    {
        var parts = new List<string>(4);

        var level = Tidy(record.Level);
        if (level.Length > 0) parts.Add(level);

        parts.Add(record.Score.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        parts.Add((record.Accuracy * 100).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "%");
        parts.Add(Rank(record.Score, record.FullCombo));

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// 收拾难度标签。这串是**上传者手打的自由文本**，真存档里见过
    /// "IN  Lv.14"（两个空格）、"BT+CBD Lv.?"、"HD"（没有 Lv）、"LV.13"。
    ///
    /// 只压掉多余空白，不改写内容 —— 把 "LV.13" 规范成 "IN Lv.13" 就是在猜，
    /// 而猜错的难度标签比原样照搬更糟。
    /// </summary>
    private static string Tidy(string? level)
        => string.IsNullOrWhiteSpace(level)
            ? ""
            : string.Join(' ', level.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // ===== JSON 小工具。取不到就给缺省值，不抛 =====

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? ""
            : "";

    private static int Int(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var n) ? n : 0;

    private static double Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            && v.TryGetDouble(out var n) ? n : 0;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
