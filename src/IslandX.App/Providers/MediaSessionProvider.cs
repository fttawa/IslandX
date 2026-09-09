using System.IO;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using IslandX.Contracts;
using IslandX.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace IslandX.Providers;

/// <summary>
/// 通过 WinRT 全局媒体传输控件（GSMTC）读取系统当前播放的任何媒体 ——
/// Spotify、网易云、QQ 音乐、浏览器视频都会上报到这里，不需要针对播放器做适配。
/// </summary>
public sealed class MediaSessionProvider : IIslandProvider
{
    private const string ProviderId = "media";

    /// <summary>
    /// 追查缩略图的时间点（毫秒）。播放器上报新标题与新缩略图往往不在同一刻
    /// （实测网易云会晚几百毫秒），只靠事件会停在旧封面上。
    /// </summary>
    /// <summary>
    /// 追查缩略图的间隔（毫秒，逐次递增）。累计约 15 秒。
    ///
    /// 前几档对付"换歌时播放器晚几百毫秒才换图"；后两档是给
    /// **图还在下载**的情形留的 —— 流媒体播放器首次播一首歌时，
    /// 封面可能要好几秒才落地。每一档只是一次属性读取，成功或换歌立刻取消，
    /// 所以尾巴拉长几乎不花什么。
    /// </summary>
    private static readonly int[] ChaseDelaysMs = [180, 400, 800, 1500, 2600, 4000, 6000];

    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    /// <summary>
    /// 会话复选的互斥闸。CurrentSessionChanged 与 SessionsChanged 常常紧挨着响，
    /// 两次并发的接管会互相踩掉对方的事件订阅（一个 Detach 撤掉另一个刚订上的）。
    /// 用 WaitAsync(0) 而不是排队：正在挑的那次读的就是最新集合，重复挑没有意义。
    /// </summary>
    private readonly SemaphoreSlim _pickGate = new(1, 1);
    private readonly System.Threading.Timer _tickTimer;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    // 封面解码开销大，用缩略图内容的哈希判断是否真的换了图
    private ImageSource? _cachedArtwork;
    private Color? _cachedAccent;
    private string _cachedTitle = "";
    private string _cachedArtist = "";
    private string _cachedThumbnailHash = "";

    private CancellationTokenSource? _chaseCts;
    private bool _disposed;

    // ===== 歌词 =====

    /// <summary>歌词服务。为 null 表示功能关闭（默认如此，它要联网）。</summary>
    private LyricsService? _lyrics;

    /// <summary>当前曲目的歌词行；null = 没有或还没拉到。</summary>
    private LyricLine[]? _lyricLines;

    /// <summary>
    /// 外语歌词的翻译显示方式，由 App 从配置注入，也可在托盘里随时改。
    ///
    /// 改完立刻重发一次：不然要等到下一句才看得出切换生效，
    /// 慢歌一句能唱十几秒，那时用户早就以为菜单没起作用了。
    /// </summary>
    public LyricTranslationMode TranslationMode
    {
        get;
        set
        {
            if (field == value) return;
            field = value;
            PublishFromCache();
        }
    } = LyricTranslationMode.Both;

    /// <summary>SMTC 的 Genre 字段。InfLink 会把网易云歌曲 ID 塞在这里。</summary>
    private string _cachedGenre = "";

    /// <summary>
    /// 换歌后歌名的驻留时长。歌词一上来就顶掉歌名的话，用户根本没看清在放什么歌 ——
    /// 而"换歌了，是这首"恰恰是这一刻最该传达的信息。
    /// 实现方式是这段时间内不给歌词（返回 null），岛体那边看到 null 自然显示歌名。
    /// </summary>
    private const int TitleHoldMs = 1500;

    /// <summary>
    /// 歌词前瞻。查询位置时加上这个偏移，整句的切换时刻因此比真实时间戳**早**这么多。
    ///
    /// 目的是让退场动画提前发生：换句过渡约 200ms，提前 170ms 起步，
    /// 新句完全显形的时刻才大致对准时间戳。不提前的话人是先听到下一句、
    /// 再看到岛上开始动，慢半拍。
    /// </summary>
    private const int LyricLeadMs = 170;

