using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;

namespace IslandX.Core;

/// <summary>
/// 诊断日志。为「右键岛体大概率卡死」这个现象加的。
///
/// 卡死不是崩溃 —— 进程还活着，只是 UI 线程不动了。这决定了两件事：
///
/// 1. **每写一行就落盘**（<c>AutoFlush</c>）。带缓冲的话卡住那一刻缓冲区里的内容
///    永远看不到，而那几行恰恰是最关键的。为此付的代价是每行一次系统调用，
///    所以只在右键这条路径上记，不在每帧的渲染回调里记。
/// 2. **另起一个线程当看门狗**。UI 线程卡住时它自己没法报告这件事，
///    只有别的线程能。见 <see cref="StartWatchdog"/>。
///
/// 日志的最后一行就是"卡在哪之前"。配合看门狗那几行"UI 线程无响应 Ns"，
/// 能把范围收到某一次调用上。
///
/// 默认不开，靠 <c>ISLANDX_DIAG=1</c> 打开（<c>tools/diag.ps1</c> 会设好）——
/// 常开的话每次右键都要写十几次磁盘，而这只是为查一个 bug 用的。
/// </summary>
public static class Diag
{
    private static readonly Lock Gate = new();
    private static StreamWriter? _writer;
    private static Stopwatch? _clock;

    /// <summary>日志上限。卡死可能反复发生，不能让它涨到几百兆。</summary>
    private const long MaxBytes = 8L * 1024 * 1024;

    private static long _written;

    public static bool Enabled { get; private set; }

    public static string Path { get; private set; } = "";

    /// <summary>
    /// 开始记录。每次启动**截断重写** —— 手动复现时只关心这一次的日志，
    /// 混着上几次的会让"最后一行"这个判据失效。
    /// </summary>
    public static void Start()
    {
        if (Environment.GetEnvironmentVariable("ISLANDX_DIAG") != "1") return;

        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandX");
            Directory.CreateDirectory(dir);

            Path = System.IO.Path.Combine(dir, "diag.log");

            // FileShare.Read：记录期间要能用别的程序读它，否则得先退出应用才能看
            var stream = new FileStream(
                Path, FileMode.Create, FileAccess.Write, FileShare.Read);

            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            _clock = Stopwatch.StartNew();
            Enabled = true;

            Log($"=== IslandX 诊断日志 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            Log($"进程 {Environment.ProcessId} / .NET {Environment.Version}");
        }
        catch
        {
            // 日志开不起来不该拖垮应用
            Enabled = false;
        }
    }

    /// <summary>
    /// 记一行。带毫秒时间戳与线程号 —— 卡死的诱因常常是"两个线程凑一起"，
    /// 只看内容分不出是哪个线程在说话。
    /// </summary>
    public static void Log(string message)
    {
        if (!Enabled) return;

        lock (Gate)
        {
            if (_writer is null) return;
            if (_written > MaxBytes) return;

            var t = _clock?.Elapsed ?? TimeSpan.Zero;
            var line = $"[{t.TotalSeconds,9:F3}s T{Environment.CurrentManagedThreadId:D2}] {message}";

            try
            {
                _writer.WriteLine(line);
                _written += line.Length + 2;

                if (_written > MaxBytes)
                {
                    _writer.WriteLine("=== 超出上限，停止记录 ===");
                    _writer.Flush();
                }
            }
            catch { /* 写不进去就算了 */ }
        }
    }

    /// <summary>
    /// 计时器：记录一段代码耗了多久。只在超过阈值时写 ——
    /// 正常路径每步都记会把日志刷成噪音，而卡死时超时那一行自然会跳出来。
    /// </summary>
    public static void Timed(string what, Action action, int warnMs = 40)
    {
        if (!Enabled) { action(); return; }

        var sw = Stopwatch.StartNew();

        try
        {
            action();
        }
        finally
        {
            if (sw.ElapsedMilliseconds >= warnMs)
                Log($"⏱ {what} 耗时 {sw.ElapsedMilliseconds}ms");
        }
    }

    /// <summary>
    /// 看门狗：另一个线程每 500ms 向 UI 线程投一个空活儿，
    /// 2 秒内没回话就认定卡住了，并**持续每秒记一行**直到它恢复。
    ///
    /// 这是唯一能把"卡死"这件事本身记下来的办法 ——
    /// UI 线程卡住时它自己写不了日志，只有别的线程能。
    /// 恢复时也记一条，于是能读出"卡了多久"，区分"真死"与"卡了三秒"。
    /// </summary>
    public static void StartWatchdog(Dispatcher dispatcher)
    {
        if (!Enabled) return;

        var thread = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(500);

                // 不 using / 不 Dispose：UI 线程恢复后这个回调还会跑，
                // 那时对象已经释放就会抛。让 GC 收比自己管更安全
                var done = new ManualResetEventSlim();

                // Send 优先级：插到队列最前面。UI 线程只是"忙"的话它照样跑得到，
                // 只有真的卡在某个调用里才会超时 —— 这正是要区分的两件事
                dispatcher.InvokeAsync(() => done.Set(), DispatcherPriority.Send);

                if (done.Wait(2000)) continue;

                var stuck = Stopwatch.StartNew();
                Log($"!!! UI 线程无响应 已 2s");

                while (!done.Wait(1000))
                    Log($"!!! UI 线程无响应 已 {stuck.Elapsed.TotalSeconds + 2:F0}s");

                Log($"UI 线程恢复，本次共卡 {(stuck.Elapsed.TotalSeconds + 2):F1}s");
            }
        })
        {
            IsBackground = true,
            Name = "IslandX-Watchdog",
        };

        thread.Start();
        Log("看门狗已启动（每 500ms 探一次，2s 无响应即报）");
    }
}
