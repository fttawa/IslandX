using System.Globalization;
using IslandX.Contracts;

namespace IslandX.Providers;

/// <summary>
/// 兜底活动：没有任何系统事件时岛体显示时间。优先级 Low，
/// 媒体一开始播放就会被顶掉 —— 这同时是优先级仲裁的活体验证。
/// </summary>
public sealed class ClockProvider : IIslandProvider
{
    private static readonly CultureInfo Chinese = new("zh-CN");
    private System.Threading.Timer? _timer;

    public string Id => "clock";

    public event Action<IslandActivity?>? ActivityChanged;

    public Task StartAsync()
    {
        _timer = new System.Threading.Timer(_ => Publish(), null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        return Task.CompletedTask;
    }

    public void Stop()
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _timer?.Dispose();
        _timer = null;
    }

    private void Publish()
    {
        var now = DateTime.Now;

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = "clock:now",
            ProviderId = "clock",
            Priority = ActivityPriority.Low,
            Glyph = "\uE823",   // Segoe Fluent Icons: 时钟
            Title = now.ToString("HH:mm", Chinese),
            Subtitle = now.ToString("M月d日 dddd", Chinese),
            // 当前小时已过去的比例
            Progress = ((now.Minute * 60) + now.Second) / 3600.0,
        });
    }
}
