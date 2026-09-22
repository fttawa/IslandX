using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

// 动画连拍器：在岛体某个动画**起始的那一刻**开始，按固定节拍连拍若干帧。
//
// 为什么不用 PowerShell：
//
// 1. 时基。PowerShell 的 while 忙等 / Start-Sleep 产生的间隔漂移到几十毫秒，
//    而被拍的动画只有 200–350ms —— 拍出来的帧之间时间不可比，
//    "第 3 帧应该在 126ms" 完全对不上。这个坑在本项目里踩过两次，
//    两次都得出了错误结论。计时必须在 C# 里。
// 2. 程序集。.NET 10 上 Add-Type 拼不起 System.Drawing 的依赖链（见 csproj 注释）。
//
// 为什么不用固定延迟起拍：动画由播放器/定时器触发，与拍摄没有共同时基。
// 上一轮用「换歌后 30/90/150ms」连拍，结果一帧转场都没抓到 ——
// GSMTC 事件本身有 270–370ms 的不定延迟，而转场只有 170ms，窗口根本对不上。
// 所以这里先高频轮询探针，看到指定字段的计数变了才起拍。
//
// 用法:
//   ShotBurst <输出目录> <字段名> <x> <y> <宽> <高> [帧数=9] [间隔ms=42]
//
//   <字段名> 是探针标题里的计数字段，出现变化即视为动画开始。例如
//     lsn  歌词换句次数
//     n    切歌转场次数
//
// 每帧存成 fNN.png，并把该帧时刻的 lsw / swap / split 探针读数打到 stdout ——
// 图与数字同一时刻取，才能确认"看到的这一帧"对应哪个动画进度。

// 即拍模式：不等任何事件，立刻截一张指定矩形。
// 存在的理由是托盘菜单 —— 它不在岛体的探针里，等不到"计数变化"，
// 而 PowerShell 那边只能做 user32 的活（.NET 10 上 Add-Type 拼不起 System.Drawing，
// 本项目踩过三次），截图这一步必须回到真项目里来。
// 生成应用图标。跑一次、把产物提交进仓库即可，不是构建步骤 ——
// 图标是会被人看的东西，应当能在版本控制里看到它变了没有，
// 而不是每次构建都重新生成一个"应该一样"的文件。
//
// 形状沿用托盘图标那个 identity（黑底 + 亮胶囊），但**反了过来**：
// 托盘图标画在系统托盘的固定背景上，黑胶囊看得清；
// 而开始菜单／任务栏的底色深浅都有，纯黑形状在深色主题上会整个消失。
// 所以这里给一个近黑的圆角方块当"应用砖块"，把胶囊做成亮色放在里面。
if (args.Length >= 2 && args[0] == "--makeicon")
{
    var iconPath = args[1];
    int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    var pngs = new List<byte[]>();

    foreach (var size in sizes)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 圆角方块底。留 1/16 边距，免得贴边被系统再裁一次
            var pad = size / 16f;
            var side = size - pad * 2;
            var radius = side * 0.24f;

            using (var tile = RoundedRect(pad, pad, side, side, radius))
            using (var fill = new SolidBrush(Color.FromArgb(255, 14, 14, 18)))
                g.FillPath(fill, tile);

            // 胶囊。宽 58%、高 22%，居中 —— 16px 下高度约 3.5px，
            // 再细就糊成一条灰线了
            var capW = size * 0.58f;
            var capH = size * 0.22f;
            var capX = (size - capW) / 2f;
            var capY = (size - capH) / 2f;

            using (var cap = RoundedRect(capX, capY, capW, capH, capH / 2f))
            using (var light = new SolidBrush(Color.FromArgb(255, 242, 243, 247)))
                g.FillPath(light, cap);

            // 摄像头那个小孔。32px 以下画不出来，画了只会让胶囊看着脏
            if (size >= 32)
            {
                var dot = capH * 0.46f;
                using var hole = new SolidBrush(Color.FromArgb(255, 14, 14, 18));
                g.FillEllipse(hole, capX + capH * 0.30f, capY + (capH - dot) / 2f, dot, dot);
            }
        }

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        pngs.Add(ms.ToArray());
    }

    // ICO 容器。每一帧都存成 PNG —— Vista 起就支持，省去写 BMP + AND 掩码那一套
    using (var fs = new FileStream(iconPath, FileMode.Create, FileAccess.Write))
    using (var ico = new BinaryWriter(fs))
    {
        ico.Write((short)0);              // reserved
        ico.Write((short)1);              // type = icon
        ico.Write((short)sizes.Length);

        var offset = 6 + 16 * sizes.Length;

        for (var i = 0; i < sizes.Length; i++)
        {
            // 宽高字段是一个字节，256 要写成 0
            ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            ico.Write((byte)0);           // 调色板数
            ico.Write((byte)0);           // reserved
            ico.Write((short)1);          // planes
            ico.Write((short)32);         // 位深
            ico.Write(pngs[i].Length);
            ico.Write(offset);
            offset += pngs[i].Length;
        }

        foreach (var png in pngs) ico.Write(png);
    }

    Console.WriteLine($"已生成 {iconPath}：{sizes.Length} 个尺寸（{string.Join(", ", sizes)}），"
        + $"{new FileInfo(iconPath).Length} 字节");
    return 0;
}

