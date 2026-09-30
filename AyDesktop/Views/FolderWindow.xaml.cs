using AyDesktop.Models;
using AyDesktop.Services;
using AyDesktop.ViewModels;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AyDesktop.Views;

public partial class FolderWindow : Window, IItemHost
{
    private const double IconWidth = 96;
    private const double IconHeight = 110;
    private const double CanvasOffset = 0;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private readonly DesktopItem _folder;
    private readonly DesktopViewModel _vm;
    private readonly bool _isModal;

    private bool _isDragging;
    private bool _dragMoved;
    private Point _dragStartScreen;
    private Dictionary<DesktopItem, Point>? _dragOrigins;

    private bool _isRubberBanding;
    private Point _rubberStart;

    private DesktopItem? _contextItem;

    public FolderWindow(DesktopItem folder, DesktopViewModel vm, bool isModal = false)
    {
        InitializeComponent();
        _folder = folder;
        _vm = vm;
        _isModal = isModal;
        DataContext = folder;
        TitleText.Text = folder.Name;
        Title = folder.Name;

        if (isModal)
            ApplyModalLayout();
    }

    private void ApplyModalLayout()
    {
        var wa = SystemParameters.WorkArea;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = wa.Left;
        Top = wa.Top;
        Width = wa.Width;
        Height = wa.Height;

        OverlayMask.Visibility = Visibility.Visible;
    }