    /// <summary>
    /// 发布间隔。歌词开启时必须细得多 —— 500ms 一次的话切句时刻最多偏半秒，
    /// 完全跟不上唱。每次 tick 的代价只是读一次 timeline 加一次二分查找，
    /// 没有网络也没有解码，110ms 一次可以忽略。
    /// </summary>
    private const int TickWithLyricsMs = 110, TickPlainMs = 500;

    /// <summary>本首歌开始播的时刻，用于 <see cref="TitleHoldMs"/> 的驻留计时。</summary>
    private DateTimeOffset _trackChangedAt = DateTimeOffset.MinValue;

    // ===== 无 timeline 时的内置计时器 =====
    //
    // 网易云（不装 InfLink）、酷狗等压根不上报 timeline，位置无从得知，
    // 歌词就完全显示不出来 —— 功能等于没有。所以回落成自己数秒：
    // 从"这首歌开始播"起累计，暂停时冻结。
    //
    // 代价很明确：**用户拖动进度条之后会错位**，因为播放器不会告诉我们它跳了。
    // 这也是为什么有 timeline 时一定优先用真实值，计时器只是兜底。

    /// <summary>本次连续播放的起点；null 表示当前暂停。</summary>
    private DateTimeOffset? _timerResumedAt;

    /// <summary>此前累计的播放时长（跨暂停累加）。</summary>
    private TimeSpan _timerElapsed;

    /// <summary>上一次发布时的播放态，用于识别暂停/恢复的跳变。</summary>
    private bool _timerWasPlaying;

    /// <summary>
    /// 开启或关闭歌词。开启时才会联网 —— 这个开关同时是隐私开关，
    /// 关掉之后不会再有任何请求发往第三方。
    /// </summary>
    public void SetLyricsEnabled(bool enabled)
    {
        if (enabled)
        {
            _lyrics ??= new LyricsService();
        }
        else
        {
            _lyrics?.Dispose();
            _lyrics = null;
            _lyricLines = null;
        }

        // 发布频率跟着歌词开关走：开了要细到能跟上逐句，关了没必要那么勤
        if (_session is not null) RestartTick();

        PublishFromCache();
    }

    /// <summary>按当前是否显示歌词重设发布间隔。</summary>
    private void RestartTick()
    {
        var period = TimeSpan.FromMilliseconds(_lyrics is not null ? TickWithLyricsMs : TickPlainMs);
        _tickTimer.Change(period, period);
    }

    internal LyricsService? LyricsDiagnostics => _lyrics;

    public MediaSessionProvider()
    {
        _tickTimer = new System.Threading.Timer(_ => PublishFromCache(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public string Id => ProviderId;

    public event Action<IslandActivity?>? ActivityChanged;

#if DEBUG
    /// <summary>诊断计数：属性变更事件 / 被闸门丢弃 / 实际解码封面 / 追查命中。</summary>
    internal static int DebugEventCount;
    internal static int DebugDroppedCount;
    internal static int DebugArtLoadCount;
    internal static int DebugChaseHitCount;

    /// <summary>当前生效的缩略图哈希，与 tools/MediaProbe 的输出同格式，可直接对比。</summary>
    internal static string DebugThumbHash = "";
#endif

    /// <summary>
    /// 歌词链路状态：off / 行数 / 有无 timeline / 命中缓存与网络次数。
    /// 不加 #if DEBUG —— 歌词"没显示"有五种原因（功能关、没拉到、匹配失败、
    /// 播放器没给进度、间奏），肉眼完全分不清，线上也需要能问出来。
    /// </summary>
    internal static string DebugLyricState = "init";

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.CurrentSessionChanged += OnCurrentSessionChanged;

            // SessionsChanged 也要订：更丰富的那个会话可能**后到**（InfLink 比网易云
            // 原生 SMTC 晚注册），而它到达时"当前会话"没变，CurrentSessionChanged 不会响。
            // 只订前者就会一直挂在先到的那个残缺会话上。
            _manager.SessionsChanged += OnSessionsChanged;

            AttachSession(await PickSessionAsync(_manager));
        }
        catch (Exception ex)
        {
            // 媒体服务不可用时静默降级，不能因为一个 Provider 挂掉整个岛
            System.Diagnostics.Debug.WriteLine($"[media] 初始化失败: {ex.Message}");
            ActivityChanged?.Invoke(null);
        }
    }

    public void Stop()
    {
        _disposed = true;
        CancelChase();
        _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _tickTimer.Dispose();

        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
        }

        DetachSession();
    }