static System.Drawing.Drawing2D.GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
{
    var path = new System.Drawing.Drawing2D.GraphicsPath();
    var d = r * 2;

    path.AddArc(x, y, d, d, 180, 90);
    path.AddArc(x + w - d, y, d, d, 270, 90);
    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    path.AddArc(x, y + h - d, d, d, 90, 90);
    path.CloseFigure();

    return path;
}

if (args.Length >= 6 && args[0] == "--rect")
{
    var path = args[1];
    Native.Shot(int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), path);
    Console.WriteLine($"已截图: {path}");
    return 0;
}

// 无触发连拍：立刻开始，按固定节拍拍若干帧，并拼成对照图。
// 与默认模式的区别只是"不等探针字段变化" —— 设置窗口不在探针里，
// 而它的开关动画只有 240ms，靠反复调 --rect 是拍不到的（每次进程启动就要几百毫秒）。
if (args.Length >= 6 && args[0] == "--burst")
{
    var bdir = args[1];
    var bx = int.Parse(args[2]);
    var by = int.Parse(args[3]);
    var bw = int.Parse(args[4]);
    var bh = int.Parse(args[5]);
    var bframes = args.Length > 6 ? int.Parse(args[6]) : 10;
    var bstep = args.Length > 7 ? double.Parse(args[7]) : 30;

    Directory.CreateDirectory(bdir);

    var bshots = new List<(Bitmap Image, string Label)>();
    var bclock = Stopwatch.StartNew();

    for (var i = 0; i < bframes; i++)
    {
        var due = i * bstep;
        while (bclock.Elapsed.TotalMilliseconds < due) Thread.Sleep(0);

        var at = bclock.Elapsed.TotalMilliseconds;
        var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bx, by, 0, 0, new Size(bw, bh), CopyPixelOperation.SourceCopy);
        }

        bmp.Save(Path.Combine(bdir, $"f{i:D2}.png"), ImageFormat.Png);
        bshots.Add((bmp, $"+{at:F0}ms"));
    }

    WriteStrip(Path.Combine(bdir, "strip.png"), bshots);
    foreach (var (img, _) in bshots) img.Dispose();

    Console.WriteLine($"已存到 {bdir}（{bframes} 帧，含 strip.png）");
    return 0;
}

if (args.Length < 6)
{
    Console.WriteLine("用法: ShotBurst <输出目录> <字段名> <x> <y> <宽> <高> [帧数] [间隔ms]");
    Console.WriteLine("      ShotBurst --rect  <输出png> <x> <y> <宽> <高>            # 即拍一张");
    Console.WriteLine("      ShotBurst --burst <输出目录> <x> <y> <宽> <高> [帧数] [间隔ms]  # 无触发连拍");
    return 1;
}

var dir = args[0];
var field = args[1];
var x = int.Parse(args[2]);
var y = int.Parse(args[3]);
var w = int.Parse(args[4]);
var h = int.Parse(args[5]);
var frames = args.Length > 6 ? int.Parse(args[6]) : 9;
var stepMs = args.Length > 7 ? double.Parse(args[7]) : 42;

Directory.CreateDirectory(dir);

var island = Process.GetProcessesByName("IslandX").FirstOrDefault();
if (island is null)
{
    Console.WriteLine("IslandX 没在运行");
    return 1;
}

var hwnd = Native.FindProbeWindow(island.Id);
if (hwnd == IntPtr.Zero)
{
    Console.WriteLine("找不到探针窗口 —— 需要 Debug 构建（Release 不写窗口标题）");
    return 1;
}

// 鼠标挪开，否则悬停会把岛体撑大、还挡住内容
Native.SetCursorPos(40, 1300);

var before = Counter(Native.WindowText(hwnd), field);
if (before < 0)
{
    Console.WriteLine($"探针里没有 |{field} 字段");
    return 1;
}

Console.WriteLine($"等 |{field} 从 {before} 变化…");

var wait = Stopwatch.StartNew();
while (wait.ElapsedMilliseconds < 8000 && Counter(Native.WindowText(hwnd), field) == before)
{
    Thread.Sleep(2);
}

if (Counter(Native.WindowText(hwnd), field) == before)
{
    Console.WriteLine("8 秒内没等到动画，放弃");
    return 1;
}

