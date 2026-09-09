using System.Windows;
using System.Windows.Interop;

namespace IslandX.Interop;

internal static class WindowInterop
{
    /// <summary>
    /// 把 WPF 窗口改造成"悬浮件"：点击不抢焦点、不进 Alt+Tab、不占任务栏。
    /// 必须在 SourceInitialized 之后调用（此时 HWND 才存在）。
    /// </summary>
    internal static void MakeOverlay(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        var exStyle = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
        exStyle |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW;
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle));
    }

    /// <summary>
    /// 重新抢占置顶层级。其他置顶窗口（输入法候选、部分播放器）会盖住岛体，
    /// 所以需要低频巡检把自己抬回去。
    /// </summary>
    internal static void ReassertTopmost(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        NativeMethods.SetWindowPos(
            hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>前台是否为全屏游戏 / 投影模式，此时岛体应当隐藏。</summary>
    internal static bool ShouldYieldToFullscreen()
    {
        if (NativeMethods.SHQueryUserNotificationState(out var state) != 0) return false;

        return state is NativeMethods.UserNotificationState.RunningD3dFullScreen
            or NativeMethods.UserNotificationState.PresentationMode
            or NativeMethods.UserNotificationState.AppFullScreen;
    }
}
