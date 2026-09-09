using System.Diagnostics;
using System.IO;
using IslandX.Contracts;
using IslandX.Core;

namespace IslandX.Providers;

/// <summary>
/// Phira（TeamFlos 的音游）成绩提醒 —— **单方面联动**，游戏那边不做任何改动。
///
/// 查证过 Phira 0.8.2 能被外部观察到的全部通道：
///
/// | 通道 | 结论 |
/// |---|---|
/// | SMTC 媒体会话 | 不注册（MediaProbe 实测） |
/// | Discord RPC | 源码里没有 |
/// | WebSocket / 命名管道 | 源码里没有，系统里也没有 |
/// | 本地端口 | 无任何 TCP 连接 |
/// | 窗口标题 | 恒为 "Phira"（MINIQUADAPP，miniquad 框架） |
/// | <c>data/data.json</c> | **唯一会随游玩变化的东西** |
///
/// 所以只能watch 存档。它的写入时机由 Phira 的
/// <c>song.rs::update_record</c> 写死：**只在这首第一次打、或刷新个人最好成绩时**
/// 调 <c>save_data()</c>。打了没进步就不写文件，这里也就不会有反应 ——
/// 这是功能的语义边界，不是 bug：它报的是"刷新纪录"，不是"打完一局"。
///
/// ⚠ 存档里还有 <c>me.email</c> 与 <c>tokens</c>。
/// <see cref="PhiraRecords.Read"/> **只解析 charts[]**，其余字段不读、不留、不外发。
/// 全程本地，不联网。
/// </summary>
public sealed class PhiraProvider : IIslandProvider
{
    /// <summary>Phira 主程序的进程名（仓库里那个 crate 就叫 phira-main）。</summary>
    private const string ProcessName = "phira-main";

    /// <summary>
    /// 找进程的间隔。没装 / 没开 Phira 的人占绝大多数，所以这条路径必须便宜 ——
    /// 8 秒一次的进程枚举可以忽略，而且**只有找到之后才会去建文件监视**。
    /// </summary>
    private static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(8);

    /// <summary>
    /// 收到文件变化后的静默期。<c>std::fs::write</c> 是"截断再写"，
    /// 一次保存会触发多个 Changed；而且事件到达时文件很可能只写了一半。
    /// 等它安静下来再读，比读到一半去重试便宜。
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

    /// <summary>成绩在岛上停留的时长。比音量那种一闪而过的长 —— 有三段数字要看。</summary>
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(6);

    /// <summary>手柄图标。码位是渲染字形对照表看过的，不是凭记忆挑的。</summary>
    private const string Glyph = "\uE7FC";

    private readonly Timer _scanTimer;
    private readonly Timer _debounceTimer;
    private readonly Lock _gate = new();

    private FileSystemWatcher? _watcher;
    private string? _dataPath;
    private int _pid;

    /// <summary>上一次看到的成绩快照。null 表示还没建立基线。</summary>
    private Dictionary<string, PhiraRecord>? _snapshot;

    /// <summary>待发布的成绩队列 —— 一次可能有两条，得排队轮流上岛。</summary>
    private readonly Queue<PhiraRecord> _pending = new();

    private bool _started;

    public string Id => "phira";

    public event Action<IslandActivity?>? ActivityChanged;

    /// <summary>探针状态：off / noproc / 监视中的曲目数 / 错误。</summary>
    internal static string DebugState = "off";

