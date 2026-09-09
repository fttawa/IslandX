using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using IslandX.Contracts;
using IslandX.Interop;

namespace IslandX.Providers;

/// <summary>
/// 剪贴板：复制文本或截图后浮出一条确认，几秒后退场。
///
/// 截图之所以走这条路而不是去钩 PrintScreen —— Win+Shift+S、PrtSc、各家截图工具
/// 最终都会把图片放进剪贴板，盯剪贴板一处就能全覆盖，而钩键只认得一种。
///
/// 用 <c>AddClipboardFormatListener</c> 而不是老的剪贴板查看器链
/// （SetClipboardViewer）：后者要求每个监听者转发消息给下一个，
/// 链上任何一个程序崩了或没转发，后面的全部收不到。
/// </summary>
public sealed class ClipboardProvider : IIslandProvider
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2.5);

    /// <summary>标题里最多显示这么多字符的文本预览。</summary>
    private const int PreviewLength = 28;

    private HwndSource? _source;

    // 下面几个诊断量刻意**不加** #if DEBUG：一共三个 int 加一个字符串，
    // 开销可忽略，而"剪贴板读失败"恰恰是那种线上看不见、出问题又必须能解释的现象。
    // 之前包在 DEBUG 里，还让 Release 编译出"ex 声明了但未使用"的警告。

    /// <summary>收到 WM_CLIPBOARDUPDATE 的次数 —— 用来区分"消息没来"与"读取失败"。</summary>
    internal static int DebugMessageCount;

    /// <summary>读剪贴板抛异常的次数。刚被别的进程写完时经常打不开，属常态。</summary>
    internal static int DebugReadFailCount;

    /// <summary>发布出去的活动数。</summary>
    internal static int DebugPublishCount;

    internal static string DebugLastError = "-";

    public string Id => "clipboard";

    public event Action<IslandActivity?>? ActivityChanged;

    public Task StartAsync()
    {
        // 需要一个窗口句柄来收 WM_CLIPBOARDUPDATE。用消息窗口而不是借岛体的 HWND：
        // 借用的话这个 Provider 就和渲染层绑死了，架构上 Provider 不该知道岛体存在。
        _source = new HwndSource(new HwndSourceParameters("IslandX.ClipboardSink")
        {
            WindowStyle = 0,
            ParentWindow = new IntPtr(-3),   // HWND_MESSAGE：纯消息窗口，不上屏
        });

        _source.AddHook(OnMessage);
        var ok = NativeMethods.AddClipboardFormatListener(_source.Handle);
        DebugLastError = ok
            ? $"listening:{_source.Handle.ToInt64():X}"
            : $"listen-failed:{Marshal.GetLastWin32Error()}";
        return Task.CompletedTask;
    }

    public void Stop()
    {
        if (_source is null) return;

        try
        {
            NativeMethods.RemoveClipboardFormatListener(_source.Handle);
            _source.RemoveHook(OnMessage);
            _source.Dispose();
        }
        catch { /* 退出路径不抛异常 */ }

        _source = null;
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE) return IntPtr.Zero;

        DebugMessageCount++;
        Publish();
        return IntPtr.Zero;
    }

    private void Publish()
    {
        try
        {
            // 剪贴板可能正被别的进程占用，读失败是常态，不是错误
            if (Clipboard.ContainsImage())
            {
                PublishImage();
                return;
            }

            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return;
                PublishText(text);
                return;
            }

            if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                if (files.Count == 0) return;
                DebugPublishCount++;

                ActivityChanged?.Invoke(new IslandActivity
                {
                    Id = "clip:files",
                    ProviderId = Id,
                    Priority = ActivityPriority.High,
                    Glyph = "\uE8B7",   // Folder
                    Title = files.Count == 1 ? "已复制文件" : $"已复制 {files.Count} 个文件",
                    Subtitle = System.IO.Path.GetFileName(files[0]),
                    AutoDismissAfter = Dwell,
                });
            }
        }
        catch (Exception ex)
        {
            // 剪贴板被占用 / 格式异常 —— 静默跳过这一次
            DebugReadFailCount++;
            DebugLastError = ex.GetType().Name;
        }
    }

    private void PublishImage()
    {
        BitmapSource? thumb = null;
        string? size = null;

        try
        {
            if (Clipboard.GetImage() is { } image)
            {
                size = $"{image.PixelWidth} × {image.PixelHeight}";

                // 缩略图缩到岛体实际用得上的尺寸再冻结：
                // 原图可能是 4K 截图，整张挂在活动上纯属浪费内存
                var scale = Math.Min(1.0, 96.0 / Math.Max(image.PixelWidth, image.PixelHeight));
                var scaled = scale < 1.0
                    ? new TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale))
                    : (BitmapSource)image;

                scaled.Freeze();
                thumb = scaled;
            }
        }
        catch { /* 取图失败就只显示文字 */ }
        DebugPublishCount++;

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = "clip:image",
            ProviderId = Id,
            Priority = ActivityPriority.High,
            Glyph = "\uEB9F",   // Photo
            Title = "已截图",
            Subtitle = size,
            Artwork = thumb,
            AutoDismissAfter = Dwell,
        });
    }

    private void PublishText(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        var preview = flat.Length <= PreviewLength ? flat : flat[..PreviewLength] + "…";
        DebugPublishCount++;

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = "clip:text",
            ProviderId = Id,
            Priority = ActivityPriority.High,
            Glyph = "\uE8C8",   // Copy
            Title = "已复制",
            Subtitle = preview,
            AutoDismissAfter = Dwell,
        });
    }
}
