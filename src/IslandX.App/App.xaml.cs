using System.Diagnostics;
using System.IO;
using System.Windows;
using IslandX.Contracts;
using IslandX.Core;
using IslandX.Providers;
using IslandX.Tray;

namespace IslandX;

public partial class App : Application
{
    private const string SingleInstanceName = @"Global\IslandX.SingleInstance.v1";

    private Mutex? _singleInstance;
    private IslandWindow? _island;
    private TrayIconHost? _tray;
    private ActivityScheduler? _scheduler;
    private MediaSessionProvider? _media;
    private WeatherProvider? _weather;
    private PhiraProvider? _phira;
    private PowerProvider? _power;
    private ClockProvider? _clock;
    private AudioPulse? _audio;
    private AppConfig? _config;

    /// <summary>可运行时开关的 Provider，按 Id 索引。常驻的时钟与媒体不在其中。</summary>
    private readonly Dictionary<string, IIslandProvider> _toggleable = [];

    /// <summary>Provider 在托盘菜单里的显示名。</summary>
    private readonly Dictionary<string, string> _providerLabels = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 诊断日志（ISLANDX_DIAG=1 才开，见 tools/diag.ps1）。
        // 尽可能早 —— 卡死也可能发生在启动阶段
        Diag.Start();

        // 卡住时 UI 线程自己写不了日志，只有别的线程能。看门狗补的就是这个缺口
        Diag.StartWatchdog(Dispatcher);

