using System.Runtime.InteropServices;

namespace IslandX.Interop;

/// <summary>
/// WASAPI 环回采集所需的最小 COM 定义。
/// 手写而非引入 NAudio：只用到四个接口、十来个方法，不值得为此加一个依赖。
/// 方法声明顺序必须与 vtable 严格一致，不用的方法也得占位。
/// </summary>
internal static partial class AudioInterop
{
    internal const int AUDCLNT_SHAREMODE_SHARED = 0;
    internal const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;

    /// <summary>WAVE_FORMAT_IEEE_FLOAT</summary>
    internal const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;

    /// <summary>WAVE_FORMAT_EXTENSIBLE —— 现代混音格式基本都是这个，实际采样格式看 SubFormat。</summary>
    internal const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;

    /// <summary>eRender：我们要抓的是"播放出去的声音"，所以取渲染端点做环回。</summary>
    internal const int ERender = 0;

    /// <summary>eConsole</summary>
    internal const int EConsole = 0;

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    internal struct WaveFormatEx
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSec;
        internal uint AvgBytesPerSec;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort CbSize;
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);

        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        int RegisterEndpointNotificationCallback(IntPtr client);

        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        int OpenPropertyStore(int access, out IntPtr properties);

        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        int GetState(out int state);
    }

    [ComImport]
    [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioClient
    {
        int Initialize(int shareMode, uint streamFlags, long bufferDuration,
            long periodicity, IntPtr format, IntPtr audioSessionGuid);

        int GetBufferSize(out uint bufferFrames);

        int GetStreamLatency(out long latency);

        int GetCurrentPadding(out uint padding);

        int IsFormatSupported(int shareMode, IntPtr format, IntPtr closestMatch);

        int GetMixFormat(out IntPtr format);

        int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

        int Start();

        int Stop();

        int Reset();

        int SetEventHandle(IntPtr handle);

        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport]
    [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioCaptureClient
    {
        int GetBuffer(out IntPtr data, out uint frames, out uint flags,
            out ulong devicePosition, out ulong performanceCounter);

        int ReleaseBuffer(uint frames);

        int GetNextPacketSize(out uint frames);
    }

    /// <summary>
    /// 端点音量。方法顺序照 endpointvolume.h 的 vtable 抄，一个都不能少、不能换 ——
    /// 用不到的也要占位，否则调用会跳到错误的槽位上。
    /// </summary>
    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);

        int GetChannelCount(out uint count);

        int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);

        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);

        int GetMasterVolumeLevel(out float levelDb);

        int GetMasterVolumeLevelScalar(out float level);

        int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);

        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);

        int GetChannelVolumeLevel(uint channel, out float levelDb);

        int GetChannelVolumeLevelScalar(uint channel, out float level);

        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);

        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);

        int GetVolumeStepInfo(out uint step, out uint stepCount);

        int VolumeStepUp(ref Guid eventContext);

        int VolumeStepDown(ref Guid eventContext);

        int QueryHardwareSupport(out uint hardwareSupportMask);

        int GetVolumeRange(out float minDb, out float maxDb, out float incrementDb);
    }

    /// <summary>
    /// 音量变化回调。由 CLR 生成 CCW 交给系统调用，实现类必须保持存活 ——
    /// 被 GC 掉的话系统回调会打到已释放的 CCW 上。
    /// </summary>
    [ComImport]
    [Guid("657804FA-D6AD-4496-8A60-352752AF4F89")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr notifyData);
    }

    /// <summary>
    /// AUDIO_VOLUME_NOTIFICATION_DATA 的前三个字段。
    /// 后面还有 nChannels 与变长的通道音量数组，我们用不到。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct AudioVolumeNotificationData
    {
        internal Guid EventContext;

        [MarshalAs(UnmanagedType.Bool)]
        internal bool Muted;

        internal float MasterVolume;
    }

    internal static Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48A0-A4DE-185C395CD317");
    internal static Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr ptr);
}
