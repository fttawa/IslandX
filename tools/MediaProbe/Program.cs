using System.IO;
using System.Security.Cryptography;
using IslandX.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

// 独立的 GSMTC 探针。
// 存在的意义是提供一个不经过 IslandX 缓存的 ground truth：
// 岛体显示的封面对不上时，用它区分「GSMTC 此刻给的缩略图就是旧的」
// 与「GSMTC 给的是新的，但 IslandX 的缓存逻辑没取到」。
//
// 用法: MediaProbe [导出目录]
//   打印当前会话的标题/艺人/缩略图字节数与 SHA-256 前 8 字节，
//   给了目录就把缩略图存成 <SHA前8>.jpg 供肉眼比对。

var outDir = args.Length > 0 ? args[0] : null;

var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();

// 枚举**全部**会话，不只是 GetCurrentSession()。
// 同一个播放器可能同时挂着两个会话：网易云原生的 SMTC 与 InfLink 插件注册的。
// 前者不给 timeline，后者给 —— 而 GetCurrentSession() 只会返回其中一个，
// 挑哪个取决于谁最近更新，于是播放时读到没 timeline 的、暂停时读到有的。
try
{
    var all = manager.GetSessions();
    var cur = manager.GetCurrentSession();
    Console.WriteLine($"=== 共 {all.Count} 个媒体会话 ===");

    var infos = new List<MediaSessionInfo>(all.Count);
    var curIndex = -1;

    for (var i = 0; i < all.Count; i++)
    {
        var s = all[i];
        var tl = s.GetTimelineProperties();
        var pb = s.GetPlaybackInfo();
        var mp = await s.TryGetMediaPropertiesAsync();

        var hasTl = tl is not null && tl.EndTime > TimeSpan.Zero;
        var genre = mp?.Genres is { Count: > 0 } ? string.Join(",", mp.Genres) : "-";
        var appId = s.SourceAppUserModelId ?? "";

        if (curIndex < 0 && cur is not null
            && (ReferenceEquals(s, cur)
                || string.Equals(appId, cur.SourceAppUserModelId, StringComparison.OrdinalIgnoreCase)))
        {
            curIndex = i;
        }

        var info = new MediaSessionInfo(
            i, appId, hasTl, mp?.Genres is { Count: > 0 }, !string.IsNullOrWhiteSpace(mp?.Title));
        infos.Add(info);

        Console.WriteLine(
            $"  [{i}] {appId,-24} {pb?.PlaybackStatus,-8} "
            + $"时间轴={(hasTl ? "有" : "无 "),-3} 流派={genre,-18} "
            + $"分={MediaSessionPick.Score(info)} 标题={mp?.Title}");

        // 歌词是按"标题 + 艺术家"去网易云搜的，所以这两个字段的**原始形状**
        // 才是判断"为什么搜不到"的依据。用 [] 括起来是为了让首尾空白、
        // 零宽字符这类看不见的东西暴露出来
        Console.WriteLine($"       艺术家=[{mp?.AlbumArtist}] / [{mp?.Artist}]   专辑=[{mp?.AlbumTitle}]");
        Console.WriteLine($"       搜索串 → \"{mp?.Title} {mp?.Artist}\"");

        // 封面这一路单独查。产品那边"取不到封面"有三种截然不同的原因，
        // 而它们在界面上长得一模一样（都是没有图）：
        //   ① 播放器压根不给 Thumbnail 引用
        //   ② 给了引用但流读不出来（还没准备好 —— 这种该重试）
        //   ③ 读出来了但解不了码（格式问题 —— 重试没用）
        // 不分清楚就不知道该往哪修
        Console.WriteLine($"       封面: {await DescribeThumbnailAsync(mp?.Thumbnail)}");
    }

    // 这一段是本探针存在的主要理由之一：**产品的选取判据会挑哪个**。
    // 判据本身有 12 项纯函数自检，但自检喂的是我手写的特征 ——
    // 只有在真会话上跑一遍，才能确认那些特征是从对的字段读出来的。
    if (all.Count > 0)
    {
        var pick = MediaSessionPick.Choose(infos, curIndex);
        Console.WriteLine();
        Console.WriteLine($"系统 GetCurrentSession() → [{curIndex}]");
        Console.WriteLine($"IslandX 选取判据       → [{pick}]"
            + (pick != curIndex ? "   ← 改选了" : "   （一致）"));

        // 同源判断是整套逻辑的安全底线：只在同 AppId 内部改选。
        // 网易云 + InfLink 能不能被认成同源，取决于两者的 AppId 是否相同 ——
        // 这一点只能实测，所以这里直接把分组打出来
        var groups = infos.GroupBy(x => x.AppId, StringComparer.OrdinalIgnoreCase).ToList();
        Console.WriteLine($"AppId 分组             → {groups.Count} 组："
            + string.Join(" / ", groups.Select(g => $"{g.Key}×{g.Count()}")));

        if (groups.Any(g => g.Count() > 1))
            Console.WriteLine("  ⚠ 有同一 AppId 的多个会话 —— 正是选取判据要处理的情形");

        // 投影包装的身份是否稳定。产品里用 ReferenceEquals 判断"挑中的还是同一个会话"
        // 来跳过重复接管；不稳定的话每次事件都会退订再重订，而跨包装的 -=
        // 未必退得掉，那就会变成事件重复触发。所以这一条必须实测，不能想当然。
        var a1 = manager.GetCurrentSession();
        var a2 = manager.GetCurrentSession();
        var g1 = manager.GetSessions();
        var g2 = manager.GetSessions();

        Console.WriteLine();
        Console.WriteLine("投影包装身份稳定性（产品的跳过守卫依赖它）：");
        Console.WriteLine($"  GetCurrentSession() 两次同引用     : {ReferenceEquals(a1, a2)}");
        Console.WriteLine($"  GetSessions()[0] 两次同引用        : "
            + (g1.Count > 0 && g2.Count > 0 ? ReferenceEquals(g1[0], g2[0]).ToString() : "无会话"));
        Console.WriteLine($"  GetSessions()[0] 与 CurrentSession : "
            + (g1.Count > 0 && a1 is not null ? ReferenceEquals(g1[0], a1).ToString() : "无会话"));
    }

    Console.WriteLine();
}
catch (Exception ex)
{
    Console.WriteLine($"枚举会话失败: {ex.Message}");
}

