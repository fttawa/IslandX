using Microsoft.Win32;

namespace IslandX.Core;

/// <summary>
/// 开机自启，走 HKCU 的 Run 键。
///
/// 不用启动文件夹：那是个用户能看见也能随手删掉的 .lnk，状态查询还得解析快捷方式。
/// 也不用计划任务：那需要管理员权限，而这只是个托盘小程序。
/// HKCU\...\Run 不需要提权，读写都是一行。
/// </summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "IslandX";

    /// <summary>
    /// 当前是否已注册。比较的是路径本身 —— 程序被移动过之后，
    /// 注册表里还留着指向旧位置的项，那种情况应视为"未启用"并允许重新写入。
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(ValueName) is not string registered) return false;
                return string.Equals(Normalize(registered), Normalize(CommandLine),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>设置开机自启。返回是否达到了期望状态。</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled) key.SetValue(ValueName, CommandLine, RegistryValueKind.String);
            else key.DeleteValue(ValueName, throwOnMissingValue: false);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 可执行文件路径，带引号 —— 路径里有空格时不加引号，
    /// 系统会把 "C:\Program Files\..." 拆成两个参数，启动直接失败。
    /// </summary>
    private static string CommandLine
    {
        get
        {
            var path = Environment.ProcessPath ?? "";
            return $"\"{path}\"";
        }
    }

    private static string Normalize(string value) => value.Trim().Trim('"');
}
