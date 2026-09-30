using Microsoft.Win32;
using System.IO;

namespace AyDesktop.Services;

public static class ShellSettings
{
    private const string AdvancedKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string HideIconsKey =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel";

    public static bool SystemShowsExtensions
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(AdvancedKey);
                var v = k?.GetValue("HideFileExt");
                return v is int i && i == 0;
            }
            catch { return false; }
        }
    }

    public static bool SystemShowsHiddenFiles
    {
        get
        {
            try
            {
                using var k = Registry.CurrentUser.OpenSubKey(AdvancedKey);
                var v = k?.GetValue("Hidden");
                return v is int i && i == 1;
            }
            catch { return false; }
        }
    }

    public static bool SystemShowsDesktopIcon(string clsid)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(HideIconsKey);
            var v = k?.GetValue(clsid);
            if (v is int i) return i == 0;
            return true;
        }
        catch { return true; }
    }

    public static bool IsHiddenOrSystem(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch { return false; }
    }

    // ====================================================================
    // ★ 新增：判断某扩展名是否"永不显示扩展名"
    //   典型：.lnk .url .pif .scf .shs .xnk .library-ms .searchconnector-ms
    //
    //   注册表位置有两处，只要有一处带 NeverShowExt 即生效：
    //     HKCR\.lnk\NeverShowExt            （直接挂在扩展名上）
    //     HKCR\lnkfile\NeverShowExt         （挂在 ProgID 上）
    // ====================================================================
    public static bool IsNeverShowExt(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return false;
        if (!extension.StartsWith(".")) extension = "." + extension;

        try
        {
            string? progId = null;
            using (var extKey = Registry.ClassesRoot.OpenSubKey(extension))
            {
                if (extKey == null) return false;

                // 方式 A：直接查 HKCR\.ext\NeverShowExt
                if (extKey.GetValue("NeverShowExt") != null) return true;

                // 取 ProgID（默认值）
                progId = extKey.GetValue("")?.ToString();
            }

            // 方式 B：查 HKCR\<ProgID>\NeverShowExt
            if (!string.IsNullOrEmpty(progId))
            {
                using var pidKey = Registry.ClassesRoot.OpenSubKey(progId);
                if (pidKey?.GetValue("NeverShowExt") != null) return true;
            }
        }
        catch { }

        return false;
    }
}