    public PhiraProvider()
    {
        _scanTimer = new Timer(_ => Scan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounceTimer = new Timer(_ => Reload(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public Task StartAsync()
    {
        _started = true;
        DebugState = "noproc";

        // 立刻扫一次，之后按 ScanEvery 轮询
        _scanTimer.Change(TimeSpan.Zero, ScanEvery);
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _started = false;
        _scanTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _debounceTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        Detach();
        DebugState = "off";
    }

    // ================= 找到 Phira =================

    /// <summary>
    /// 找 phira-main 进程，并从它的可执行文件路径推出存档位置。
    ///
    /// 用进程路径而不是去磁盘上搜：Phira 是绿色包，可能解压在任何地方
    /// （本机在 D:\Downloads\Phira-windows-x86_64-v0.8.2）。
    /// 扫盘既慢又可能扫到别人的副本，而进程路径是唯一确定的。
    /// </summary>
    private void Scan()
    {
        if (!_started) return;

        try
        {
#if DEBUG
            // 开发用：ISLANDX_PHIRA_DATA 指向一个存档副本，跳过找进程这一步。
            // 整条链路（监视 → 去抖 → Diff → 上岛）只有拿真文件驱动才算验过，
            // 而**绝不能拿用户的真存档来试** —— 那是别人的游戏进度。
            // 指向我自己的临时副本，改它、看岛体反应，真存档一个字节都不碰。
            if (Environment.GetEnvironmentVariable("ISLANDX_PHIRA_DATA") is { Length: > 0 } fake)
            {
                if (_watcher is not null) return;
                if (!File.Exists(fake)) { DebugState = "nodata"; return; }

                Attach(fake, pid: -1);
                return;
            }
#endif


            var proc = Process.GetProcessesByName(ProcessName).FirstOrDefault();

            if (proc is null)
            {
                if (_watcher is not null)
                {
                    Detach();
                    DebugState = "noproc";
                }

                return;
            }

            if (_watcher is not null && proc.Id == _pid) return;   // 已经在盯着同一个了

            // 换了进程（重启了游戏）就重新挂
            Detach();

            var exe = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) { DebugState = "nopath"; return; }

            var root = Path.GetDirectoryName(exe);
            if (root is null) { DebugState = "nopath"; return; }

            var path = Path.Combine(root, "data", "data.json");
            if (!File.Exists(path)) { DebugState = "nodata"; return; }

            Attach(path, proc.Id);
        }
        catch (Exception ex)
        {
            // 拿不到 MainModule 是常见情况（权限、进程刚退出），不该刷屏
            DebugState = $"scanfail/{ex.GetType().Name}";
        }
    }

    private void Attach(string path, int pid)
    {
        _dataPath = path;
        _pid = pid;

        // **先建立基线再开始监视**。不然启动那一刻会把存档里已有的
        // 二十几条历史成绩全当成"刚刷新"，一口气弹一屏
        _snapshot = TryRead(path);
        DebugState = _snapshot is null ? "readfail" : $"watch/{_snapshot.Count}rec";

        var dir = Path.GetDirectoryName(path);
        if (dir is null) return;

        _watcher = new FileSystemWatcher(dir, "data.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
    }

    private void Detach()
    {
        if (_watcher is not null)
        {
            _watcher.Changed -= OnChanged;
            _watcher.Created -= OnChanged;
            _watcher.Dispose();
            _watcher = null;
        }

        _dataPath = null;
        _pid = 0;
        _snapshot = null;

        lock (_gate) _pending.Clear();
    }

    // ================= 存档变化 =================

    private void OnChanged(object sender, FileSystemEventArgs e)
        => _debounceTimer.Change(Debounce, Timeout.InfiniteTimeSpan);

    private void Reload()
    {
        if (!_started || _dataPath is null) return;

        var now = TryRead(_dataPath);
        if (now is null) { DebugState = "readfail"; return; }

        var before = _snapshot;
        _snapshot = now;

        if (before is null) { DebugState = $"watch/{now.Count}rec"; return; }

        var changed = PhiraRecords.Diff(before, now);
        DebugState = $"watch/{now.Count}rec/+{changed.Length}";

        if (changed.Length == 0) return;

        lock (_gate)
        {
            foreach (var record in changed) _pending.Enqueue(record);
        }

        PublishNext();
    }

    /// <summary>
    /// 发布队列里的下一条。一次刷了两首（罕见，但导入 + 游玩会撞上）时轮流上岛 ——
    /// 同时发两条的话仲裁器只会留下后到的那条，前一条等于没显示。
    /// </summary>
    private void PublishNext()
    {
        PhiraRecord record;

        lock (_gate)
        {
            if (_pending.Count == 0) return;
            record = _pending.Dequeue();
        }

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = $"phira:{record.Key}:{record.Score}",
            ProviderId = Id,
            Glyph = Glyph,
            Title = string.IsNullOrWhiteSpace(record.Name) ? "Phira" : record.Name,
            Subtitle = PhiraRecords.Describe(record),
            AutoDismissAfter = Dwell,

            // 占主位而不是右侧小圆：曲名 + 难度 + 分数 + 准度 + 评级，
            // 塞进 32px 的圆里等于没显示（和降水提醒同一个理由）
            WantsMainSlot = true,
        });

        // 队列里还有就排到这条撤下之后再发
        lock (_gate)
        {
            if (_pending.Count == 0) return;
        }

        _ = Task.Delay(Dwell + TimeSpan.FromMilliseconds(400)).ContinueWith(_ =>
        {
            if (_started) PublishNext();
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// 主动查看时用的那条活动 —— 右键工具栏点「Phira」走这里，给的是**当前最好成绩**。
    ///
    /// 和自动那条的区别：自动那条报的是"刚刷新了纪录"（只在存档变化时发、几秒后撤下），
    /// 这条是"我现在想看看我打得怎么样"。没在跑 Phira、或还没有任何成绩时返回 null，
    /// 工具栏据此把按钮置灰。
    /// </summary>
    public IslandActivity? BuildPeek()
    {
        var snapshot = _snapshot;
        if (snapshot is null || snapshot.Count == 0) return null;

        // 按分数取最高的那一首。准度更高但分数更低的情况存在，
        // 但"最好成绩"在音游语境里就是按分数说的
        PhiraRecord best = default;
        var found = false;

        foreach (var record in snapshot.Values)
        {
            if (found && record.Score <= best.Score) continue;
            best = record;
            found = true;
        }

        if (!found) return null;

        return new IslandActivity
        {
            Id = "phira:peek",
            ProviderId = Id,
            Priority = ActivityPriority.High,
            Glyph = Glyph,
            Title = string.IsNullOrWhiteSpace(best.Name) ? "Phira" : best.Name,
            Subtitle = PhiraRecords.Describe(best),
            WantsMainSlot = true,

            // 没有 AutoDismissAfter：主动看的东西不该自己跑掉
        };
    }

    /// <summary>
    /// 读存档。文件可能正被 Phira 写到一半（<c>std::fs::write</c> 先截断后写），
    /// 那时 JSON 解析会抛 —— 短暂重试几次，都不行就放弃这一轮，
    /// 下次保存自然会再触发一遍。
    /// </summary>
    private static Dictionary<string, PhiraRecord>? TryRead(string path)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                // 用 FileShare.ReadWrite 打开：Phira 正持有写句柄时，
                // 默认的独占读会直接失败
                using var stream = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);

                return PhiraRecords.Read(reader.ReadToEnd());
            }
            catch
            {
                Thread.Sleep(120);
            }
        }

        return null;
    }
}
