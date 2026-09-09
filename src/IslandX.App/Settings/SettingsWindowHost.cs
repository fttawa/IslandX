using System.Windows;
using IslandX.Core;

namespace IslandX.Settings;

/// <summary>
/// 设置窗口的唯一入口。
///
/// 存在的理由只有一条：**不要开出第二个**。托盘菜单点两次「设置…」就会有两份 UI
/// 读同一份配置，两边的勾还各自记着自己那一刻的值 —— 从一个改完另一个就是错的。
/// 已经开着就激活它，而不是再造一个。
/// </summary>
public static class SettingsWindowHost
{
    private static SettingsWindow? _current;

    public static void Show(SettingsBridge bridge)
    {
        // 托盘的 Click 来自 WinForms 的消息循环，和 WPF 是同一个 UI 线程，
        // 但保险起见还是过一遍 Dispatcher —— 造 Window 必须在 UI 线程上
        var app = Application.Current;
        if (app is null) return;

        app.Dispatcher.Invoke(() =>
        {
            if (_current is not null)
            {
                if (_current.WindowState == WindowState.Minimized)
                    _current.WindowState = WindowState.Normal;

                _current.Activate();
                return;
            }

            _current = new SettingsWindow(bridge);
            _current.Closed += (_, _) => _current = null;
            _current.Show();
        });
    }
}
