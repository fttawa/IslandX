using System.Drawing;
using System.Drawing.Drawing2D;
using IslandX.Core;
using IslandX.Interop;
using IslandX.Settings;
using WinForms = System.Windows.Forms;

namespace IslandX.Tray;

/// <summary>
/// 托盘驻留入口。常用开关直接放在菜单里，完整设置走「设置…」打开设置窗口。
///
/// 勾选状态**在菜单弹出时才重读配置**，不是构造时抓一次快照。
/// 有了设置窗口之后这一点是必须的：从设置页改完，托盘的勾如果还停在旧值上，
/// 用户看到的就是两个界面互相矛盾。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly SettingsBridge _bridge;
    private readonly WinForms.NotifyIcon _notifyIcon;

    /// <summary>需要在菜单弹出时同步勾选状态的项，键是"怎么读当前值"。</summary>
    private readonly List<(WinForms.ToolStripMenuItem Item, Func<bool> Read)> _syncable = [];

    private IntPtr _iconHandle;

    public TrayIconHost(SettingsBridge bridge)
    {
        _bridge = bridge;

        var menu = new WinForms.ContextMenuStrip();
        var config = bridge.Config;

        // 把实际生效的热键标注在菜单上 —— 候选可能因占用而回退，用户需要知道最终是哪一组
        var toggleItem = new WinForms.ToolStripMenuItem(
            Label("展开 / 收起", bridge.HotkeyLabel(HotkeyKind.Expand)));
        toggleItem.Click += (_, _) => bridge.ToggleExpanded();
        menu.Items.Add(toggleItem);

        menu.Items.Add(Check(
            Label("灵动点模式", bridge.HotkeyLabel(HotkeyKind.Dot)),
            () => config.DotMode,
            bridge.SetDotMode));

        // 律动要持续渲染（约 3% 单核），给个开关让在意功耗的人关掉
        menu.Items.Add(Check("频谱随音乐律动", () => config.PulseEnabled, bridge.SetPulse));

        // 标题里写明会联网 —— 这个开关同时是隐私开关，用户得知道自己在同意什么
        menu.Items.Add(Check("显示歌词（需联网获取）", () => config.LyricsEnabled, bridge.SetLyrics));

        menu.Items.Add(BuildTranslationGroup());

        // 同样把代价写在标题上，而且比歌词更重 —— 这个要读系统定位、
        // 再把坐标发给天气服务商。用户点开关之前就该知道这两件事
        menu.Items.Add(Check("降水提醒（需定位 + 联网）", () => config.WeatherEnabled, bridge.SetWeather));

        // 这一条的代价和上面两个不同：不联网，但要读 Phira 的存档文件。
        // 照样写在标题上 —— 用户该知道它去动了别的程序的数据
        menu.Items.Add(Check("Phira 成绩提醒（读本地存档）", () => config.PhiraEnabled, bridge.SetPhira));

        // Provider 开关收进子菜单：平铺的话主菜单会越加越长
        if (bridge.Providers.Count > 0)
        {
            var group = new WinForms.ToolStripMenuItem("显示哪些事件");

            foreach (var provider in bridge.Providers)
            {
                var id = provider.Id;
                group.DropDownItems.Add(Check(
                    provider.Label, () => config.IsProviderEnabled(id), provider.Set));
            }

            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(group);
        }

        menu.Items.Add(new WinForms.ToolStripSeparator());

        // 开机自启留在托盘：它是常用开关。写注册表可能被策略挡住，
        // 失败就把勾回弹 —— 否则菜单显示已开启、实际没生效，用户是被骗了
        var autoStartItem = new WinForms.ToolStripMenuItem("开机自动启动")
        {
            CheckOnClick = true,
            Checked = bridge.AutoStartEnabled(),
        };

        autoStartItem.Click += (_, _) =>
        {
            if (!bridge.SetAutoStart(autoStartItem.Checked))
                autoStartItem.Checked = !autoStartItem.Checked;
        };

        // 它的真值在注册表里，别的程序也能改，所以同样纳入弹出时重读
        _syncable.Add((autoStartItem, bridge.AutoStartEnabled));
        menu.Items.Add(autoStartItem);

        // 设置窗口是完整入口：手填坐标、行政区、热键这几项只有它能改。
        // 原来的「打开配置文件夹」不再单列 —— 它当初存在就是为了让人手改这几项，
        // 现在设置页覆盖了，而且页里自己也有那个按钮
        var settingsItem = new WinForms.ToolStripMenuItem("设置…");
        settingsItem.Click += (_, _) => SettingsWindowHost.Show(bridge);
        menu.Items.Add(settingsItem);

        var exitItem = new WinForms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => bridge.Exit();
        menu.Items.Add(exitItem);

        // 弹出前把所有勾选状态重读一遍。这是"托盘与设置页不打架"的关键 ——
        // 也顺带修掉了原来的一个隐患：开机自启是在注册表里的，
        // 别的程序（或用户自己）改了之后，快照式的菜单会一直显示旧值
        menu.Opening += (_, _) =>
        {
            foreach (var (item, read) in _syncable) item.Checked = read();
            SyncTranslationGroup();
        };

        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = CreateIcon(out _iconHandle),
            Text = "IslandX 灵动岛",
            Visible = true,
            ContextMenuStrip = menu,
        };

        // 左键单击直接切换展开态
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) bridge.ToggleExpanded();
        };
    }

    /// <summary>
    /// 造一个可勾选项。当前值给的是**读取函数**而不是布尔值 ——
    /// 传值就退回快照，那正是这次要消除的东西。
    /// </summary>
    private WinForms.ToolStripMenuItem Check(string text, Func<bool> read, Action<bool> set)
    {
        var item = new WinForms.ToolStripMenuItem(text)
        {
            CheckOnClick = true,
            Checked = read(),
        };

        item.Click += (_, _) => set(item.Checked);
        _syncable.Add((item, read));
        return item;
    }

    // ===== 歌词翻译（三选一） =====

    private readonly List<(WinForms.ToolStripMenuItem Item, LyricTranslationMode Mode)> _translationItems = [];

    private WinForms.ToolStripMenuItem BuildTranslationGroup()
    {
        // 三选一，做成互斥（单选）而不是三个独立勾选 —— 它们本就是同一个枚举的三个值。
        // WinForms 没有现成的单选菜单项，手动维护 Checked：点谁谁亮，其余全灭。
        //
        // 平级放在"显示歌词"下面而不是做成它的子菜单：带子菜单的可勾选项点上去
        // 既翻勾又展开，两种反馈叠在一起很难说清刚才发生了什么。
        var group = new WinForms.ToolStripMenuItem("歌词翻译");

        (LyricTranslationMode Mode, string Label)[] modes =
        [
            (LyricTranslationMode.Both, "原文 + 译文"),
            (LyricTranslationMode.TranslationOnly, "只显示译文"),
            (LyricTranslationMode.OriginalOnly, "只显示原文"),
        ];

        foreach (var (mode, label) in modes)
        {
            var item = new WinForms.ToolStripMenuItem(label)
            {
                Checked = _bridge.Config.LyricTranslation == mode,
            };

            var picked = mode;
            item.Click += (_, _) =>
            {
                _bridge.SetLyricTranslation(picked);
                SyncTranslationGroup();
            };

            _translationItems.Add((item, mode));
            group.DropDownItems.Add(item);
        }

        return group;
    }

    private void SyncTranslationGroup()
    {
        foreach (var (item, mode) in _translationItems)
            item.Checked = _bridge.Config.LyricTranslation == mode;
    }

    private static string Label(string text, string? hotkey)
        => hotkey is null ? text : $"{text}    ({hotkey})";

    // ===== 图标 =====

    /// <summary>
    /// 画一个和岛体同形的托盘图标：深色胶囊 + 一点高光。
    /// 不用外部 .ico 资源，省掉一个构建产物，也保证与岛体外观一致。
    /// </summary>
    private static Icon CreateIcon(out IntPtr handle)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using var path = new GraphicsPath();
            var rect = new Rectangle(3, 10, 26, 12);
            path.AddArc(rect.X, rect.Y, rect.Height, rect.Height, 90, 180);
            path.AddArc(rect.Right - rect.Height, rect.Y, rect.Height, rect.Height, 270, 180);
            path.CloseFigure();

            using var body = new SolidBrush(Color.FromArgb(235, 12, 12, 14));
            g.FillPath(body, path);

            using var dot = new SolidBrush(Color.FromArgb(220, 235, 240, 255));
            g.FillEllipse(dot, 8, 14, 4, 4);
        }

        handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        // GetHicon 拿到的句柄要显式销毁，Icon.Dispose 不管它
        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }
    }
}