    public async Task SkipPreviousAsync()
    {
        try { if (_session is not null) await _session.TrySkipPreviousAsync(); } catch { /* 播放器可能已退出 */ }
    }

    public async Task TogglePlayPauseAsync()
    {
        try { if (_session is not null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task SkipNextAsync()
    {
        try { if (_session is not null) await _session.TrySkipNextAsync(); } catch { }
    }

    // 两个事件都走同一条复选路径：会话集合变了、或系统认为的"当前"变了，
    // 都要重新挑一次。故意不 await —— 事件回调里 await 会把 WinRT 的事件线程占住。
    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => _ = AttachBestAsync(sender);

    private void OnSessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
        => _ = AttachBestAsync(sender);

    /// <summary>复选一次并接管。<see cref="_pickGate"/> 保证不会两次并发接管。</summary>
    private async Task AttachBestAsync(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        if (!await _pickGate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            var picked = await PickSessionAsync(manager).ConfigureAwait(false);

            // 挑中的还是同一个就什么都不做。省掉一次退订/重订 + RefreshAsync ——
            // SessionsChanged 在别的播放器起停时也会响，那与我们无关。
            //
            // 引用比较在这里够用，因为**同一个 API 反复调返回同一个包装**（实测：
            // GetCurrentSession() 两次同引用、GetSessions()[0] 两次同引用）。
            // 跨 API 则不同（GetSessions()[0] 与 GetCurrentSession() 实测 False），
            // 所以在"单会话走 GetCurrentSession / 多会话走 GetSessions"之间切换时
            // 会多接管一次 —— 那本来就是会话集合真的变了，重接是对的。
            // 也不会漏退订：退订用的始终是当初订阅的那个包装。
            if (ReferenceEquals(picked, _session)) return;

            AttachSession(picked);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[media] 复选失败: {ex.Message}");
        }
        finally
        {
            _pickGate.Release();
        }
    }

    /// <summary>
    /// 挑一个会话接管。判据在 <see cref="MediaSessionPick"/> 里（纯函数，有自检），
    /// 这里只负责把 WinRT 会话读成它要的特征、再把结论映射回真会话。
    ///
    /// 单个会话读属性失败就当它"什么都没有"，不能让一个正在消失的会话把整条链路带崩。
    /// </summary>
    private async Task<GlobalSystemMediaTransportControlsSession?> PickSessionAsync(
        GlobalSystemMediaTransportControlsSessionManager manager)
    {
        try
        {
            var sessions = manager.GetSessions();
            var current = manager.GetCurrentSession();

            // 只有一个（绝大多数情况）就没什么可挑的，直接沿用系统的判断。
            // 顺带省掉 N 次 TryGetMediaPropertiesAsync
            if (sessions.Count <= 1)
            {
                DebugSessionPick = $"{sessions.Count}sess/trust";
                return current;
            }

            var currentId = current?.SourceAppUserModelId;
            var infos = new List<MediaSessionInfo>(sessions.Count);
            var currentIndex = -1;

            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var appId = s.SourceAppUserModelId ?? "";

                // 哪个是 current：先按引用，再按 AppId 兜底。
                // 兜底不是保险，是**主路径** —— 实测 GetSessions()[i] 与
                // GetCurrentSession() 是两个不同的投影包装，引用比较恒为 false
                // （用 MediaProbe 可以直接看到这三行对照）。
                //
                // 同 AppId 有多个时取第一个。这不影响结论：接下来要在同源组里重挑，
                // 而组的成员与从哪个下标进来无关，下标只用于平分时的稳定性偏好。
                if (currentIndex < 0 && current is not null
                    && (ReferenceEquals(s, current)
                        || string.Equals(appId, currentId, StringComparison.OrdinalIgnoreCase)))
                {
                    currentIndex = i;
                }

                bool hasTimeline = false, hasGenre = false, hasTitle = false;

                try
                {
                    var tl = s.GetTimelineProperties();
                    hasTimeline = tl is not null && tl.EndTime > TimeSpan.Zero;

                    var props = await s.TryGetMediaPropertiesAsync();
                    hasTitle = !string.IsNullOrWhiteSpace(props?.Title);
                    hasGenre = props?.Genres is { Count: > 0 };
                }
                catch { /* 会话正在消失，按"什么都没有"计分 */ }

                infos.Add(new MediaSessionInfo(i, appId, hasTimeline, hasGenre, hasTitle));
            }

            var pick = MediaSessionPick.Choose(infos, currentIndex);

            // 探针要能看出"系统给的是哪个、我们改成了哪个、依据是什么"。
            // 只报结论的话，挑错时根本无从判断是判据错了还是特征读错了
            DebugSessionPick = $"{sessions.Count}sess/cur{currentIndex}/pick{pick}/"
                + string.Concat(infos.Select(x => MediaSessionPick.Score(x).ToString("X1")));

            return pick >= 0 ? sessions[pick] : current;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[media] 会话选取失败，回落 GetCurrentSession: {ex.Message}");
            DebugSessionPick = $"fail/{ex.GetType().Name}";
            try { return manager.GetCurrentSession(); } catch { return null; }
        }
    }

    /// <summary>
    /// 会话选取的调试状态：几个会话、系统认为哪个是当前、我们挑了哪个、各自的评分。
    /// 不加 #if DEBUG —— "进度条/歌词忽有忽无"在界面上就是个玄学现象，
    /// 而它最可能的原因恰恰是挑错了会话，线上也需要能问出来。
    /// </summary>
    internal static string DebugSessionPick = "-";

    private void AttachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        DetachSession();
        _session = session;

        if (_session is null)
        {
            _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
            DebugLyricState = _lyrics is null ? "off" : "nosession";
            ActivityChanged?.Invoke(null);
            return;
        }

        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;

        _ = RefreshAsync();
    }

    private void DetachSession()
    {
        if (_session is null) return;

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
#if DEBUG
        Interlocked.Increment(ref DebugEventCount);
#endif
        _ = RefreshAsync();
    }

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => PublishFromCache();

    /// <summary>重新拉取标题/艺人/封面。多个事件同时到达时串行化，避免重复解码封面。</summary>
    private async Task RefreshAsync()
    {
        if (_disposed) return;

        if (!await _refreshGate.WaitAsync(0))
        {
#if DEBUG
            Interlocked.Increment(ref DebugDroppedCount);
#endif
            // 丢弃是安全的：正在跑的那次会读到最新属性，
            // 且换歌后封面没跟上时还有追查兜底。
            return;
        }

        try
        {
            var session = _session;
            if (session is null) return;

            var props = await session.TryGetMediaPropertiesAsync();
            if (props is null) return;

            var title = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title.Trim();
            var artist = (props.Artist ?? "").Trim();
            if (string.IsNullOrWhiteSpace(artist)) artist = (props.AlbumTitle ?? "").Trim();

            var trackChanged = title != _cachedTitle || artist != _cachedArtist;
            _cachedTitle = title;
            _cachedArtist = artist;

            // InfLink 把网易云歌曲 ID 塞在 Genre 里（NCM-{id}），有它就能精确匹配歌词，
            // 免去按标题搜索时配到同名翻唱的风险
            _cachedGenre = props.Genres is { Count: > 0 } ? string.Join(" ", props.Genres) : "";

            // 换歌就把上一首的歌词丢掉，否则新歌会顶着旧词唱；计时器也从零重开
            if (trackChanged)
            {
                _lyricLines = null;
                _timerElapsed = TimeSpan.Zero;
                _timerResumedAt = null;
                _timerWasPlaying = false;
                _trackChangedAt = DateTimeOffset.Now;   // 歌名驻留从此刻起算
            }

            var artworkChanged = await TryUpdateArtworkAsync(props.Thumbnail);

            PublishFromCache();
            RestartTick();

            // 什么时候该追：
            //
            // ① 换歌了但缩略图还是上一首的 —— 播放器晚几百毫秒才换图。
            //    不追的话这张旧封面会一直挂到下次换歌，因为后续事件都判定"图没变"。
            // ② **播放器给了缩略图引用，我们却一张图都没有** —— 流还没准备好
            //    （OpenReadAsync 抛、或者 Size==0），或者解码失败了。
            //
            // 第 ② 种原先没有覆盖：只在换歌那一刻追，读失败就得等到下一次换歌
            // 才有机会恢复。而"引用在、图没有"恰恰是最该重试的情形 ——
            // 它几乎总是"还没好"，而不是"没有"。
            var hasThumbRef = props.Thumbnail is not null;

            if (artworkChanged) CancelChase();
            else if (trackChanged || (hasThumbRef && _cachedArtwork is null)) StartArtworkChase();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[media] 刷新失败: {ex.Message}");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    /// <summary>
    /// 读取缩略图并在内容真的变化时解码。
    /// 判据是**缩略图字节的哈希**，而不是标题 —— 标题与缩略图不保证同一刻更新，
    /// 用标题做键会把"新标题 + 旧封面"这一瞬固化下来，封面就永远慢一首。
    /// </summary>
    /// <returns>封面是否发生了变化。</returns>
    private async Task<bool> TryUpdateArtworkAsync(IRandomAccessStreamReference? thumbnail)
    {
        if (thumbnail is null)
        {
            if (_cachedThumbnailHash.Length == 0) return false;

            _cachedThumbnailHash = "";
            _cachedArtwork = null;
            _cachedAccent = null;
            return true;
        }

        var bytes = await ReadThumbnailBytesAsync(thumbnail);
        if (bytes is null || bytes.Length == 0) return false;

        var hash = Convert.ToHexString(SHA256.HashData(bytes), 0, 8);
        if (hash == _cachedThumbnailHash) return false;

        var (image, accent) = DecodeArtwork(bytes);

        // 解码失败时**不要记哈希**。
        //
        // 记了的话这串字节就被当成"已经处理过"，后续每次读到同样的字节都会撞上
        // 上面那个 `hash == _cachedThumbnailHash` 而直接返回 —— 于是这首歌
        // 到换歌为止都不会再有封面，一次偶发的解码失败被**永久固化**。
        //
        // 返回 false 还有第二层作用：调用方看到"没变"才会去追，
        // 返回 true 的话追查会被当场取消，等于自断退路。
        if (image is null) return false;

        _cachedThumbnailHash = hash;
        _cachedArtwork = image;
        _cachedAccent = accent;

#if DEBUG
        Interlocked.Increment(ref DebugArtLoadCount);
        DebugThumbHash = hash;
#endif
        return true;
    }

    private static async Task<byte[]?> ReadThumbnailBytesAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            if (stream.Size == 0) return null;

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[media] 缩略图读取失败: {ex.Message}");
            return null;
        }
    }

    private static (ImageSource?, Color?) DecodeArtwork(byte[] bytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 192;   // 展开态最大只用到 88px，192 足够且省内存
            bitmap.EndInit();
            bitmap.Freeze();                 // 冻结后可跨线程传给 UI

            return (bitmap, ArtworkColor.Extract(bitmap));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[media] 封面解码失败: {ex.Message}");
            return (null, null);
        }
    }

