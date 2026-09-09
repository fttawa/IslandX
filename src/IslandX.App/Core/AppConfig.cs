using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IslandX.Core;

/// <summary>外语歌词的翻译显示方式。</summary>
public enum LyricTranslationMode
{
    /// <summary>原文 + 译文双行。</summary>
    Both,

    /// <summary>只显示译文。没有译文时回落到原文 —— 否则中文歌会整个空掉。</summary>
    TranslationOnly,

    /// <summary>只显示原文，不显示译文。</summary>
    OriginalOnly,
}

/// <summary>
/// 用户配置，存在 %APPDATA%\IslandX\config.json。
///
/// 读失败一律回落到默认值而不是报错退出 —— 配置文件损坏不该让岛体起不来。
/// 写失败也只是静默放弃：下次启动回到默认，比弹一个用户无从处理的错误框好。
/// </summary>
public sealed class AppConfig
{
    /// <summary>各 Provider 的开关。键是 Provider 的 Id，缺省视为启用。</summary>
    public Dictionary<string, bool> Providers { get; set; } = [];

    /// <summary>频谱随音乐律动。</summary>
    public bool PulseEnabled { get; set; } = true;

    /// <summary>
    /// 收起态显示歌词。**默认关闭**：它需要联网，会把当前播放的曲目名
    /// 发到 music.163.com 去换歌词。这属于用户该主动同意的事，不该默认替他决定。
    /// </summary>
    public bool LyricsEnabled { get; set; }

    /// <summary>
    /// 降水提醒与气象预警。**默认关闭**：它要用系统定位，并把坐标发到天气服务商。
    /// 位置比曲目名敏感得多，更该由用户主动点头。
    /// </summary>
    public bool WeatherEnabled { get; set; }

    /// <summary>
    /// 手填的坐标，填了就**不再调用系统定位**。
    /// 用于两种情况：系统定位权限拿不到，或者不愿意让程序读真实位置而想给个大概的地点。
    /// </summary>
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }

    /// <summary>
    /// 手填的行政区，形如「辽宁省沈阳市」。填了就**跳过反向地理编码**。
    ///
    /// 气象预警是按行政区组织的，而系统定位只给坐标（实测 CivicAddress 是空的），
    /// 中间要一次反查。填了这一项就不必让坐标经过第三方地理服务，
    /// 反查结果不准时也能手动纠正。
    /// </summary>
    public string? WeatherRegion { get; set; }

    /// <summary>
    /// 外语歌词的翻译显示方式。
    ///
    /// <c>Both</c>（默认）= 原文 + 译文双行，<c>TranslationOnly</c> = 只显示译文，
    /// <c>OriginalOnly</c> = 只显示原文（等于关掉翻译）。
    ///
    /// 中文歌不受影响 —— 数据源只对外语歌提供译文，中文歌的译文字段直接是空的，
    /// 所以"非简体中文才翻译"是自动的，不需要在这里配。
    /// </summary>
    public LyricTranslationMode LyricTranslation { get; set; } = LyricTranslationMode.Both;

    /// <summary>
    /// Phira 成绩提醒。**默认关闭**，理由和歌词/天气不同 ——
    /// 它全程本地、不联网，但要**读另一个程序的存档文件**，
    /// 而且绝大多数人没装 Phira，开着只是白白轮询进程。
    ///
    /// 解析器只取 charts[]，存档里的 me.email / tokens 一律不读（见 PhiraRecords）。
    /// </summary>
    public bool PhiraEnabled { get; set; }

    /// <summary>灵动点模式。</summary>
    public bool DotMode { get; set; }

    /// <summary>
    /// 展开/收起热键，形如 "Ctrl+Alt+I"。为空表示用内置候选列表自动挑一个。
    /// 之所以可配：本机 Ctrl+Alt+D 与备选 Ctrl+Alt+J 都被别的程序用低级键盘钩子截了，
    /// RegisterHotKey 报成功但按下去没反应，只能让用户自己换。
    /// </summary>
    public string? ExpandHotkey { get; set; }

    /// <summary>灵动点模式热键。</summary>
    public string? DotHotkey { get; set; }

    public bool IsProviderEnabled(string id) => !Providers.TryGetValue(id, out var on) || on;

    public void SetProviderEnabled(string id, bool enabled)
    {
        Providers[id] = enabled;
        Save();
    }

    // ===== 持久化 =====

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // 枚举存成字符串。存数字的话配置文件里是个 0/1/2，用户没法手改，
        // 而这个项目的配置本来就指望人直接编辑
        Converters = { new JsonStringEnumConverter() },
    };

    private static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandX");

    private static string FilePath => Path.Combine(Directory, "config.json");

    public static AppConfig Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppConfig();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppConfig>(json, Options) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // 磁盘满 / 权限不足 —— 配置丢了不影响本次运行
        }
    }
}