        DispatcherUnhandledException += (_, args) =>
        {
            // 只记录、不吞 —— 吞掉会把"崩溃"变成"莫名其妙的卡住"，
            // 那比崩溃更难查
            Diag.Log($"!!! UI 未处理异常: {args.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Diag.Log($"!!! 后台未处理异常: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
            Diag.Log($"!!! 未观察的 Task 异常: {args.Exception}");

        _singleInstance = new Mutex(true, SingleInstanceName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已有实例在跑，静默退出，不弹窗打扰
            Shutdown();
            return;
        }

        _config = AppConfig.Load();

        _island = new IslandWindow
        {
            // 必须在 SourceInitialized 之前设好 —— 热键就是在那里注册的
            PreferredExpandHotkey = _config.ExpandHotkey,
            PreferredDotHotkey = _config.DotHotkey,
        };

        _media = new MediaSessionProvider { TranslationMode = _config.LyricTranslation };
        if (_config.LyricsEnabled) _media.SetLyricsEnabled(true);
        _island.OnPrevious = () => _media.SkipPreviousAsync();
        _island.OnTogglePlay = () => _media.TogglePlayPauseAsync();
        _island.OnNext = () => _media.SkipNextAsync();

        // 音频能量源：让频谱条随音乐律动。采集失败不影响其余功能
        _audio = new AudioPulse();
        if (_config.PulseEnabled) _audio.Start();
        _island.BindAudio(_audio);

        _scheduler = new ActivityScheduler();
        _clock = new ClockProvider();
        _scheduler.Register(_clock);
        _scheduler.Register(_media);

        // 瞬时事件 Provider：浮现几秒自动退场，可在托盘里逐个关掉
        _power = new PowerProvider();
        RegisterToggleable(_power, "电源");
        RegisterToggleable(new VolumeProvider(), "音量");
        RegisterToggleable(new ClipboardProvider(), "剪贴板 / 截图");

        // 天气不进 _toggleable：它要定位 + 联网，属于该由用户显式开启的那一类，
        // 和其余"显示哪些事件"的开关不是一回事（那些默认全开）。
        // 它的开关是 WeatherEnabled，默认 false，和歌词一样兼作隐私开关。
        _weather = new WeatherProvider
        {
            ManualLatitude = _config.Latitude,
            ManualLongitude = _config.Longitude,
            ManualRegion = _config.WeatherRegion,
        };
        _scheduler.Register(_weather);

        // Phira 成绩提醒。和天气同一类：默认关闭、由用户显式开启，
        // 所以也不进 _toggleable（那一组默认全开）。
        // 它不联网，代价是"读另一个程序的存档"+ 常驻一个进程轮询
        _phira = new PhiraProvider();
        _scheduler.Register(_phira);

        _island.BindScheduler(_scheduler);
        if (_config.DotMode) _island.SetDotMode(true);
        _island.Show();

        // 托盘与设置窗口共用这一个对象。两处各接一遍的话，加一个开关就得改两处，
        // 而漏改的那处不会有编译错误 —— 只会表现为"从一处改了、另一处没跟着变"
        var bridge = new SettingsBridge
        {
            Config = _config,

            ToggleExpanded = () => _island.ToggleExpanded(),
            Exit = Shutdown,

            SetDotMode = enabled =>
            {
                _island.SetDotMode(enabled);
                _config.DotMode = enabled;
                _config.Save();
            },

            SetPulse = enabled =>
            {
                if (enabled) _audio?.Start();
                else _audio?.Stop();
                _config.PulseEnabled = enabled;
                _config.Save();
            },

            SetLyrics = enabled =>
            {
                _media.SetLyricsEnabled(enabled);
                _config.LyricsEnabled = enabled;
                _config.Save();
            },

            SetLyricTranslation = mode =>
            {
                _media.TranslationMode = mode;   // setter 会立刻重发，不用等下一句
                _config.LyricTranslation = mode;
                _config.Save();
            },

            SetWeather = enabled =>
            {
                _config.WeatherEnabled = enabled;
                _config.Save();

                if (enabled)
                {
                    _ = _weather.StartAsync();
                }
                else
                {
                    _weather.Stop();
                    // Stop 只是不再发新活动，正在显示的那条要单独撤掉
                    _scheduler.Clear(_weather.Id);
                }
            },

            SetPhira = enabled =>
            {
                _config.PhiraEnabled = enabled;
                _config.Save();

                if (enabled)
                {
                    _ = _phira.StartAsync();
                }
                else
                {
                    _phira.Stop();
                    // Stop 只是不再发新活动，正在显示的那条要单独撤掉
                    _scheduler.Clear(_phira.Id);
                }
            },

            SetLocation = (lat, lon, region) =>
            {
                _config.Latitude = lat;
                _config.Longitude = lon;
                _config.WeatherRegion = region;
                _config.Save();

                // 位置换了要立刻重新定位，否则得等最长 6 小时的重定位周期
                _weather.ManualLatitude = lat;
                _weather.ManualLongitude = lon;
                _weather.ManualRegion = region;
                _weather.InvalidateLocation();
            },

            HotkeyLabel = kind => kind == HotkeyKind.Expand
                ? _island.ExpandHotkeyLabel
                : _island.DotHotkeyLabel,

            SetHotkey = (kind, text) => _island.TrySetHotkey(kind, text),
            SubscribeHotkeyFired = handler => _island.HotkeyFired += handler,
            UnsubscribeHotkeyFired = handler => _island.HotkeyFired -= handler,

            SetAutoStart = AutoStart.Set,
            AutoStartEnabled = () => AutoStart.IsEnabled,

            SetStartMenu = StartMenu.Set,
            StartMenuRegistered = () => StartMenu.IsRegistered,

            OpenConfigFolder = OpenConfigFolder,
            Providers = BuildProviderToggles(),
        };

        _tray = new TrayIconHost(bridge);

        // 岛体右键工具栏能切换的视图。
        //
        // 这一排是**看**，不是**设**：点一下岛体临时切到那个视图，指针离开就还原。
        // 所以设置类的开关（歌词、频谱、开机自启……）一个都不放这里 ——
        // 它们在托盘菜单和设置窗口里，那才是"改以后怎样"的地方。
        //
        // 顺序按"多久看一次"排：正在播的最常看，成绩最少。
        // 图标码位全部渲染字形对照表看过（探针 --glyphs），不是凭记忆挑的。
        _island.BindViews(
        [
            new IslandView("", "正在播放", _media.Id, () => null),
            new IslandView("", "降水预报", _weather.Id, _weather.BuildPeek),
            new IslandView("", "时间", _clock.Id, () => null),
            new IslandView("", "电量", _power.Id, _power.BuildPeek),
            new IslandView("", "Phira 最好成绩", _phira.Id, _phira.BuildPeek),
        ]);

#if DEBUG
        // 开发用：ISLANDX_SETTINGS=1 时启动就把设置窗口打开。
        // Win11 的托盘是 XAML 岛，脚本点不到那个菜单项（试过，读不到按钮矩形），
        // 而设置页的每一次改动都得看图确认 —— 没这个钩子就只能手点。
        if (Environment.GetEnvironmentVariable("ISLANDX_SETTINGS") == "1")
        {
            Settings.SettingsWindowHost.Show(bridge);

            // 顺手置顶。外部脚本用 SetWindowPos(HWND_TOPMOST) 抢不过别的置顶窗口
            // （QQ、壁纸引擎之类），而每改一处外观都要截图确认 ——
            // 让它自己置顶比在外面反复争 z-order 可靠得多。仅开发钩子路径生效。
            foreach (Window w in Windows)
            {
                if (w is Settings.SettingsWindow) w.Topmost = true;
            }
        }
#endif

        // Provider 启动可能等待系统服务，不阻塞 UI 线程。
        // 被用户关掉的压根不启动 —— 常驻的时钟与媒体不受开关控制，
        // 关掉它们岛体就什么都不剩了，所以它们不在 _toggleable 里。
        _ = _scheduler.StartAllAsync(id =>
            id == _weather.Id ? _config.WeatherEnabled
            : id == _phira.Id ? _config.PhiraEnabled
            : !_toggleable.ContainsKey(id) || _config.IsProviderEnabled(id));
    }

    private void RegisterToggleable(IIslandProvider provider, string label)
    {
        _toggleable[provider.Id] = provider;
        _providerLabels[provider.Id] = label;
        _scheduler!.Register(provider);
    }

    private List<ProviderToggle> BuildProviderToggles()
    {
        var list = new List<ProviderToggle>();

        foreach (var (id, provider) in _toggleable)
        {
            // 不再带"当前值"——当前值一律实时从 AppConfig 读（见 SettingsBridge 的注释）
            list.Add(new ProviderToggle(
                id,
                _providerLabels.GetValueOrDefault(id, id),
                enabled => SetProviderEnabled(provider, enabled)));
        }

        return list;
    }

    private void SetProviderEnabled(IIslandProvider provider, bool enabled)
    {
        _config?.SetProviderEnabled(provider.Id, enabled);

        try
        {
            if (enabled)
            {
                _ = provider.StartAsync();
            }
            else
            {
                provider.Stop();
                // Stop 只是不再发新活动，正在显示的那条要单独撤掉
                _scheduler?.Clear(provider.Id);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[app] 切换 {provider.Id} 失败: {ex.Message}");
        }
    }

    private static void OpenConfigFolder()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IslandX");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* 打不开就算了，不值得为此弹错误框 */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduler?.StopAll();
        _audio?.Dispose();
        _tray?.Dispose();
        _island?.Close();

        _singleInstance?.Dispose();
        _singleInstance = null;

        base.OnExit(e);
    }
}
