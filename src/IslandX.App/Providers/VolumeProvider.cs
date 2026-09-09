using System.Runtime.InteropServices;
using IslandX.Contracts;
using IslandX.Interop;

namespace IslandX.Providers;

/// <summary>
/// 系统音量：调音量或静音时浮出，几秒后自动退场。
///
/// 走 WASAPI 的端点音量回调而不是轮询 —— 轮询要 50ms 一跳才跟手，
/// 那是白挂在常驻占用上的开销；回调只在真的变化时才唤醒我们。
/// 键盘音量键、系统音量合成器、播放器自己调，三种来源都能捕获。
/// </summary>
public sealed class VolumeProvider : IIslandProvider
{
    private static readonly TimeSpan Dwell = TimeSpan.FromSeconds(2);

    private AudioInterop.IAudioEndpointVolume? _endpoint;

    /// <summary>回调对象必须由我们持有 —— 只交给 COM 的话会被 GC 掉，回调打到空 CCW 上。</summary>
    private VolumeCallback? _callback;

    /// <summary>
    /// 注册完成的时刻。用来忽略紧随注册之后的回调 ——
    /// 起初这里写的是"吞掉第一个回调"，但 RegisterControlChangeNotify 其实**不会**
    /// 在注册时回灌当前值，那个 flag 于是吞掉了用户第一次真实按键：
    /// 单按一次音量毫无反应，连按才从第二下开始出现。
    /// 换成时间窗口，既能挡住可能存在的初始回灌，又不会吃掉真实操作。
    /// </summary>
    private long _armedTicks;

    public string Id => "volume";

    public event Action<IslandActivity?>? ActivityChanged;

    public string? LastError { get; private set; }

    public Task StartAsync()
    {
        try
        {
            var enumerator = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumerator();

            var hr = enumerator.GetDefaultAudioEndpoint(
                AudioInterop.ERender, AudioInterop.EConsole, out var device);
            if (hr != 0) { LastError = $"endpoint:0x{hr:X8}"; return Task.CompletedTask; }

            var iid = AudioInterop.IID_IAudioEndpointVolume;
            hr = device.Activate(ref iid, 1 /* CLSCTX_INPROC_SERVER */, IntPtr.Zero, out var raw);
            if (hr != 0) { LastError = $"activate:0x{hr:X8}"; return Task.CompletedTask; }

            _endpoint = (AudioInterop.IAudioEndpointVolume)raw;
            _callback = new VolumeCallback(OnVolumeChanged);

            hr = _endpoint.RegisterControlChangeNotify(_callback);
            if (hr != 0) { LastError = $"register:0x{hr:X8}"; _endpoint = null; _callback = null; }
            else _armedTicks = Environment.TickCount64;
        }
        catch (Exception ex)
        {
            LastError = ex.GetType().Name;
            _endpoint = null;
            _callback = null;
        }

        return Task.CompletedTask;
    }

    public void Stop()
    {
        try
        {
            if (_endpoint is not null && _callback is not null)
                _endpoint.UnregisterControlChangeNotify(_callback);
        }
        catch { /* 退出路径不抛异常 */ }

        _endpoint = null;
        _callback = null;
        _armedTicks = 0;
    }

    private void OnVolumeChanged(bool muted, float level)
    {
        // 刚注册那一瞬间的回调（如果有）不算用户操作
        if (Environment.TickCount64 - _armedTicks < 400) return;

        var pct = (int)Math.Round(Math.Clamp(level, 0, 1) * 100);

        ActivityChanged?.Invoke(new IslandActivity
        {
            Id = muted ? "volume:muted" : "volume:level",
            ProviderId = Id,
            Priority = ActivityPriority.High,
            Glyph = GlyphFor(muted, pct),
            Title = muted ? "已静音" : "音量",
            Subtitle = muted ? null : $"{pct}%",
            Progress = muted ? 0 : pct / 100.0,
            AutoDismissAfter = Dwell,
        });
    }

    /// <summary>
    /// 扬声器四档 Volume0–Volume3 = E992–E995，静音 Mute = E74F。
    /// 一律写成 \u 转义，不把字符直接贴进源码 —— 那些码位在编辑器里不可见，
    /// 贴进去看着就是个空字符串，日后改动全凭运气。
    /// </summary>
    private static string GlyphFor(bool muted, int pct) => muted
        ? "\uE74F"
        : pct == 0 ? "\uE992"
        : pct < 34 ? "\uE993"
        : pct < 67 ? "\uE994"
        : "\uE995";

    /// <summary>
    /// COM 回调的接收端。单独一个类而不是让 Provider 自己实现接口 ——
    /// Provider 是 public 的，把 COM 接口挂在它身上会连带暴露出去。
    /// </summary>
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class VolumeCallback : AudioInterop.IAudioEndpointVolumeCallback
    {
        private readonly Action<bool, float> _onChanged;

        internal VolumeCallback(Action<bool, float> onChanged) => _onChanged = onChanged;

        public int OnNotify(IntPtr notifyData)
        {
            // 这里在系统线程上跑，抛异常会穿回原生代码，一律吞掉
            try
            {
                if (notifyData == IntPtr.Zero) return 0;

                var data = Marshal.PtrToStructure<AudioInterop.AudioVolumeNotificationData>(notifyData);
                _onChanged(data.Muted, data.MasterVolume);
            }
            catch { /* 回调不能让异常逃逸到 COM */ }

            return 0;
        }
    }
}
