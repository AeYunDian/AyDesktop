using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using AyDesktop.Models;
using AyDesktop.Services;

namespace AyDesktop.ViewModels;

/// <summary>系统图标定义</summary>
public class SystemIconDef
{
    public string Name { get; init; } = "";
    public string Target { get; init; } = "";
    public string? Clsid { get; init; }
}

public class DesktopViewModel : INotifyPropertyChanged
{
    public const double IconWidth = 96;
    public const double IconHeight = 110;

    // ★ 文件夹内网格：比桌面更紧凑
    public const double FolderCellWidth = 110;
    public const double FolderCellHeight = 130;
    public const int FolderMaxRows = 4;

    private readonly ConfigService _configService = new();
    private readonly DesktopMonitorService _monitor = new();

    public ObservableCollection<DesktopItem> Items { get; } = new();
    public AppConfig Config { get; private set; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public static readonly SystemIconDef[] SystemIconDefs = new[]
    {
        new SystemIconDef { Name = "此电脑",
            Target = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            Clsid = "{20D04FE0-3AEA-1069-A2D8-08002B30309D}" },
        new SystemIconDef { Name = "回收站",
            Target = "::{645FF040-5081-101B-9F08-00AA002F954E}",
            Clsid = "{645FF040-5081-101B-9F08-00AA002F954E}" },
        new SystemIconDef { Name = "网络",
            Target = "::{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}",
            Clsid = "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}" },
        new SystemIconDef { Name = "用户文件夹",
            Target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Clsid = null },
        new SystemIconDef { Name = "控制面板",
            Target = "::{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}",
            Clsid = "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}" },
    };

    public DesktopViewModel()
    {
        _monitor.FileChanged += (path, changeType) =>
            Application.Current?.Dispatcher.Invoke(() => HandleFileChange(path, changeType));
    }

    public double CellWidth => Config.GridSize + 24;
    public double CellHeight => Config.GridSize + 60;

    public Brush BackgroundBrush
    {
        get
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(Config.BackgroundColor);
                c.A = (byte)(Math.Clamp(Config.BackgroundOpacity, 0, 1) * 255);
                return new SolidColorBrush(c);
            }
            catch { return Brushes.Transparent; }
        }
    }
    public Visibility BackgroundVisibility =>
        Config.ShowBackground ? Visibility.Visible : Visibility.Collapsed;

    // ================ 三态 ================
    public bool ShouldShowExtensions() => Config.ShowFileExtensions switch
    {
        TriState.On => true,
        TriState.Off => false,
        _ => ShellSettings.SystemShowsExtensions,
    };
    public bool ShouldShowHidden() => Config.ShowHiddenFiles switch
    {
        TriState.On => true,
        TriState.Off => false,
        _ => ShellSettings.SystemShowsHiddenFiles,
    };
    public bool ShouldShowSystemIcons() => Config.ShowSystemIcons switch
    {
        TriState.On => true,
        TriState.Off => false,
        _ => true,
    };

    public void LoadConfig()
    {
        Config = _configService.LoadConfig();

        // ★ 首次运行（无配置）：默认监听 用户桌面 + 公共桌面，并写回磁盘
        if (Config.MonitoredFolders.Count == 0)
        {
            var userDesktop = IconExtractor.GetDesktopPath();
            if (!string.IsNullOrEmpty(userDesktop))
                Config.MonitoredFolders.Add(userDesktop);

            var publicDesktop = IconExtractor.GetPublicDesktopPath();
            if (!string.IsNullOrEmpty(publicDesktop))
                Config.MonitoredFolders.Add(publicDesktop);

            try { _configService.SaveConfig(Config); } catch { }
        }

        OnP(nameof(BackgroundBrush));
        OnP(nameof(BackgroundVisibility));
        OnP(nameof(CellWidth));
        OnP(nameof(CellHeight));
    }

    // ================ 加载 ================
    public void LoadDesktopItems()
    {
        var saved = _configService.LoadDesktopItems();
        bool hadSavedData = saved.Count > 0;
        Items.Clear();

        if (ShouldShowSystemIcons())
        {
            foreach (var def in SystemIconDefs)
            {
                var existing = saved.FirstOrDefault(i => i.IsSystem && i.FullPath == def.Target);
                if (existing != null) { Items.Add(existing); saved.Remove(existing); }
                else
                {
                    var slot = FindFreeSlot();
                    Items.Add(new DesktopItem
                    {
                        Name = def.Name,
                        FullPath = def.Target,
                        Clsid = def.Clsid ?? "",
                        IsSystem = true,
                        IsDirectory = true,
                        X = slot.X,
                        Y = slot.Y,
                    });
                }
            }
        }

        foreach (var i in saved)
        {
            if (i.IsSystem && SystemIconDefs.Any(d => d.Target == i.FullPath)) continue;
            Items.Add(i);
            if (i.IsFolder) ApplyFlagsRecursive(i.Children);
        }

        if (!hadSavedData)
        {
            ScanFolders();
            if (Config.AutoArrange) AutoArrange();
        }

        ApplyShowExtension();
        ApplyFolderMode();
        LoadAllIcons();
    }

    private void ApplyFlagsRecursive(IEnumerable<DesktopItem> items)
    {
        bool showExt = ShouldShowExtensions();
        foreach (var it in items)
        {
            it.ShowExtension = showExt;
            it.IsNeverShowExt = !it.IsSystemIcon && ShellSettings.IsNeverShowExt(it.Extension);
            if (it.IsFolder) ApplyFlagsRecursive(it.Children);
        }
    }

    private void ApplyFolderMode()
    {
        foreach (var i in Items)
            ApplyFolderModeRecursive(i);
    }

    private void ApplyFolderModeRecursive(DesktopItem item)
    {
        if (item.IsFolder)
        {
            // 优先用文件夹自身的覆盖样式，否则跟随默认
            var mode = item.FolderModeOverrideValue ?? Config.FolderMode;
            item.IsGroupMode = mode == FolderMode.Group;
        }

        foreach (var c in item.Children)
            ApplyFolderModeRecursive(c);
    }

    private void ScanFolders()
    {
        bool showHidden = ShouldShowHidden();
        bool showExt = ShouldShowExtensions();

        foreach (var folder in Config.MonitoredFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.GetFileSystemEntries(folder))
            {
                if (!showHidden && ShellSettings.IsHiddenOrSystem(path)) continue;

                bool isDir = Directory.Exists(path);
                var ext = Path.GetExtension(path);
                var slot = FindFreeSlot();

                Items.Add(new DesktopItem
                {
                    Name = Path.GetFileName(path),
                    FullPath = path,
                    IsDirectory = isDir,
                    GroupName = isDir ? "文件夹" : GetGroupByExtension(ext),
                    ShowExtension = showExt,
                    IsNeverShowExt = ShellSettings.IsNeverShowExt(ext),
                    X = slot.X,
                    Y = slot.Y,
                });
            }
        }
    }

    // ================ 增量刷新 ================
    public void RefreshDesktopItems()
    {
        SyncSystemIcons();

        bool showHidden = ShouldShowHidden();
        var diskPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in Config.MonitoredFolders)
        {
            if (!Directory.Exists(folder)) continue;
            foreach (var path in Directory.GetFileSystemEntries(folder))
            {
                if (!showHidden && ShellSettings.IsHiddenOrSystem(path)) continue;
                diskPaths.Add(path);
            }
        }

        var existingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items)
        {
            if (i.IsSystemIcon || i.IsFolder) continue;
            existingPaths.Add(i.FullPath);
        }
        foreach (var folder in Items.Where(i => i.IsFolder))
        {
            foreach (var c in folder.Children)
            {
                if (c.IsSystemIcon) continue;
                existingPaths.Add(c.FullPath);
            }
        }

        var toRemove = Items.Where(i => !i.IsSystemIcon && !i.IsFolder
                                     && !diskPaths.Contains(i.FullPath)).ToList();
        foreach (var item in toRemove) Items.Remove(item);

        bool showExt = ShouldShowExtensions();
        foreach (var path in diskPaths)
        {
            if (existingPaths.Contains(path)) continue;
            bool isDir = Directory.Exists(path);
            var ext = Path.GetExtension(path);
            var slot = FindFreeSlot();
            Items.Add(new DesktopItem
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = isDir,
                Icon = IconExtractor.GetIcon(path),
                GroupName = isDir ? "文件夹" : GetGroupByExtension(ext),
                ShowExtension = showExt,
                IsNeverShowExt = ShellSettings.IsNeverShowExt(ext),
                X = slot.X,
                Y = slot.Y,
            });
        }

        foreach (var item in Items)
        {
            if (item.IsSystemIcon || item.IsFolder) continue;
            item.ShowExtension = showExt;
            item.IsNeverShowExt = ShellSettings.IsNeverShowExt(item.Extension);
        }
    }

    private void SyncSystemIcons()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var duplicates = new List<DesktopItem>();
        foreach (var i in Items.Where(x => x.IsSystem))
        {
            if (!seen.Add(i.FullPath)) duplicates.Add(i);
        }
        foreach (var d in duplicates) Items.Remove(d);

        bool show = ShouldShowSystemIcons();

        var existingByTarget = new Dictionary<string, DesktopItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var i in Items.Where(x => x.IsSystem))
            existingByTarget[i.FullPath] = i;

        if (!show)
        {
            foreach (var i in existingByTarget.Values.ToList()) Items.Remove(i);
            return;
        }

        foreach (var def in SystemIconDefs)
        {
            if (existingByTarget.ContainsKey(def.Target)) continue;
            var slot = FindFreeSlot();
            Items.Add(new DesktopItem
            {
                Name = def.Name,
                FullPath = def.Target,
                Clsid = def.Clsid ?? "",
                IsSystem = true,
                IsDirectory = true,
                Icon = LoadSystemIcon(def),
                X = slot.X,
                Y = slot.Y,
            });
        }
    }

    private ImageSource? LoadSystemIcon(SystemIconDef def)
        => !string.IsNullOrEmpty(def.Clsid)
            ? IconExtractor.GetSystemIcon(def.Clsid!)
            : IconExtractor.GetIcon(def.Target);

    private void ApplyShowExtension() => ApplyFlagsRecursive(Items);

    private void LoadAllIcons()
    {
        foreach (var item in Items) LoadIconRecursive(item);
    }

    private void LoadIconRecursive(DesktopItem item)
    {
        if (item.IsFolder) item.Icon = IconExtractor.GetFolderIcon();
        else if (item.IsSystem)
        {
            item.Icon = !string.IsNullOrEmpty(item.Clsid)
                ? IconExtractor.GetSystemIcon(item.Clsid)
                : IconExtractor.GetIcon(item.FullPath);
        }
        else if (File.Exists(item.FullPath) || Directory.Exists(item.FullPath))
            item.Icon = IconExtractor.GetIcon(item.FullPath);

        foreach (var c in item.Children) LoadIconRecursive(c);
    }

    // ================ 文件夹操作 ================
    public DesktopItem? CreateFolderFrom(List<DesktopItem> items, string name = "新建文件夹")
    {
        var valid = items.Where(i => !i.IsSystemIcon).ToList();
        if (valid.Count < 2) return null;

        var first = valid[0];
        double fx = first.X, fy = first.Y;

        string baseName = name;
        int n = 1;
        while (Items.Any(i => i.IsFolder && i.Name == name))
            name = $"{baseName} ({n++})";

        var folder = new DesktopItem
        {
            Name = name,
            FullPath = "::folder:" + Guid.NewGuid().ToString("N"),
            IsFolder = true,
            IsDirectory = true,
            X = fx,
            Y = fy,
            Icon = IconExtractor.GetFolderIcon(),
            IsGroupMode = Config.FolderMode == FolderMode.Group,
        };

        foreach (var it in valid)
        {
            Items.Remove(it);
            folder.Children.Add(it);
        }

        // ★ 给新子项在文件夹内排位置（桌面坐标不能直接带进文件夹）
        AppendChildrenToFolder(folder, valid);

        Items.Add(folder);
        return folder;
    }

    /// <summary>
    /// 把新子项追加进文件夹，按文件夹内网格找空位。
    /// 已经在文件夹里的子项位置不动。
    /// </summary>
    public void AppendChildrenToFolder(
        DesktopItem folder, IEnumerable<DesktopItem> newChildren)
    {
        if (!folder.IsFolder) return;

        var incoming = newChildren.Where(c => c != folder).ToList();
        if (incoming.Count == 0) return;

        const double cw = FolderCellWidth;
        const double ch = FolderCellHeight;
        const int maxRows = FolderMaxRows;

        var used = new HashSet<(int col, int row)>();
        foreach (var c in folder.Children)
        {
            if (incoming.Contains(c)) continue;
            int col = Math.Max(0, (int)Math.Round(c.X / cw));
            int row = Math.Max(0, (int)Math.Round(c.Y / ch));
            used.Add((col, row));
        }

        int cc = 0, rr = 0;
        foreach (var item in incoming)
        {
            while (used.Contains((cc, rr)))
            {
                rr++;
                if (rr >= maxRows) { rr = 0; cc++; }
            }
            item.X = cc * cw;
            item.Y = rr * ch;
            used.Add((cc, rr));
        }
    }

    public void UnpackFolder(DesktopItem folder)
    {
        if (!folder.IsFolder) return;
        var children = folder.Children.ToList();
        folder.Children.Clear();
        Items.Remove(folder);
        foreach (var c in children)
        {
            var slot = FindFreeSlot();
            c.X = slot.X; c.Y = slot.Y;
            Items.Add(c);
        }
    }

    public void RenameFolder(DesktopItem folder, string newName)
    {
        if (!folder.IsFolder) return;
        if (string.IsNullOrWhiteSpace(newName)) return;
        if (Items.Any(i => i.IsFolder && i != folder && i.Name == newName)) return;
        folder.Name = newName;
    }
    // ================ ★ 文件夹样式切换 ================

    /// <summary>
    /// 设置文件夹的样式覆盖。<paramref name="mode"/> = null 表示跟随默认。
    /// 切换后自动按新网格重排子项。
    /// </summary>
    public void SetFolderMode(DesktopItem folder, FolderMode? mode)
    {
        if (!folder.IsFolder) return;

        folder.FolderModeOverrideValue = mode;

        var effective = mode ?? Config.FolderMode;
        folder.IsGroupMode = effective == FolderMode.Group;

        RelayoutFolderChildren(folder);
        Save();
    }

    /// <summary>按当前组/全屏模式重排文件夹内的子项到网格位置</summary>
    private void RelayoutFolderChildren(DesktopItem folder)
    {
        if (!folder.IsFolder) return;

        if (folder.IsGroupMode)
        {
            // 组模式：2 列网格，与 GroupFolderTemplate 一致
            const double cw = DesktopItem.GroupCellWidth;
            const double ch = DesktopItem.GroupCellHeight;
            const int cols = DesktopItem.GroupColumnsFixed;

            int i = 0;
            foreach (var c in folder.Children)
            {
                c.X = (i % cols) * cw;
                c.Y = (i / cols) * ch;
                i++;
            }
        }
        else
        {
            // 全屏模式：文件夹内网格，与 FolderWindow 一致
            const double cw = FolderCellWidth;
            const double ch = FolderCellHeight;
            const int maxRows = FolderMaxRows;

            int i = 0;
            foreach (var c in folder.Children)
            {
                c.X = (i / maxRows) * cw;
                c.Y = (i % maxRows) * ch;
                i++;
            }
        }
    }
    // ================ 空位 / 布局 ================
    private static (double w, double h) LayoutOf(DesktopItem i)
    {
        if (i.IsFolder && i.IsGroupMode) return (i.GroupWidth, i.GroupHeight);
        return (IconWidth, IconHeight);
    }

    public Point FindFreeSlot()
    {
        double cw = CellWidth, ch = CellHeight;
        if (cw <= 0 || ch <= 0) return new Point(0, 0);

        double maxHeight = Math.Max(ch * 2, SystemParameters.WorkArea.Height - 60);
        int maxRows = Math.Max(1, (int)(maxHeight / ch));

        for (int col = 0; col < 500; col++)
            for (int row = 0; row < maxRows; row++)
            {
                double x = col * cw, y = row * ch;
                if (!IsOverlapped(x, y)) return new Point(x, y);
            }
        return new Point(0, 0);
    }

    private bool IsOverlapped(double x, double y)
    {
        var r = new Rect(x, y, IconWidth, IconHeight);
        foreach (var i in Items)
        {
            var (w, h) = LayoutOf(i);
            var ri = new Rect(i.X, i.Y, w, h);
            if (r.IntersectsWith(ri)) return true;
        }
        return false;
    }

    public void AutoArrange()
    {
        double cw = CellWidth, ch = CellHeight;
        double maxHeight = Math.Max(ch * 2, SystemParameters.WorkArea.Height - 60);
        double x = 0, y = 0;
        foreach (var item in Items)
        {
            var (_, h) = LayoutOf(item);

            if (y > 0 && y + h > maxHeight) { y = 0; x += cw; }

            item.X = x;
            item.Y = y;
            y += h;
        }
    }

    public Point SnapPoint(double x, double y)
    {
        if (!Config.SnapToGrid) return new Point(x, y);
        double cw = CellWidth, ch = CellHeight;
        return new Point(
            Math.Max(0, Math.Round(x / cw) * cw),
            Math.Max(0, Math.Round(y / ch) * ch));
    }

    public void SnapSelectedToGrid(IEnumerable<DesktopItem> items)
    {
        if (!Config.SnapToGrid) return;
        var list = items.ToList();
        if (list.Count == 0) return;
        var first = list[0];
        var snapped = SnapPoint(first.X, first.Y);
        double dx = snapped.X - first.X, dy = snapped.Y - first.Y;
        foreach (var it in list)
        {
            it.X = Math.Max(0, it.X + dx);
            it.Y = Math.Max(0, it.Y + dy);
        }
    }

    // ================ 监控 ================
    public void StartMonitoring()
    {
        foreach (var folder in Config.MonitoredFolders)
            _monitor.AddFolder(folder);
    }

    public void StopMonitoring() => _monitor.Dispose();

    private void HandleFileChange(string path, WatcherChangeTypes changeType)
    {
        var existing = Items.FirstOrDefault(i => i.FullPath == path);
        if (changeType == WatcherChangeTypes.Deleted)
        {
            if (existing != null) Items.Remove(existing);
            return;
        }
        if (existing == null && (File.Exists(path) || Directory.Exists(path)))
        {
            if (!ShouldShowHidden() && ShellSettings.IsHiddenOrSystem(path)) return;

            foreach (var folder in Items.Where(i => i.IsFolder))
                if (folder.Children.Any(c => string.Equals(c.FullPath, path, StringComparison.OrdinalIgnoreCase)))
                    return;

            bool isDir = Directory.Exists(path);
            var slot = FindFreeSlot();
            var ext = Path.GetExtension(path);
            Items.Add(new DesktopItem
            {
                Name = Path.GetFileName(path),
                FullPath = path,
                IsDirectory = isDir,
                Icon = IconExtractor.GetIcon(path),
                GroupName = isDir ? "文件夹" : GetGroupByExtension(ext),
                ShowExtension = ShouldShowExtensions(),
                IsNeverShowExt = ShellSettings.IsNeverShowExt(ext),
                X = slot.X,
                Y = slot.Y,
            });
        }
        else if (existing != null)
        {
            existing.Icon = IconExtractor.GetIcon(path);
        }
    }

    // ================ 保存 ================
    public void Save() => _configService.SaveDesktopItems(Items.ToList());

    private static string GetGroupByExtension(string ext) => ext.ToLowerInvariant() switch
    {
        ".exe" or ".lnk" or ".bat" or ".cmd" => "应用程序",
        ".url" or ".website" => "应用程序",
        ".txt" or ".doc" or ".docx" or ".pdf" or ".xls" or ".xlsx" => "文档",
        ".jpg" or ".png" or ".gif" or ".bmp" or ".mp4" or ".mp3" => "媒体",
        ".zip" or ".rar" or ".7z" => "压缩包",
        _ => "其他"
    };

    protected void OnP(string name) => PropertyChanged?.Invoke(this, new(name));
}