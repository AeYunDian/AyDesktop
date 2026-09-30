using System.IO;
using System.Runtime.InteropServices;

namespace AyDesktop.Services;

public static class ShellContextMenu
{
    // ==================== COM 接口 ====================

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E6-0000-0000-C000-000000000046")]
    private interface IShellFolder
    {
        void ParseDisplayName(IntPtr hwnd, IntPtr pbc,
            [MarshalAs(UnmanagedType.LPWStr)] string pszDisplayName,
            out uint pchEaten, out IntPtr ppidl, ref uint pdwAttributes);
        void EnumObjects(IntPtr hwnd, uint grfFlags, out IntPtr ppenumIDList);
        void BindToObject(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void BindToStorage(IntPtr pidl, IntPtr pbc, ref Guid riid, out IntPtr ppv);
        void CompareIDs(IntPtr lParam, IntPtr pidl1, IntPtr pidl2);
        void CreateViewObject(IntPtr hwndOwner, ref Guid riid, out IntPtr ppv);
        void GetAttributesOf(uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl, ref uint rgfInOut);
        void GetUIObjectOf(IntPtr hwndOwner, uint cidl,
            [MarshalAs(UnmanagedType.LPArray)] IntPtr[] apidl,
            ref Guid riid, IntPtr rgfReserved, out IntPtr ppv);
        void GetDisplayNameOf(IntPtr pidl, uint uFlags, out IntPtr pName);
        void SetNameOf(IntPtr hwnd, IntPtr pidl,
            [MarshalAs(UnmanagedType.LPWStr)] string pszName,
            uint uFlags, out IntPtr ppidlOut);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214E4-0000-0000-C000-000000000046")]
    private interface IContextMenu
    {
        [PreserveSig]
        int QueryContextMenu(IntPtr hMenu, uint indexMenu,
            uint idCmdFirst, uint idCmdLast, uint uFlags);
        void InvokeCommand(IntPtr pici);
        void GetCommandString(IntPtr idCmd, uint uType,
            IntPtr pReserved, IntPtr pszName, uint cchMax);
    }

    // ==================== P/Invoke ====================

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl,
        uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll")]
    private static extern int SHBindToParent(IntPtr pidl, ref Guid riid,
        out IShellFolder ppv, out IntPtr ppidlLast);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags,
        IntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenuEx(IntPtr hMenu, uint fuFlags,
        int x, int y, IntPtr hwnd, IntPtr lptpm);

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMF_NORMAL = 0x00000000;

    private const int SW_SHOWNORMAL = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    private struct CMINVOKECOMMANDINFO
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        public IntPtr lpVerb;
        public IntPtr lpParameters;
        public IntPtr lpDirectory;
        public int nShow;
        public uint dwHotKey;
        public IntPtr hIcon;
    }

    // ==================== 弹原生菜单 ====================

    /// <summary>
    /// 弹原生 Windows Shell 右键菜单（含所有 shell extension：7-Zip / runas / Git…）。
    /// </summary>
    public static bool ShowNative(
        IntPtr hwndOwner,
        IReadOnlyList<string> paths,
        IReadOnlyList<(string label, Action action)>? customItems,
        int screenX, int screenY)
    {
        if (paths.Count == 0) return false;

        IntPtr[] pidls = new IntPtr[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            if (SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _) != 0
                || pidls[i] == IntPtr.Zero)
            {
                for (int j = 0; j < i; j++)
                    try { Marshal.FreeCoTaskMem(pidls[j]); } catch { }
                return false;
            }
        }

        IntPtr hMenu = IntPtr.Zero;
        try
        {
            var iidShell = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], ref iidShell, out var parent, out _) != 0
                || parent == null)
                return false;

            IntPtr[] childPidls = new IntPtr[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                var iidShell2 = typeof(IShellFolder).GUID;
                SHBindToParent(pidls[i], ref iidShell2, out _, out childPidls[i]);
            }

            var iidCm = typeof(IContextMenu).GUID;
            parent.GetUIObjectOf(hwndOwner, (uint)childPidls.Length,
                childPidls, ref iidCm, IntPtr.Zero, out IntPtr cmPtr);
            if (cmPtr == IntPtr.Zero) return false;

            var cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
            Marshal.Release(cmPtr);

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero) return false;

            const uint idFirst = 1;
            const uint idLast = 0x7FFF;
            cm.QueryContextMenu(hMenu, 0, idFirst, idLast, CMF_NORMAL);

            var customActions = new Dictionary<int, Action>();
            if (customItems != null && customItems.Count > 0)
            {
                AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
                int customId = 0x8000;
                foreach (var (label, action) in customItems)
                {
                    AppendMenu(hMenu, MF_STRING, (IntPtr)customId, label);
                    customActions[customId] = action;
                    customId++;
                }
            }

            uint cmd = TrackPopupMenuEx(hMenu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                screenX, screenY, hwndOwner, IntPtr.Zero);

            if (cmd == 0) return false;

            if (cmd >= 0x8000)
            {
                if (customActions.TryGetValue((int)cmd, out var act))
                {
                    act();
                    return true;
                }
            }
            else if (cmd >= idFirst)
            {
                var info = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd = hwndOwner,
                    lpVerb = (IntPtr)(cmd - idFirst),
                    nShow = SW_SHOWNORMAL,
                };
                IntPtr pInfo = Marshal.AllocHGlobal(info.cbSize);
                try
                {
                    Marshal.StructureToPtr(info, pInfo, false);
                    cm.InvokeCommand(pInfo);
                    return true;
                }
                finally { Marshal.FreeHGlobal(pInfo); }
            }
            return false;
        }
        catch { return false; }
        finally
        {
            if (hMenu != IntPtr.Zero) try { DestroyMenu(hMenu); } catch { }
            foreach (var pidl in pidls)
                try { Marshal.FreeCoTaskMem(pidl); } catch { }
        }
    }

    // ==================== 属性对话框 ====================

    public static void ShowProperties(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        var psi = new System.Diagnostics.ProcessStartInfo(path)
        {
            UseShellExecute = true,
            Verb = "properties",
        };
        try { System.Diagnostics.Process.Start(psi); } catch { }
    }
}