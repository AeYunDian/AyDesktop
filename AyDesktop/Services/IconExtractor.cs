using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace AyDesktop.Services;

public static class IconExtractor
{
    // ==================== P/Invoke ====================

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHGetFileInfo(IntPtr pidl, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszName,
        IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHDefExtractIcon(string pszIconFile, int iIndex, uint uFlags,
        ref IntPtr phiconLarge, ref IntPtr phiconSmall, uint nIconSize);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, out int pi);
        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, out int pi);
        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);
        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);
        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, out int pi);
        [PreserveSig] int Draw(IntPtr pimldp);
        [PreserveSig] int Remove(int i);
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_PIDL = 0x000000008;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
    private const uint SHGFI_SYSICONINDEX = 0x000004000;
    private const uint SHGFI_OVERLAYINDEX = 0x000000040;   // ★ 取 overlay 索引
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private const int SHIL_LARGE = 0x0;         // 32×32（overlay 从这里取）
    private const int SHIL_SMALL = 0x1;         // 16×16
    private const int SHIL_EXTRALARGE = 0x2;    // 48×48（主图标）

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // ==================== 普通文件（主图标 + overlay） ====================

    public static ImageSource? GetIcon(string path)
    {
        if (path.StartsWith("::", StringComparison.Ordinal))
            return GetSystemIcon(path.Substring(2));

        var icon = TryGetHighResIcon(path);
        if (icon != null) return icon;

        return GetLargeIconFallback(path);
    }

    /// <summary>
    /// 拿 48×48 主图标，并合成 overlay（快捷方式箭头 / UAC 盾牌）。
    /// </summary>
    private static ImageSource? TryGetHighResIcon(string path, bool forceDirectory = false)
    {
        try
        {
            var info = new SHFILEINFO();
            uint flags = SHGFI_SYSICONINDEX | SHGFI_OVERLAYINDEX;

            bool isDir = forceDirectory || Directory.Exists(path);
            bool exists = !forceDirectory && (File.Exists(path) || Directory.Exists(path));

            if (!exists)
                flags |= SHGFI_USEFILEATTRIBUTES;

            uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;

            IntPtr res = SHGetFileInfo(path, attr, ref info,
                (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (res == IntPtr.Zero) return null;

            // ★ iIcon：低 24 位是主图标索引，高 8 位是 overlay 索引
            int iconIndex = info.iIcon & 0x00FFFFFF;
            int overlayIndex = (info.iIcon >> 24) & 0xFF;
            if (iconIndex < 0) return null;

            var main = GetImageListIcon(SHIL_EXTRALARGE, iconIndex);
            if (main == null) return null;

            if (overlayIndex <= 0) return main;

            // overlay 从 32×32 列表取，缩放到主图标右下角
            var overlay = GetImageListIcon(SHIL_LARGE, overlayIndex);
            if (overlay == null) return main;

            return CompositeWithOverlay(main, overlay);
        }
        catch { }
        return null;
    }

    /// <summary>从指定尺寸的 image list 里取图标</summary>
    private static BitmapSource? GetImageListIcon(int imageListType, int iconIndex)
    {
        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(imageListType, ref iid, out var imageList) != 0 || imageList == null)
            return null;

        try
        {
            // ILD_TRANSPARENT = 0x1
            if (imageList.GetIcon(iconIndex, 0x1, out IntPtr hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;

            try
            {
                var bmp = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally { DestroyIcon(hIcon); }
        }
        finally
        {
            try { Marshal.ReleaseComObject(imageList); } catch { }
        }
    }

    /// <summary>把 overlay 合成到主图标右下角（overlay 占主图标 1/2 大小）</summary>
    private static BitmapSource CompositeWithOverlay(BitmapSource main, BitmapSource overlay)
    {
        int w = main.PixelWidth;
        int h = main.PixelHeight;

        double size = w / 2.0;
        var rect = new Rect(w - size, h - size, size, size);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawImage(main, new Rect(0, 0, w, h));
            dc.DrawImage(overlay, rect);
        }

        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static ImageSource? GetLargeIconFallback(string path)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON;

        bool isDir = Directory.Exists(path);
        if (!File.Exists(path) && !isDir)
            flags |= SHGFI_USEFILEATTRIBUTES;

        uint attr = isDir ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        SHGetFileInfo(path, attr, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return FromHIcon(info.hIcon);
    }

    // ==================== 系统图标 ====================

    public static ImageSource? GetSystemIcon(string clsid)
    {
        string raw = clsid.TrimStart(':');
        return TryViaPidl(raw) ?? TryViaString(raw) ?? TryViaRegistry(raw);
    }

    private static ImageSource? TryViaPidl(string clsid)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            string name = clsid.StartsWith("::") ? clsid : "::" + clsid;
            if (SHParseDisplayName(name, IntPtr.Zero, out pidl, 0, out _) != 0 || pidl == IntPtr.Zero)
                return null;

            var info = new SHFILEINFO();
            uint flags = SHGFI_PIDL | SHGFI_ICON | SHGFI_LARGEICON;
            SHGetFileInfo(pidl, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            return FromHIcon(info.hIcon);
        }
        catch { return null; }
        finally { if (pidl != IntPtr.Zero) try { Marshal.FreeCoTaskMem(pidl); } catch { } }
    }

    private static ImageSource? TryViaString(string clsid)
    {
        try
        {
            string name = clsid.StartsWith("::") ? clsid : "::" + clsid;
            var info = new SHFILEINFO();
            uint flags = SHGFI_ICON | SHGFI_LARGEICON;
            SHGetFileInfo(name, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            return FromHIcon(info.hIcon);
        }
        catch { return null; }
    }

    private static ImageSource? TryViaRegistry(string clsid)
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey("CLSID\\" + clsid + "\\DefaultIcon");
            var s = key?.GetValue("")?.ToString();
            if (string.IsNullOrEmpty(s)) return null;

            s = s.TrimStart('@');
            int comma = s.LastIndexOf(',');
            string file = comma < 0 ? s : s.Substring(0, comma).Trim('"');
            int idx = 0;
            if (comma >= 0) int.TryParse(s.Substring(comma + 1), out idx);
            if (idx < 0) idx = -idx;
            file = Environment.ExpandEnvironmentVariables(file);

            IntPtr large = IntPtr.Zero, small = IntPtr.Zero;
            int hr = SHDefExtractIcon(file, idx, 0, ref large, ref small, 0x00200020);
            if (hr != 0 || large == IntPtr.Zero) return null;
            return FromHIcon(large);
        }
        catch { return null; }
    }

    /// <summary>文件夹图标（显式指定 FILE_ATTRIBUTE_DIRECTORY）</summary>
    public static ImageSource? GetFolderIcon()
    {
        var high = TryGetHighResIcon("folder", forceDirectory: true);
        if (high != null) return high;

        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | SHGFI_LARGEICON | SHGFI_USEFILEATTRIBUTES;
        SHGetFileInfo("folder", FILE_ATTRIBUTE_DIRECTORY, ref info,
                      (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
        return FromHIcon(info.hIcon);
    }

    private static ImageSource? FromHIcon(IntPtr hIcon)
    {
        if (hIcon == IntPtr.Zero) return null;
        try
        {
            var icon = Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            icon.Freeze();
            return icon;
        }
        finally { DestroyIcon(hIcon); }
    }

    public static string GetDesktopPath() =>
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

    public static string GetPublicDesktopPath()
    {
        var p = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(p)) return p;
        var pub = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var root = string.IsNullOrEmpty(pub) ? null : Path.GetDirectoryName(pub);
        return string.IsNullOrEmpty(root) ? "" : Path.Combine(root, "Desktop");
    }
}