// 探针自己限流到 20fps，所以"看到"变化最多滞后 50ms —— 起拍点因此不是 t=0。
// 这个偏差是已知的、也是无法消除的（提高探针频率会反过来干扰被测动画），
// 所以第一帧标注的是相对起拍的时刻，不假装它就是动画的 0ms。
var clock = Stopwatch.StartNew();
var shots = new List<(Bitmap Image, string Label)>();

for (var i = 0; i < frames; i++)
{
    var due = i * stepMs;
    while (clock.Elapsed.TotalMilliseconds < due) Thread.Sleep(0);

    var at = clock.Elapsed.TotalMilliseconds;

    var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
    using (var g = Graphics.FromImage(bmp))
    {
        g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
    }

    bmp.Save(Path.Combine(dir, $"f{i:D2}.png"), ImageFormat.Png);

    var title = Native.WindowText(hwnd);
    var probe = Field(title, "lsw");
    Console.WriteLine($"  f{i:D2}  +{at,5:F0}ms   {probe}");

    shots.Add((bmp, $"+{at:F0}ms   {probe}"));
}

// 竖排拼一张对照图。逐帧看图很难判断"动画对不对"——
// 差别在相邻帧之间，而那要靠来回切换窗口去比。拼起来一眼就能看出
// 两句有没有错开、模糊有没有真的在退、宽度是哪一帧开始收的。
WriteStrip(Path.Combine(dir, "strip.png"), shots);

foreach (var (image, _) in shots) image.Dispose();

Console.WriteLine($"已存到 {dir}（含 strip.png 对照图）");
return 0;

static void WriteStrip(string path, List<(Bitmap Image, string Label)> shots)
{
    if (shots.Count == 0) return;

    var frameW = shots[0].Image.Width;
    var frameH = shots[0].Image.Height;

    using var font = new Font("Consolas", 10.5f);

    // 标注区按**最长的那条**算宽度，不写死。探针字段是会加的 ——
    // 写死一个值，某次加了字段之后标注就会溢出到画面上、把被测的东西盖住，
    // 而那恰恰是最需要看清的部分。
    int labelWidth;
    using (var probe = new Bitmap(1, 1))
    using (var pg = Graphics.FromImage(probe))
    {
        var widest = 0f;
        foreach (var (_, label) in shots)
            widest = Math.Max(widest, pg.MeasureString(label, font).Width);

        labelWidth = (int)Math.Ceiling(widest) + 24;
    }

    using var strip = new Bitmap(labelWidth + frameW, frameH * shots.Count, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(strip);
    using var dim = new SolidBrush(Color.FromArgb(0xB0, 0xB0, 0xB0));

    g.Clear(Color.FromArgb(0x12, 0x12, 0x12));

    for (var i = 0; i < shots.Count; i++)
    {
        var top = i * frameH;
        g.DrawImage(shots[i].Image, labelWidth, top);
        g.DrawString(shots[i].Label, font, dim, 8, top + (frameH / 2f) - 8);
    }

    strip.Save(path, ImageFormat.Png);
}

// ===== 探针字段解析 =====

static int Counter(string title, string name)
{
    var marker = $"|{name}";
    var i = title.IndexOf(marker, StringComparison.Ordinal);
    if (i < 0) return -1;

    var j = i + marker.Length;
    var value = 0;
    var any = false;

    while (j < title.Length && char.IsAsciiDigit(title[j]))
    {
        value = (value * 10) + (title[j] - '0');
        j++;
        any = true;
    }

    return any ? value : -1;
}

static string Field(string title, string name)
{
    var marker = $"|{name}";
    var i = title.IndexOf(marker, StringComparison.Ordinal);
    if (i < 0) return "?";

    var start = i + marker.Length;
    var end = title.IndexOf('|', start);
    return end < 0 ? title[start..] : title[start..end];
}

// 用老式 DllImport 而不是 LibraryImport：后者要求整个项目开 AllowUnsafeBlocks，
// 而这里只是四个 user32 调用，不值得为它放宽一个工具项目的编译约束。
internal static class Native
{
    private delegate bool EnumProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc callback, IntPtr param);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int max);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    /// <summary>截一张屏幕矩形存成 PNG。</summary>
    public static void Shot(int x, int y, int w, int h, string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        bmp.Save(full, ImageFormat.Png);
    }

    public static string WindowText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(2400);
        var len = GetWindowTextW(hwnd, buffer, buffer.Capacity);
        return len <= 0 ? "" : buffer.ToString();
    }

    public static IntPtr FindProbeWindow(int pid)
    {
        var found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var owner);
            if (owner != (uint)pid) return true;

            if (!WindowText(hwnd).StartsWith("IslandX|", StringComparison.Ordinal)) return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }
}
