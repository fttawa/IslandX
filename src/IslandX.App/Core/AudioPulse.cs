using System.Runtime.InteropServices;
using IslandX.Interop;

namespace IslandX.Core;

/// <summary>
/// 从系统音频输出（WASAPI 环回）持续读取能量，供频谱条随音乐律动。
///
/// 采集在后台 MTA 线程上跑，结果写进 volatile 字段，UI 线程每帧直接读。
/// 单写单读的 float，不需要锁。
///
/// 注：接口 GUID 全部照 Windows SDK 头文件抄写（Audioclient.h / mmdeviceapi.h）。
/// 凭记忆写会得到 E_NOINTERFACE 这种只在运行期暴露、且看不出所以然的错误。
/// </summary>
public sealed class AudioPulse : IDisposable
{
    /// <summary>峰值跟踪的衰减系数：让安静段落之后的增益慢慢恢复，而不是忽然放大噪声。</summary>
    private const double PeakDecay = 0.9992;

    /// <summary>峰值下限，避免除以一个极小值把底噪放大成满幅。</summary>
    private const double PeakFloor = 0.008;

    // 上升快、下降慢 —— 这是频谱类可视化的手感关键：跟得上敲击，又不会抽搐
    private const double RiseRate = 0.5;
    private const double FallRate = 0.11;

    private const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;

    /// <summary>频谱条数量。与参考实现一致 —— 再多在这个尺寸下也分辨不出来。</summary>
    public const int BandCount = 6;

    /// <summary>FFT 长度。48kHz 下约 21ms 一帧，bin 宽约 47Hz，够分出 6 个对数频段。</summary>
    private const int FftSize = 1024;

    /// <summary>各频段的上边界（Hz），按对数分布 —— 线性分布会让 5 根条全挤在高频。</summary>
    private static readonly double[] BandEdges = [120, 300, 700, 1600, 4000, 10000];

    private volatile float _level;
    private long _lastSignalTicks;

    /// <summary>整数组原子替换，读侧拿到的永远是一致的一帧。</summary>
    private volatile float[] _spectrum = new float[BandCount];

    // FFT 的累积缓冲与工作区（只在采集线程访问）
    private readonly double[] _fftBuffer = new double[FftSize];
    private readonly double[] _fftRe = new double[FftSize];
    private readonly double[] _fftIm = new double[FftSize];
    private readonly double[] _window = BuildHannWindow(FftSize);
    private readonly double[] _bandSmooth = new double[BandCount];
    private readonly double[] _bandPeak = new double[BandCount];
    private int _fftFill;
    private int _sampleRate = 48000;

    private Thread? _thread;
    private volatile bool _running;

    // 峰值跟踪的状态（只在采集线程访问）
    private double _peakLevel = PeakFloor;
    private double _smoothLevel;

    /// <summary>整体能量，0–1。频谱条不用它，留作诊断"到底有没有采到声音"。</summary>
    public float Level => _level;

    /// <summary>
    /// 六段频谱能量，0–1，已各自归一化并平滑。
    /// 返回的数组是只读快照，调用方不要改写。
    /// </summary>
    public float[] Spectrum => _spectrum;

    /// <summary>最近 1.5 秒内是否有过有效信号 —— 用来决定要不要持续渲染。</summary>
    public bool IsActive =>
        Environment.TickCount64 - Interlocked.Read(ref _lastSignalTicks) < 1500;

    /// <summary>采集是否可用。WASAPI 初始化失败时为 false，岛体照常工作、只是没有律动。</summary>
    public bool IsAvailable { get; private set; }

    /// <summary>最近一次失败的步骤与 HRESULT，用于诊断为什么没有律动。</summary>
    public string LastError { get; private set; } = "";

    private string _step = "init";

    public void Start()
    {
        if (_thread is not null) return;

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "IslandX.AudioPulse",
            Priority = ThreadPriority.BelowNormal,
        };

        // WASAPI 的 COM 对象在 MTA 上使用最省事
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>停止采集。可以再次 <see cref="Start"/> 重新开始。</summary>
    public void Stop()
    {
        _running = false;
        _thread = null;
        IsAvailable = false;

        // 归零，否则关闭律动后频谱条会停在最后一帧的高度上
        _smoothLevel = 0;
        _level = 0;
        Array.Clear(_bandSmooth);
        _spectrum = new float[BandCount];
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (_running)
        {
            try
            {
                CaptureSession();
            }
            catch (Exception ex)
            {
                LastError = $"{_step}:0x{ex.HResult:X8}";
                System.Diagnostics.Debug.WriteLine($"[audio] 采集中断于 {_step}: 0x{ex.HResult:X8} {ex.Message}");
            }

            // 设备被切换、独占占用或拔出都会让上面抛出；歇一下再重建
            if (_running) Thread.Sleep(1200);
        }
    }