var session = manager.GetCurrentSession();
Console.WriteLine("=== GetCurrentSession() 返回的那个 ===");

if (session is null)
{
    Console.WriteLine("没有活动的媒体会话");
    return;
}

var props = await session.TryGetMediaPropertiesAsync();
if (props is null)
{
    Console.WriteLine("读取媒体属性失败");
    return;
}

var timeline = session.GetTimelineProperties();
var playback = session.GetPlaybackInfo();

Console.WriteLine($"时刻     : {DateTime.Now:HH:mm:ss.fff}");
Console.WriteLine($"来源     : {session.SourceAppUserModelId}");
Console.WriteLine($"标题     : {props.Title}");
Console.WriteLine($"艺人     : {props.Artist}");
Console.WriteLine($"专辑     : {props.AlbumTitle}");
Console.WriteLine($"状态     : {playback?.PlaybackStatus}");
Console.WriteLine($"进度     : {timeline?.Position} / {timeline?.EndTime}");

// timeline 是歌词同步的前提。全零 = 播放器没上报，歌词只能靠内置计时器估。
// 网易云需要 InfLink 插件才会给 timeline。
var hasTimeline = timeline is not null && timeline.EndTime > TimeSpan.Zero;
Console.WriteLine($"时间轴   : {(hasTimeline ? "有" : "无 —— 歌词无法精确同步")}");
Console.WriteLine($"更新时刻 : {timeline?.LastUpdatedTime:HH:mm:ss.fff}");

// Genres：InfLink 把网易云歌曲 ID 塞在这里（NCM-{id}），是精确匹配歌词的钥匙。
// 没有它就只能拿标题+艺人去搜索，遇到同名翻唱容易配错。
var genres = props.Genres is { Count: > 0 } ? string.Join(" | ", props.Genres) : "（空）";
Console.WriteLine($"流派     : {genres}");
Console.WriteLine($"曲目类型 : {props.PlaybackType}");
Console.WriteLine($"音轨号   : {props.TrackNumber}");
Console.WriteLine($"副标题   : {props.Subtitle}");
Console.WriteLine($"专辑艺人 : {props.AlbumArtist}");

if (props.Thumbnail is null)
{
    Console.WriteLine("缩略图   : 无");
    return;
}

try
{
    using var stream = await props.Thumbnail.OpenReadAsync();
    var bytes = new byte[stream.Size];

    using (var reader = new DataReader(stream))
    {
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
    }

    var digest = Convert.ToHexString(SHA256.HashData(bytes))[..16];
    Console.WriteLine($"缩略图   : {bytes.Length} 字节  sha={digest}");

    if (outDir is not null)
    {
        Directory.CreateDirectory(outDir);
        var path = Path.Combine(outDir, $"{digest}.jpg");
        await File.WriteAllBytesAsync(path, bytes);
        Console.WriteLine($"已导出   : {path}");
    }
}
catch (Exception ex)
{
    Console.WriteLine($"缩略图读取失败: {ex.Message}");
}

/// <summary>
/// 查一个缩略图引用到底卡在哪一步。三种失败原因在界面上长得一样，这里把它们分开。
/// </summary>
static async Task<string> DescribeThumbnailAsync(
    Windows.Storage.Streams.IRandomAccessStreamReference? thumb)
{
    if (thumb is null) return "无引用（播放器没给）";

    byte[] bytes;

    try
    {
        using var stream = await thumb.OpenReadAsync();
        if (stream.Size == 0) return "有引用，但流是空的（多半还没准备好）";

        bytes = new byte[stream.Size];
        using var reader = new Windows.Storage.Streams.DataReader(stream);
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
    }
    catch (Exception ex)
    {
        return $"有引用，但读流抛了：{ex.GetType().Name} {ex.Message}";
    }

    try
    {
        var bmp = new System.Windows.Media.Imaging.BitmapImage();
        bmp.BeginInit();
        bmp.StreamSource = new MemoryStream(bytes);
        bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bmp.EndInit();
        bmp.Freeze();

        return $"{bytes.Length} 字节，{bmp.PixelWidth}×{bmp.PixelHeight}，解码正常";
    }
    catch (Exception ex)
    {
        return $"{bytes.Length} 字节，但解码失败：{ex.GetType().Name}";
    }
}
