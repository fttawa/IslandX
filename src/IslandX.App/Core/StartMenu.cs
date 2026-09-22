using System.IO;
using System.Runtime.InteropServices;

namespace IslandX.Core;

/// <summary>
/// 把自己注册进**当前用户**的开始菜单。
///
/// 这是个绿色单文件程序，没有安装器，所以「开始」里搜不到它 ——
/// 用户只能记住 exe 扔在哪。这里补的就是这个缺口。
///
/// 只写 <c>%APPDATA%\Microsoft\Windows\Start Menu\Programs</c>（当前用户），
/// **不碰 %ProgramData% 的全局目录** —— 那个要管理员权限，而这只是个托盘小程序。
/// 和开机自启写 HKCU 是同一个取舍。
///
/// 快捷方式直接放在 Programs 根下而不是建一个同名文件夹：
/// 一个文件夹里只装一个快捷方式，在开始菜单的"所有应用"里反而多一层要点开。
/// </summary>
public static class StartMenu
{
    private const string LinkName = "IslandX.lnk";

    private static string LinkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), LinkName);

    /// <summary>
    /// 是否已注册。
    ///
    /// 判据是**快捷方式指向的路径与当前 exe 相同**，而不是"文件存不存在" ——
    /// 绿色程序会被挪位置，那时旧快捷方式还在、却指向一个不存在的文件。
    /// 那种情况应当视为"未注册"，好让用户能重新勾一次把它修好。
    /// （开机自启那边是同一套判据，理由见 <see cref="AutoStart.IsEnabled"/>。）
    /// </summary>
    public static bool IsRegistered
    {
        get
        {
            try
            {
                if (!File.Exists(LinkPath)) return false;

                var target = ReadTarget(LinkPath);
                if (string.IsNullOrEmpty(target)) return false;

                return string.Equals(
                    Path.GetFullPath(target),
                    Path.GetFullPath(Environment.ProcessPath ?? ""),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>注册或取消。返回是否达到了期望状态。</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                if (File.Exists(LinkPath)) File.Delete(LinkPath);
                return true;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

            Write(LinkPath, exe);
            return IsRegistered;
        }
        catch
        {
            return false;
        }
    }

    // ================= .lnk 读写 =================
    //
    // .NET 没有内建的快捷方式 API，只能走 COM 的 IShellLink。
    // 另一条路是后期绑定 WScript.Shell —— 代码少得多，但它依赖 Windows Script Host，
    // 而那东西在不少企业策略里是被关掉的。IShellLink 是系统自带的，不会被那样挡住。

    private static void Write(string linkPath, string targetExe)
    {
        var link = (IShellLinkW)new ShellLink();

        link.SetPath(targetExe);
        link.SetDescription("IslandX 灵动岛");

        // 工作目录设成 exe 所在目录：程序自己不依赖它，
        // 但从开始菜单启动时若不设，工作目录会是 system32，
        // 之后任何相对路径的行为都会变得莫名其妙
        var dir = Path.GetDirectoryName(targetExe);
        if (!string.IsNullOrEmpty(dir)) link.SetWorkingDirectory(dir);

        ((IPersistFile)link).Save(linkPath, fRemember: true);
    }

    private static string ReadTarget(string linkPath)
    {
        var link = (IShellLinkW)new ShellLink();
        ((IPersistFile)link).Load(linkPath, STGM_READ);

        var buffer = new char[MAX_PATH];
        link.GetPath(buffer, buffer.Length, IntPtr.Zero, SLGP_RAWPATH);

        var end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? buffer.Length : end);
    }

    private const int MAX_PATH = 260;
    private const uint STGM_READ = 0;
    private const uint SLGP_RAWPATH = 4;

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    /// <summary>
    /// ⚠ 方法的**声明顺序就是 vtable 顺序**，一个都不能少、不能换位 ——
    /// 用不到的也要占着位置，否则调用会跑到相邻的槽上去。
    /// </summary>
    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] char[] pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport,
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        // IPersist 的那一个也要占位 —— IPersistFile 继承自它
        void GetClassID(out Guid pClassID);

        [PreserveSig] int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            [MarshalAs(UnmanagedType.Bool)] bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
