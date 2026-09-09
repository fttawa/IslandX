using System.Runtime.InteropServices;

namespace IslandX.Interop;

internal static partial class NativeMethods
{
    internal const int GWL_EXSTYLE = -20;

    /// <summary>点击不激活窗口，焦点留在用户原来的应用上。</summary>
    internal const int WS_EX_NOACTIVATE = 0x08000000;

    /// <summary>不出现在 Alt+Tab 和任务栏中。</summary>
    internal const int WS_EX_TOOLWINDOW = 0x00000080;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);

    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>用户通知状态，用于识别全屏游戏 / 投影模式并自动让位。</summary>
    internal enum UserNotificationState
    {
        NotPresent = 1,
        Busy = 2,
        RunningD3dFullScreen = 3,
        PresentationMode = 4,
        AcceptsNotifications = 5,
        QuietTime = 6,
        AppFullScreen = 7,
    }

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [LibraryImport("shell32.dll")]
    internal static partial int SHQueryUserNotificationState(out UserNotificationState state);

    /// <summary>释放 Bitmap.GetHicon 产生的图标句柄。</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(IntPtr hIcon);

    /// <summary>命中测试消息。半透明像素上系统会发它来问"这一点算不算你的"。</summary>
    internal const int WM_NCHITTEST = 0x0084;

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        internal int X;
        internal int Y;
    }

    /// <summary>
    /// 屏幕物理坐标 → 客户区物理坐标。
    /// 不用 Visual.PointFromScreen：在 PerMonitorV2 下它对 WM_NCHITTEST 传来的
    /// 物理坐标不做 DPI 换算，算出来的点会偏到窗口外面去。
    /// </summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(IntPtr hWnd, ref POINT point);

    /// <summary>回答 WM_NCHITTEST：这一点不属于我，继续往下找窗口。</summary>
    internal static readonly IntPtr HTTRANSPARENT = new(-1);

    // ===== 剪贴板监听 =====

    /// <summary>剪贴板内容变化。注册后由系统投递，不需要轮询。</summary>
    internal const int WM_CLIPBOARDUPDATE = 0x031D;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AddClipboardFormatListener(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveClipboardFormatListener(IntPtr hWnd);

    // ===== 电源状态 =====

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        /// <summary>0 = 用电池，1 = 接交流电，255 = 未知。</summary>
        internal byte ACLineStatus;

        /// <summary>位标志：1 高 / 2 低 / 4 危急 / 8 充电中 / 128 无电池 / 255 未知。</summary>
        internal byte BatteryFlag;

        /// <summary>剩余百分比 0–100，255 表示未知。</summary>
        internal byte BatteryLifePercent;

        internal byte SystemStatusFlag;

        /// <summary>剩余秒数，-1 表示未知。</summary>
        internal int BatteryLifeTime;
        internal int BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    // ===== 全局热键 =====
    internal const int WM_HOTKEY = 0x0312;
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);
}
