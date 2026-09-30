using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AyDesktop.Services;

public static class ShellContextMenu
{
    // ====================================================================
    // COM 接口
    // ====================================================================

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

    // ====================================================================
    // P/Invoke
    // ====================================================================

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

    [DllImport("user32.dll")]
    private static extern int GetMenuItemCount(IntPtr hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMenuItemInfo(IntPtr hMenu, uint uItem,
        [MarshalAs(UnmanagedType.Bool)] bool fByPosition,
        ref MENUITEMINFO lpmii);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool InsertMenuItem(IntPtr hMenu, uint uItem,
        [MarshalAs(UnmanagedType.Bool)] bool fByPosition,
        ref MENUITEMINFO lpmii);

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint CMF_NORMAL = 0x00000000;

    private const int SW_SHOWNORMAL = 1;

    // MIIM_* 用于 Get/InsertMenuItem
    private const uint MIIM_STATE = 0x00000001;
    private const uint MIIM_ID = 0x00000002;
    private const uint MIIM_SUBMENU = 0x00000004;
    private const uint MIIM_CHECKMARKS = 0x00000008;
    private const uint MIIM_DATA = 0x00000020;
    private const uint MIIM_STRING = 0x00000040;
    private const uint MIIM_BITMAP = 0x00000080;
    private const uint MIIM_FTYPE = 0x00000100;

    private const uint CustomIdBase = 0x8000;
    private const uint ShellIdFirst = 1;
    private const uint ShellIdLast = 0x7FFF;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MENUITEMINFO
    {
        public uint cbSize;
        public uint fMask;
        public uint fType;
        public uint fState;
        public uint wID;
        public IntPtr hSubMenu;
        public IntPtr hbmpChecked;
        public IntPtr hbmpUnchecked;
        public IntPtr dwItemData;
        public IntPtr dwTypeData;
        public uint cch;
        public IntPtr hbmpItem;
    }

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

    // ====================================================================
    // 缓存
    // ====================================================================

    private sealed class CachedMenu
    {
        public IntPtr HMenu;
        public IContextMenu Cm = null!;
        public IntPtr[] Pidls = Array.Empty<IntPtr>();
        public uint IdFirst = ShellIdFirst;
        public uint IdLast = ShellIdLast;
    }

    private static readonly Dictionary<string, CachedMenu> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ReaderWriterLockSlim _lock = new();

    private static int _hookState;   // 0 = 未挂，1 = 已挂

    // ====================================================================
    // 公开入口
    // ====================================================================

    /// <summary>
    /// 弹原生 Windows Shell 右键菜单（含所有 shell extension：7-Zip / Git / TortoiseSVN…）。
    /// 首次调用某个路径组合时构建并缓存 IContextMenu，之后立即弹出。
    /// Shell 关联发生变化（装/卸软件、改文件关联）时自动清空缓存。
    /// </summary>
    public static bool ShowNative(
        IntPtr hwndOwner,
        IReadOnlyList<string> paths,
        IReadOnlyList<(string label, Action action)>? customItems,
        int screenX, int screenY)
    {
        if (paths.Count == 0) return false;
        EnsureShellHook();

        var cached = GetOrBuild(paths);
        if (cached == null) return false;

        // 克隆一份菜单，避免污染缓存
        IntPtr hMenu = CloneMenu(cached.HMenu);
        if (hMenu == IntPtr.Zero) return false;

        var customActions = new Dictionary<int, Action>();
        try
        {
            if (customItems is { Count: > 0 })
            {
                AppendMenu(hMenu, MF_SEPARATOR, IntPtr.Zero, null);
                uint id = CustomIdBase;
                foreach (var (label, action) in customItems)
                {
                    AppendMenu(hMenu, MF_STRING, (IntPtr)id, label);
                    customActions[(int)id] = action;
                    id++;
                }
            }

            uint cmd = TrackPopupMenuEx(hMenu,
                TPM_RETURNCMD | TPM_RIGHTBUTTON,
                screenX, screenY, hwndOwner, IntPtr.Zero);

            if (cmd == 0) return false;

            // 自定义项
            if (cmd >= CustomIdBase)
            {
                if (customActions.TryGetValue((int)cmd, out var act))
                {
                    act();
                    return true;
                }
                return false;
            }

            // Shell 项
            if (cmd >= cached.IdFirst && cmd <= cached.IdLast)
            {
                var info = new CMINVOKECOMMANDINFO
                {
                    cbSize = Marshal.SizeOf<CMINVOKECOMMANDINFO>(),
                    hwnd = hwndOwner,
                    lpVerb = (IntPtr)(cmd - cached.IdFirst),
                    nShow = SW_SHOWNORMAL,
                };
                IntPtr pInfo = Marshal.AllocHGlobal(info.cbSize);
                try
                {
                    Marshal.StructureToPtr(info, pInfo, false);
                    cached.Cm.InvokeCommand(pInfo);
                    return true;
                }
                finally { Marshal.FreeHGlobal(pInfo); }
            }
            return false;
        }
        catch { return false; }
        finally { try { DestroyMenu(hMenu); } catch { } }
    }

    /// <summary>清空缓存（不解除 Shell 变更监听）。</summary>
    public static void ClearCache()
    {
        _lock.EnterWriteLock();
        try
        {
            foreach (var c in _cache.Values) FreeCached(c);
            _cache.Clear();
        }
        finally { _lock.ExitWriteLock(); }
    }

    /// <summary>App 退出时调用：清缓存 + 解除监听。</summary>
    public static void Shutdown()
    {
        ClearCache();
        try { ShellChangeNotifier.Stop(); } catch { }

        if (Interlocked.Exchange(ref _hookState, 0) == 1)
        {
            try { ShellChangeNotifier.AssociationChanged -= ClearCache; } catch { }
        }
    }

    // ====================================================================
    // 缓存构建 / 复用
    // ====================================================================

    private static CachedMenu? GetOrBuild(IReadOnlyList<string> paths)
    {
        string key = BuildCacheKey(paths);

        _lock.EnterReadLock();
        try
        {
            if (_cache.TryGetValue(key, out var hit)) return hit;
        }
        finally { _lock.ExitReadLock(); }

        var built = BuildMenu(paths);
        if (built == null) return null;

        _lock.EnterWriteLock();
        try
        {
            if (_cache.TryGetValue(key, out var existing))
            {
                FreeCached(built);
                return existing;
            }
            _cache[key] = built;
            return built;
        }
        finally { _lock.ExitWriteLock(); }
    }

    private static CachedMenu? BuildMenu(IReadOnlyList<string> paths)
    {
        var pidls = new IntPtr[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            if (SHParseDisplayName(paths[i], IntPtr.Zero, out pidls[i], 0, out _) != 0
                || pidls[i] == IntPtr.Zero)
            {
                for (int j = 0; j < i; j++)
                    try { Marshal.FreeCoTaskMem(pidls[j]); } catch { }
                return null;
            }
        }

        IntPtr hMenu = IntPtr.Zero;
        IContextMenu? cm = null;
        try
        {
            var iidShell = typeof(IShellFolder).GUID;
            if (SHBindToParent(pidls[0], ref iidShell, out var parent, out _) != 0
                || parent == null)
            {
                foreach (var p in pidls) try { Marshal.FreeCoTaskMem(p); } catch { }
                return null;
            }

            var childPidls = new IntPtr[paths.Count];
            for (int i = 0; i < paths.Count; i++)
            {
                var g = typeof(IShellFolder).GUID;
                SHBindToParent(pidls[i], ref g, out _, out childPidls[i]);
            }

            var iidCm = typeof(IContextMenu).GUID;
            // hwndOwner 传 IntPtr.Zero：绝大多数 shell 扩展不依赖它，
            // 这样可以让缓存与具体窗口无关
            parent.GetUIObjectOf(IntPtr.Zero, (uint)childPidls.Length,
                childPidls, ref iidCm, IntPtr.Zero, out IntPtr cmPtr);
            if (cmPtr == IntPtr.Zero)
            {
                foreach (var p in pidls) try { Marshal.FreeCoTaskMem(p); } catch { }
                return null;
            }

            cm = (IContextMenu)Marshal.GetObjectForIUnknown(cmPtr);
            Marshal.Release(cmPtr);

            hMenu = CreatePopupMenu();
            if (hMenu == IntPtr.Zero)
            {
                try { Marshal.ReleaseComObject(cm); } catch { }
                foreach (var p in pidls) try { Marshal.FreeCoTaskMem(p); } catch { }
                return null;
            }

            cm.QueryContextMenu(hMenu, 0, ShellIdFirst, ShellIdLast, CMF_NORMAL);

            return new CachedMenu
            {
                HMenu = hMenu,
                Cm = cm,
                Pidls = pidls,
                IdFirst = ShellIdFirst,
                IdLast = ShellIdLast,
            };
        }
        catch
        {
            if (hMenu != IntPtr.Zero) try { DestroyMenu(hMenu); } catch { }
            if (cm != null) try { Marshal.ReleaseComObject(cm); } catch { }
            foreach (var p in pidls) try { Marshal.FreeCoTaskMem(p); } catch { }
            return null;
        }
    }

    private static void FreeCached(CachedMenu c)
    {
        try { if (c.HMenu != IntPtr.Zero) DestroyMenu(c.HMenu); } catch { }
        foreach (var p in c.Pidls) try { Marshal.FreeCoTaskMem(p); } catch { }
        try { Marshal.ReleaseComObject(c.Cm); } catch { }
    }

    private static string BuildCacheKey(IReadOnlyList<string> paths)
    {
        var sb = new System.Text.StringBuilder(64);
        foreach (var p in paths)
        {
            sb.Append(p).Append('\u0001');
        }
        return sb.ToString();
    }

    // ====================================================================
    // HMENU 克隆
    // ====================================================================

    private static IntPtr CloneMenu(IntPtr src)
    {
        IntPtr dst = CreatePopupMenu();
        if (dst == IntPtr.Zero) return IntPtr.Zero;

        try
        {
            int count = GetMenuItemCount(src);
            if (count <= 0) return dst;

            for (int i = 0; i < count; i++)
            {
                var mii = new MENUITEMINFO
                {
                    cbSize = (uint)Marshal.SizeOf<MENUITEMINFO>(),
                    fMask = MIIM_FTYPE | MIIM_ID | MIIM_STATE | MIIM_STRING
                          | MIIM_SUBMENU | MIIM_DATA | MIIM_BITMAP | MIIM_CHECKMARKS,
                    cch = 0,
                    dwTypeData = IntPtr.Zero,
                };

                if (!GetMenuItemInfo(src, (uint)i, true, ref mii))
                    continue;

                IntPtr strBuf = IntPtr.Zero;
                try
                {
                    // 取字符串：第一次拿到 cch（长度，不含 null）
                    if (mii.cch > 0)
                    {
                        int bytes = ((int)mii.cch + 1) * 2;
                        strBuf = Marshal.AllocHGlobal(bytes);
                        for (int b = 0; b < bytes; b++) Marshal.WriteByte(strBuf, b, 0);

                        mii.dwTypeData = strBuf;
                        mii.cch += 1;   // 含 null
                        if (!GetMenuItemInfo(src, (uint)i, true, ref mii))
                            continue;
                    }

                    // 递归克隆子菜单
                    IntPtr subClone = IntPtr.Zero;
                    if (mii.hSubMenu != IntPtr.Zero)
                    {
                        subClone = CloneMenu(mii.hSubMenu);
                        mii.hSubMenu = subClone;
                    }

                    bool ok = InsertMenuItem(dst, (uint)i, true, ref mii);
                    if (!ok && subClone != IntPtr.Zero)
                    {
                        try { DestroyMenu(subClone); } catch { }
                    }
                }
                finally
                {
                    if (strBuf != IntPtr.Zero) Marshal.FreeHGlobal(strBuf);
                }
            }
            return dst;
        }
        catch
        {
            try { DestroyMenu(dst); } catch { }
            return IntPtr.Zero;
        }
    }

    // ====================================================================
    // Shell 变更监听
    // ====================================================================

    private static void EnsureShellHook()
    {
        if (Interlocked.Exchange(ref _hookState, 1) == 1) return;

        ShellChangeNotifier.AssociationChanged += ClearCache;

        // HwndSource 需要在 STA/UI 线程创建
        try { ShellChangeNotifier.Start(); }
        catch
        {
            // 挂不上监听也不影响主流程（只是缓存不会自动失效）
            try { ShellChangeNotifier.AssociationChanged -= ClearCache; } catch { }
            Interlocked.Exchange(ref _hookState, 0);
        }
    }

    /// <summary>监听 SHCNE_ASSOCCHANGED → 清缓存。</summary>
    private static class ShellChangeNotifier
    {
        public static event Action? AssociationChanged;

        private static HwndSource? _source;
        private static uint _notifyId;
        private static bool _started;

        private const int WM_SHELLCHANGE = 0x8000;
        private const int SHCNRF_InterruptLevel = 0x0001;
        private const int SHCNRF_ShellLevel = 0x0002;
        private const uint SHCNE_ASSOCCHANGED = 0x08000000;

        [StructLayout(LayoutKind.Sequential)]
        private struct SHChangeNotifyEntry
        {
            public IntPtr pidl;
            [MarshalAs(UnmanagedType.Bool)] public bool fRecursive;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint SHChangeNotifyRegister(
            IntPtr hwnd, int fSources, uint fEvents, uint wMsg,
            int cEntries,
            [MarshalAs(UnmanagedType.LPArray)] SHChangeNotifyEntry[] pshcne);

        [DllImport("shell32.dll")]
        private static extern bool SHChangeNotifyDeregister(uint hNotify);

        public static void Start()
        {
            if (_started) return;
            _started = true;

            var p = new HwndSourceParameters("AyDesktopShellNotify")
            {
                Width = 0,
                Height = 0,
                WindowStyle = 0,
            };
            _source = new HwndSource(p);
            _source.AddHook(WndProc);

            var entries = new[]
            {
                new SHChangeNotifyEntry { pidl = IntPtr.Zero, fRecursive = true }
            };

            _notifyId = SHChangeNotifyRegister(
                _source.Handle,
                SHCNRF_InterruptLevel | SHCNRF_ShellLevel,
                SHCNE_ASSOCCHANGED,
                WM_SHELLCHANGE,
                entries.Length,
                entries);
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;

            if (_notifyId != 0)
            {
                try { SHChangeNotifyDeregister(_notifyId); } catch { }
                _notifyId = 0;
            }

            if (_source != null)
            {
                try
                {
                    _source.RemoveHook(WndProc);
                    _source.Dispose();
                }
                catch { }
                _source = null;
            }
        }

        private static IntPtr WndProc(IntPtr hwnd, int msg,
            IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == WM_SHELLCHANGE)
            {
                handled = true;
                try { AssociationChanged?.Invoke(); } catch { }
            }
            return IntPtr.Zero;
        }
    }

    // ====================================================================
    // 属性对话框
    // ====================================================================

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