    private void OverlayMask_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _vm.Save();
        Close();
    }

    // ================ IItemHost 显式转发 ================
    void IItemHost.OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => Item_MouseLeftButtonDown(sender, e);

    void IItemHost.OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Item_MouseLeftButtonUp(sender, e);

    void IItemHost.OnItemMouseMove(object sender, MouseEventArgs e)
        => Item_MouseMove(sender, e);

    void IItemHost.OnItemMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => Item_MouseRightButtonDown(sender, e);

    void IItemHost.OnSubItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }
    void IItemHost.OnSubItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e) { }
    void IItemHost.OnSubItemMouseMove(object sender, MouseEventArgs e) { }
    void IItemHost.OnSubItemMouseRightButtonDown(object sender, MouseButtonEventArgs e) { }

    // ================ DPI 辅助 ================
    private Matrix GetDipFromScreenScale()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null) return Matrix.Identity;
        return src.CompositionTarget.TransformFromDevice;
    }

    // ================ 窗口拖动 ================
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_isModal) return;

        if (e.LeftButton == MouseButtonState.Pressed)
            try { DragMove(); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _vm.Save();
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { _vm.Save(); Close(); return; }
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            foreach (var i in _folder.Children) i.IsSelected = true;
            e.Handled = true; return;
        }
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }

    // ================ 拖拽 ================
    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DesktopItem item) return;
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            if (item.IsFolder) return;
            OpenItem(item);
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl || shift) item.IsSelected = true;
        else if (!item.IsSelected) { ClearSelection(); item.IsSelected = true; }

        _isDragging = true;
        _dragMoved = false;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragOrigins = _folder.Children.Where(i => i.IsSelected)
            .ToDictionary(i => i, i => new Point(i.X, i.Y));

        Mouse.Capture(fe, CaptureMode.Element);
    }

    private void Item_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragOrigins == null) return;

        var cur = PointToScreen(e.GetPosition(this));
        var scale = GetDipFromScreenScale();
        var dx = (cur.X - _dragStartScreen.X) * scale.M11;
        var dy = (cur.Y - _dragStartScreen.Y) * scale.M22;

        if (!_dragMoved && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) _dragMoved = true;
        if (!_dragMoved) return;

        foreach (var kv in _dragOrigins)
        {
            kv.Key.X = Math.Max(0, kv.Value.X + dx);
            kv.Key.Y = Math.Max(0, kv.Value.Y + dy);
        }

        UpdateSnapPreview();
    }

    private void UpdateSnapPreview()
    {
        if (!_vm.Config.SnapToGrid || _dragOrigins == null || _dragOrigins.Count == 0)
        { SnapPreview.Visibility = Visibility.Collapsed; return; }

        var first = _dragOrigins.Keys.First();
        var snap = SnapToFolderGrid(first.X, first.Y);
        Canvas.SetLeft(SnapPreview, snap.X);
        Canvas.SetTop(SnapPreview, snap.Y);
        SnapPreview.Visibility = Visibility.Visible;
    }

    private static Point SnapToFolderGrid(double x, double y)
    {
        double cw = DesktopViewModel.FolderCellWidth;
        double ch = DesktopViewModel.FolderCellHeight;
        return new Point(
            Math.Max(0, Math.Round(x / cw) * cw),
            Math.Max(0, Math.Round(y / ch) * ch));
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (Mouse.Captured != null) Mouse.Capture(null);
        SnapPreview.Visibility = Visibility.Collapsed;

        bool moved = _dragMoved;
        _dragOrigins = null;
        _dragMoved = false;

        if (moved)
        {
            var sel = _folder.Children.Where(i => i.IsSelected).ToList();
            if (_vm.Config.SnapToGrid)
            {
                var first = sel.FirstOrDefault();
                if (first != null)
                {
                    var snapped = SnapToFolderGrid(first.X, first.Y);
                    double dx = snapped.X - first.X;
                    double dy = snapped.Y - first.Y;
                    foreach (var it in sel)
                    {
                        it.X = Math.Max(0, it.X + dx);
                        it.Y = Math.Max(0, it.Y + dy);
                    }
                }
            }
            _vm.Save();
        }
    }

    private void Item_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DesktopItem item) return;
        if (!item.IsSelected) { ClearSelection(); item.IsSelected = true; }
        _contextItem = item;

        GetCursorPos(out var pt);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        var custom = new List<(string, Action)>
        {
            ("重命名", () => RenameItem(item)),
            ("从文件夹移除", () => RemoveFromFolder(item)),
            ("删除", () => DeleteSelected()),
        };

        ShellContextMenu.ShowNative(hwnd, new[] { item.FullPath }, custom, pt.X, pt.Y);
        _vm.Save();
    }

    // ================ 操作 ================
    private void OpenItem(DesktopItem item)
    {
        try
        {
            if (item.IsDirectory)
                Process.Start("explorer.exe", $"\"{item.FullPath}\"");
            else
                Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法打开：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void OpenLocation(DesktopItem item)
    {
        try
        {
            if (item.IsDirectory) Process.Start("explorer.exe", $"\"{item.FullPath}\"");
            else Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        }
        catch { }
    }

    private void RemoveFromFolder(DesktopItem item)
    {
        if (!_folder.Children.Contains(item)) return;

        _folder.Children.Remove(item);
        var slot = _vm.FindFreeSlot();
        item.X = slot.X;
        item.Y = slot.Y;
        _vm.Items.Add(item);
        _vm.Save();
    }

    private void RenameItem(DesktopItem item)
    {
        if (item.IsSystemIcon) return;
        var dlg = new RenameDialog(item.Name) { Owner = this };
        if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.NewName)) return;

        try
        {
            var dir = Path.GetDirectoryName(item.FullPath)!;
            var newPath = Path.Combine(dir, dlg.NewName);

            if (item.IsDirectory) Directory.Move(item.FullPath, newPath);
            else File.Move(item.FullPath, newPath);

            item.FullPath = newPath;
            item.Name = dlg.NewName;
            item.Icon = IconExtractor.GetIcon(newPath);
            item.IsNeverShowExt = ShellSettings.IsNeverShowExt(item.Extension);
            _vm.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重命名失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearSelection()
    {
        foreach (var i in _folder.Children) i.IsSelected = false;
    }

    private void DeleteSelected()
    {
        var sel = _folder.Children.Where(i => i.IsSelected && !i.IsSystemIcon).ToList();
        if (sel.Count == 0) return;

        var confirm = MessageBox.Show(
            $"确定要删除选中的 {sel.Count} 个项目吗？\n（将移入回收站）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in sel)
        {
            try { MoveToRecycleBin(item.FullPath); _folder.Children.Remove(item); }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{item.Name}\n{ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        _vm.Save();
    }

    [DllImport("Shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd; public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags; public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszProgressTitle;
    }

    private const uint FO_DELETE = 0x0003;
    private const ushort FOF_ALLOWUNDO = 0x0040;
    private const ushort FOF_NOCONFIRMATION = 0x0010;
    private const ushort FOF_SILENT = 0x0004;

    private static void MoveToRecycleBin(string path)
    {
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = path + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        };
        int ret = SHFileOperation(ref op);
        if (ret != 0) throw new IOException($"Shell 删除失败，错误码 {ret}");
    }

    // ================ 框选 ================
    private void Canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not Canvas canvas) return;
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) ClearSelection();

        _isRubberBanding = true;
        _rubberStart = e.GetPosition(RootGrid);
        Canvas.SetLeft(RubberBand, _rubberStart.X);
        Canvas.SetTop(RubberBand, _rubberStart.Y);
        RubberBand.Width = 0; RubberBand.Height = 0;
        RubberBand.Visibility = Visibility.Visible;
        Mouse.Capture(canvas, CaptureMode.Element);
        e.Handled = true;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isRubberBanding) return;
        var current = e.GetPosition(RootGrid);
        double x = Math.Min(_rubberStart.X, current.X);
        double y = Math.Min(_rubberStart.Y, current.Y);
        double w = Math.Abs(current.X - _rubberStart.X);
        double h = Math.Abs(current.Y - _rubberStart.Y);
        Canvas.SetLeft(RubberBand, x);
        Canvas.SetTop(RubberBand, y);
        RubberBand.Width = w; RubberBand.Height = h;
    }

    private void Canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isRubberBanding) return;
        _isRubberBanding = false;
        RubberBand.Visibility = Visibility.Collapsed;
        if (Mouse.Captured != null) Mouse.Capture(null);

        var rect = new Rect(Canvas.GetLeft(RubberBand), Canvas.GetTop(RubberBand),
                            RubberBand.Width, RubberBand.Height);
        if (rect.Width < 2 && rect.Height < 2) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        foreach (var item in _folder.Children)
        {
            var itemRect = new Rect(item.X, item.Y, IconWidth, IconHeight);
            if (rect.IntersectsWith(itemRect)) item.IsSelected = true;
            else if (!ctrl) item.IsSelected = false;
        }
    }

    // ================ 空白菜单 ================
    private void Menu_AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        const double cw = DesktopViewModel.FolderCellWidth;
        const double ch = DesktopViewModel.FolderCellHeight;
        const int maxRows = DesktopViewModel.FolderMaxRows;

        double x = 0;
        int row = 0;
        foreach (var item in _folder.Children)
        {
            item.X = x;
            item.Y = row * ch;
            row++;
            if (row >= maxRows) { row = 0; x += cw; }
        }
        _vm.Save();
    }

    private void Menu_NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var sel = _folder.Children.Where(i => i.IsSelected && !i.IsSystemIcon).ToList();
        if (sel.Count < 2)
        {
            MessageBox.Show("请先选中两个或更多图标。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var valid = sel.Where(i => !i.IsSystemIcon).ToList();
        var first = valid[0];
        double fx = first.X, fy = first.Y;

        string baseName = "新建文件夹";
        string name = baseName;
        int n = 1;
        while (_folder.Children.Any(c => c.IsFolder && c.Name == name))
            name = $"{baseName} ({n++})";

        var sub = new DesktopItem
        {
            Name = name,
            FullPath = "::folder:" + Guid.NewGuid().ToString("N"),
            IsFolder = true,
            IsDirectory = true,
            X = fx,
            Y = fy,
            Icon = IconExtractor.GetFolderIcon(),
        };

        foreach (var it in valid)
        {
            _folder.Children.Remove(it);
            sub.Children.Add(it);
        }

        _vm.AppendChildrenToFolder(sub, valid);

        _folder.Children.Add(sub);
        _vm.Save();
    }
}