    /// <summary>建立一次环回采集会话并读到出错为止（设备变化时靠外层重建）。</summary>
    private void CaptureSession()
    {
        _step = "create-enumerator";
        var enumerator = (AudioInterop.IMMDeviceEnumerator)new AudioInterop.MMDeviceEnumerator();

        _step = "get-endpoint";
        Check(enumerator.GetDefaultAudioEndpoint(
            AudioInterop.ERender, AudioInterop.EConsole, out var device));

        _step = "activate-client";
        var clientIid = AudioInterop.IID_IAudioClient;
        Check(device.Activate(ref clientIid, 0, IntPtr.Zero, out var clientObj));
        var client = (AudioInterop.IAudioClient)clientObj;

        _step = "mix-format";
        Check(client.GetMixFormat(out var formatPtr));

        int channels;
        bool isFloat;
        try
        {
            var format = Marshal.PtrToStructure<AudioInterop.WaveFormatEx>(formatPtr);
            channels = Math.Max(1, (int)format.Channels);
            _sampleRate = (int)Math.Max(8000, format.SamplesPerSec);

            // EXTENSIBLE 的实际格式在 SubFormat 里，但 32 位一律按 float 处理 ——
            // 现代 Windows 混音格式就是 32 位 float，16 位整数只出现在老配置里。
            isFloat = format.BitsPerSample == 32;
            if (format.BitsPerSample is not (16 or 32))
                throw new NotSupportedException($"未支持的采样位深 {format.BitsPerSample}");

            _step = $"initialize ch={channels} bits={(isFloat ? 32 : 16)}";
            // 100ms 缓冲，环回模式不需要低延迟
            Check(client.Initialize(
                AudioInterop.AUDCLNT_SHAREMODE_SHARED,
                AudioInterop.AUDCLNT_STREAMFLAGS_LOOPBACK,
                1_000_000, 0, formatPtr, IntPtr.Zero));
        }
        finally
        {
            AudioInterop.CoTaskMemFree(formatPtr);
        }

        _step = "get-capture-service";
        var captureIid = AudioInterop.IID_IAudioCaptureClient;
        Check(client.GetService(ref captureIid, out var captureObj));
        var capture = (AudioInterop.IAudioCaptureClient)captureObj;

        _step = "start";
        Check(client.Start());
        IsAvailable = true;

        try
        {
            while (_running)
            {
                _step = "packet-size";
                Check(capture.GetNextPacketSize(out var frames));

                if (frames == 0)
                {
                    // 没有新数据：可能是静音，让平滑值自然衰减
                    Decay();
                    Thread.Sleep(10);
                    continue;
                }

                while (frames > 0 && _running)
                {
                    Check(capture.GetBuffer(out var data, out var count, out var flags, out _, out _));

                    try
                    {
                        if (count > 0 && (flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0)
                            Analyze(data, count, channels, isFloat);
                        else
                            Decay();
                    }
                    finally
                    {
                        capture.ReleaseBuffer(count);
                    }

                    Check(capture.GetNextPacketSize(out frames));
                }
            }
        }
        finally
        {
            try { client.Stop(); } catch { }
            IsAvailable = false;
        }
    }

    private unsafe void Analyze(IntPtr data, uint frames, int channels, bool isFloat)
    {
        double sumSq = 0;

        if (isFloat)
        {
            var p = (float*)data;
            for (uint i = 0; i < frames; i++)
            {
                double s = 0;
                for (var c = 0; c < channels; c++) s += p[(i * channels) + c];
                s /= channels;

                sumSq += s * s;
                PushSample(s);
            }
        }
        else
        {
            var p = (short*)data;
            for (uint i = 0; i < frames; i++)
            {
                double s = 0;
                for (var c = 0; c < channels; c++) s += p[(i * channels) + c] / 32768.0;
                s /= channels;

                sumSq += s * s;
                PushSample(s);
            }
        }

        Publish(Math.Sqrt(sumSq / frames));
    }

    /// <summary>累积单声道采样，攒满一帧就做一次 FFT。</summary>
    private void PushSample(double sample)
    {
        _fftBuffer[_fftFill++] = sample;
        if (_fftFill < FftSize) return;

        _fftFill = 0;
        ComputeSpectrum();
    }

    private void ComputeSpectrum()
    {
        // 加 Hann 窗：不加窗的话每帧边界的突变会在频谱里糊出一片旁瓣
        for (var i = 0; i < FftSize; i++)
        {
            _fftRe[i] = _fftBuffer[i] * _window[i];
            _fftIm[i] = 0;
        }

        Fft(_fftRe, _fftIm);

        var snapshot = new float[BandCount];
        var binHz = _sampleRate / (double)FftSize;
        var lowBin = Math.Max(1, (int)(40 / binHz));   // 40Hz 以下多是直流与噪声

        for (var band = 0; band < BandCount; band++)
        {
            var hiBin = Math.Min(FftSize / 2 - 1, (int)(BandEdges[band] / binHz));
            if (hiBin < lowBin) hiBin = lowBin;

            double sum = 0;
            for (var bin = lowBin; bin <= hiBin; bin++)
            {
                var re = _fftRe[bin];
                var im = _fftIm[bin];
                sum += Math.Sqrt((re * re) + (im * im));
            }

            var magnitude = sum / (hiBin - lowBin + 1);
            lowBin = hiBin + 1;

            // 每段单独跟踪峰值：高频能量天然比低频低一个数量级，
            // 用同一个增益的话高频那几根永远趴着不动。
            _bandPeak[band] = Math.Max(_bandPeak[band] * PeakDecay, magnitude);
            var target = Math.Clamp(magnitude / Math.Max(_bandPeak[band], 1e-4), 0, 1);

            _bandSmooth[band] += (target - _bandSmooth[band])
                * (target > _bandSmooth[band] ? RiseRate : FallRate);

            snapshot[band] = (float)_bandSmooth[band];
        }

        _spectrum = snapshot;
    }

    private static double[] BuildHannWindow(int size)
    {
        var w = new double[size];
        for (var i = 0; i < size; i++) w[i] = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (size - 1)));
        return w;
    }

    /// <summary>原地 radix-2 Cooley–Tukey FFT。</summary>
    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;

        // 位反转置换
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = -2 * Math.PI / len;
            var wRe = Math.Cos(angle);
            var wIm = Math.Sin(angle);
            var half = len >> 1;

            for (var i = 0; i < n; i += len)
            {
                double curRe = 1, curIm = 0;

                for (var j = 0; j < half; j++)
                {
                    var uRe = re[i + j];
                    var uIm = im[i + j];
                    var vRe = (re[i + j + half] * curRe) - (im[i + j + half] * curIm);
                    var vIm = (re[i + j + half] * curIm) + (im[i + j + half] * curRe);

                    re[i + j] = uRe + vRe;
                    im[i + j] = uIm + vIm;
                    re[i + j + half] = uRe - vRe;
                    im[i + j + half] = uIm - vIm;

                    var nextRe = (curRe * wRe) - (curIm * wIm);
                    curIm = (curRe * wIm) + (curIm * wRe);
                    curRe = nextRe;
                }
            }
        }
    }

    /// <summary>
    /// 归一化 + 平滑。
    /// 归一化用缓慢衰减的峰值跟踪而不是固定增益 —— 否则小音量时频谱条几乎不动、
    /// 大音量时又一直顶满，观感完全取决于系统音量。
    /// </summary>
    private void Publish(double rms)
    {
        _peakLevel = Math.Max(_peakLevel * PeakDecay, rms);

        var targetLevel = Math.Clamp(rms / Math.Max(_peakLevel, PeakFloor), 0, 1);
        _smoothLevel += (targetLevel - _smoothLevel) * (targetLevel > _smoothLevel ? RiseRate : FallRate);
        _level = (float)_smoothLevel;

        // 只有真的有声音才算活跃，静音时让渲染循环得以停下
        if (rms > 0.0015) Interlocked.Exchange(ref _lastSignalTicks, Environment.TickCount64);
    }

    private void Decay()
    {
        _smoothLevel *= 1 - FallRate;
        _level = (float)_smoothLevel;

        var snapshot = new float[BandCount];
        for (var i = 0; i < BandCount; i++)
        {
            _bandSmooth[i] *= 1 - FallRate;
            snapshot[i] = (float)_bandSmooth[i];
        }

        _spectrum = snapshot;
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }
}