    // ================= 缩略图追查 =================

    private void StartArtworkChase()
    {
        CancelChase();

        var cts = new CancellationTokenSource();
        _chaseCts = cts;
        _ = ChaseArtworkAsync(cts.Token);
    }

    private void CancelChase()
    {
        var cts = Interlocked.Exchange(ref _chaseCts, null);
        if (cts is null) return;

        try { cts.Cancel(); } catch { }
        cts.Dispose();
    }

    /// <summary>在几个递增的时刻重查缩略图，直到它真的换了图或次数用尽。</summary>
    private async Task ChaseArtworkAsync(CancellationToken ct)
    {
        foreach (var delay in ChaseDelaysMs)
        {
            try { await Task.Delay(delay, ct); }
            catch (OperationCanceledException) { return; }

            if (_disposed || ct.IsCancellationRequested) return;

            var session = _session;
            if (session is null) return;

            // 与 RefreshAsync 共用闸门，避免并发改缓存字段
            if (!await _refreshGate.WaitAsync(400, CancellationToken.None)) continue;

            try
            {
                if (ct.IsCancellationRequested) return;

                var props = await session.TryGetMediaPropertiesAsync();
                if (props is null) continue;

                if (await TryUpdateArtworkAsync(props.Thumbnail))
                {
#if DEBUG
                    Interlocked.Increment(ref DebugChaseHitCount);
#endif
                    PublishFromCache();
                    return;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[media] 追查缩略图失败: {ex.Message}");
            }
            finally
            {
                _refreshGate.Release();
            }
        }
    }

    /// <summary>用缓存的标题/封面加上实时进度，合成一个活动快照发出去。</summary>
    private void PublishFromCache()
    {
        if (_disposed) return;

        var session = _session;
        if (session is null)
        {
            // 压根没有媒体会话，和"歌词功能被关掉"是两件事。
            // 不区分的话探针停在初始值 off，而 off 在文档里的含义是"功能未开启" ——
            // 于是"没在放歌"读起来就像"你把歌词关了"，这正是这套探针要消除的歧义。
            DebugLyricState = _lyrics is null ? "off" : "nosession";
            ActivityChanged?.Invoke(null);
            return;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            var status = playback?.PlaybackStatus;

            // 已停止/关闭的会话不占用岛体
            if (status is null
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped)
            {
                _tickTimer.Change(Timeout.Infinite, Timeout.Infinite);
                ActivityChanged?.Invoke(null);
                return;
            }

            var isPlaying = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            AdvanceTimer(isPlaying);

            var (lyric, lyricSub) = CurrentLyric(session);

            ActivityChanged?.Invoke(new IslandActivity
            {
                Id = "media:current",
                ProviderId = ProviderId,
                Priority = ActivityPriority.Normal,
                // Segoe Fluent Icons: E8D6 音乐, E769 暂停
                Glyph = isPlaying ? "" : "",
                Title = _cachedTitle,
                Subtitle = string.IsNullOrEmpty(_cachedArtist) ? null : _cachedArtist,
                Progress = ComputeProgress(session, isPlaying),
                Lyric = lyric,
                LyricSub = lyricSub,
                Artwork = _cachedArtwork,
                AccentColor = _cachedAccent,
                HasTransportControls = true,
                IsPlaying = isPlaying,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[media] 发布失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 当前该显示的那句歌词。
    ///
    /// **完全依赖播放器上报 timeline**：没有位置就无从知道唱到哪了，
    /// 这时返回 null 而不是从第一句开始猜 —— 一句对不上的歌词比没有歌词更碍眼。
    /// 网易云需要装 InfLink 插件才有 timeline（见 README）。
    /// </summary>
    private (string? Main, string? Sub) CurrentLyric(GlobalSystemMediaTransportControlsSession session)
    {
        if (_lyrics is null)
        {
            DebugLyricState = "off";
            return (null, null);
        }

        // 缓存里没有就顺手触发一次后台拉取，拉到后回调重新发布
        if (_lyricLines is null)
        {
            if (!_lyrics.TryGet(_cachedTitle, _cachedArtist, _cachedGenre, out var fetched, PublishFromCache))
            {
                DebugLyricState = $"fetching/c{_lyrics.CacheHits}/n{_lyrics.Fetches}";
                return (null, null);
            }

            _lyricLines = fetched;
        }

        var stat = $"c{_lyrics.CacheHits}/n{_lyrics.Fetches}/m{_lyrics.Misses}"
            + (_cachedGenre.Contains("NCM-", StringComparison.OrdinalIgnoreCase) ? "/id" : "/search");

        if (_lyricLines is null || _lyricLines.Length == 0)
        {
            DebugLyricState = $"nomatch/{stat}/{_lyrics.LastError}";
            return (null, null);
        }

        // 换歌后先让歌名站一会儿。返回 null，岛体那边就显示「歌名 + 艺人」——
        // 与"前奏还没唱到第一句"走的是同一条路径，不需要额外的状态。
        var held = (DateTimeOffset.Now - _trackChangedAt).TotalMilliseconds;
        if (held < TitleHoldMs)
        {
            DebugLyricState = $"{_lyricLines.Length}line/hold{TitleHoldMs - held:F0}ms/{stat}";
            return (null, null);
        }

        // 真实 timeline 优先；没有就用自己数的秒兜底。
        // 加上前瞻：整句的切换因此比真实时间戳早 LyricLeadMs，
        // 退场动画得以提前起步，新句显形时才对准时间戳。
        var reported = ComputePosition(session);
        var position = (reported ?? _timerElapsed) + TimeSpan.FromMilliseconds(LyricLeadMs);
        var source = reported is null ? "timer" : "smtc";

        var line = Lyrics.LineAt(_lyricLines, position);
        var (main, sub) = Lyrics.Present(line, TranslationMode);

        DebugLyricState = $"{_lyricLines.Length}line/{source}@{position.TotalSeconds:F0}s/{stat}"
            + $"/{TranslationMode.ToString()[..2].ToLowerInvariant()}"
            + (sub is not null ? "+t" : "")
            + (line is null ? "/intro" : "");

        return (main, sub);
    }

    /// <summary>
    /// 维护内置计时器。每次发布都要调 —— 它靠"上一次是不是在播"来识别暂停与恢复。
    /// </summary>
    private void AdvanceTimer(bool isPlaying)
    {
        var now = DateTimeOffset.Now;

        if (isPlaying)
        {
            if (!_timerWasPlaying || _timerResumedAt is null)
            {
                // 刚从暂停恢复（或首次开播）：把计时起点挪到现在，之前的累计保留
                _timerResumedAt = now;
            }
            else
            {
                var delta = now - _timerResumedAt.Value;

                // 系统睡眠 / 长时间挂起后 delta 会是个荒唐的大数，不采信
                if (delta > TimeSpan.Zero && delta < TimeSpan.FromSeconds(30)) _timerElapsed += delta;

                _timerResumedAt = now;
            }
        }
        else
        {
            _timerResumedAt = null;
        }

        _timerWasPlaying = isPlaying;
    }

    /// <summary>
    /// 实时播放位置。与 <see cref="ComputeProgress"/> 同一套推算，
    /// 但返回绝对时间而不是 0–1 的比例 —— 歌词要的是"第几秒"。
    /// </summary>
    private static TimeSpan? ComputePosition(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline is null) return null;
            if (timeline.EndTime - timeline.StartTime <= TimeSpan.Zero) return null;

            var position = timeline.Position - timeline.StartTime;

            if (timeline.LastUpdatedTime > DateTimeOffset.MinValue)
            {
                var drift = DateTimeOffset.Now - timeline.LastUpdatedTime;
                if (drift > TimeSpan.Zero && drift < TimeSpan.FromSeconds(10)) position += drift;
            }

            return position;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// GSMTC 的 Position 不是实时的，只在 LastUpdatedTime 那一刻准确。
    /// 播放中需要按经过时间本地推算，否则进度条会一秒一跳地卡住。
    /// </summary>
    private static double? ComputeProgress(GlobalSystemMediaTransportControlsSession session, bool isPlaying)
    {
        try
        {
            var timeline = session.GetTimelineProperties();
            if (timeline is null) return null;

            // 注意：不少播放器（尤其网络电台、部分国产客户端）完全不上报 timeline，
            // 此时 StartTime/EndTime/Position 全为零、LastUpdatedTime 停在 FILETIME 纪元，
            // 下面的 duration 检查会让进度条正确地隐藏，而不是显示一条假的空进度。
            var duration = timeline.EndTime - timeline.StartTime;
            if (duration <= TimeSpan.Zero) return null;

            var position = timeline.Position - timeline.StartTime;

            if (isPlaying && timeline.LastUpdatedTime > DateTimeOffset.MinValue)
            {
                var drift = DateTimeOffset.Now - timeline.LastUpdatedTime;
                // 部分播放器不刷新 LastUpdatedTime，偏移过大时不采信
                if (drift > TimeSpan.Zero && drift < TimeSpan.FromSeconds(10)) position += drift;
            }

            return Math.Clamp(position.TotalSeconds / duration.TotalSeconds, 0, 1);
        }
        catch
        {
            return null;
        }
    }
}
