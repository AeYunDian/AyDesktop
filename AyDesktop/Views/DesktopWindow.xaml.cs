using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AyDesktop.Models;
using AyDesktop.Services;
using AyDesktop.ViewModels;

namespace AyDesktop.Views;

public partial class DesktopWindow : Window, IItemHost
{
    private const double IconWidth = 96;
    private const double IconHeight = 110;
    private const double CanvasOffset = 12;

    private const double EnterOffsetX = 40;
    private const double ExitOffsetX = 40;
    private const int EnterOpacityMs = 200;
    private const int EnterSlideMs = 260;
    private const int ExitOpacityMs = 160;
    private const int ExitSlideMs = 200;

    private readonly DesktopViewModel _viewModel;
    private readonly AuthService _auth;
    private DateTime _lastDeactivated = DateTime.MinValue;
    private int _suppressHide;

    private bool _isDragging;
    private bool _dragMoved;
    private Point _dragStartScreen;
    private Dictionary<DesktopItem, Point>? _dragOrigins;
    private DesktopItem? _mergeTarget;
    private DesktopItem? _dragSourceFolder;

    private bool _isRubberBanding;
    private Point _rubberStart;
    private DesktopItem? _contextItem;
    private readonly List<FolderWindow> _openFolderWindows = new();

    public bool IsFadingOut { get; private set; }

    public bool SuppressNextShow =>
        (DateTime.Now - _lastDeactivated).TotalMilliseconds < 250;

    public DesktopWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        _viewModel = new DesktopViewModel();
        DataContext = _viewModel;
        _viewModel.LoadConfig();
        _viewModel.LoadDesktopItems();
        _viewModel.StartMonitoring();

        if (_viewModel.Config.EnableAnimations)
        {
            ItemsHost.Opacity = 0;
            ItemsTranslate.X = EnterOffsetX;
        }
        else
        {
            ItemsHost.Opacity = 1;
            ItemsTranslate.X = 0;
        }

        App.ConfigChanged += OnConfigChanged;
    }

    // ====================================================================
    // Win32
    // ====================================================================

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    private static bool IsForegroundOwnedByThisProcess()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsVirtualFolder(DesktopItem item)
        => item.IsFolder &&
           item.FullPath.StartsWith("::folder:", StringComparison.Ordinal);

    // ====================================================================
    // 进出动画
    // ====================================================================
    public void ShowWithAnimation()
    {
        if (IsVisible && !IsFadingOut)
        {
            try { Activate(); } catch { }
            return;
        }

        IsFadingOut = false;

        if (!_viewModel.Config.EnableAnimations)
        {
            ItemsHost.BeginAnimation(UIElement.OpacityProperty, null);
            ItemsTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ItemsHost.Opacity = 1;
            ItemsTranslate.X = 0;
            if (!IsVisible) Show();
            try { Activate(); } catch { }
            return;
        }

        if (!IsVisible)
        {
            ItemsHost.Opacity = 0;
            ItemsTranslate.X = EnterOffsetX;
            Show();
        }

        try { Activate(); } catch { }

        double startOpacity = ItemsHost.Opacity;
        double startX = ItemsTranslate.X;
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        var opAnim = new DoubleAnimation(startOpacity, 1, TimeSpan.FromMilliseconds(EnterOpacityMs))
        { EasingFunction = easeOut, FillBehavior = FillBehavior.Stop };
        opAnim.Completed += (_, _) => ItemsHost.Opacity = 1;

        var xAnim = new DoubleAnimation(startX, 0, TimeSpan.FromMilliseconds(EnterSlideMs))
        { EasingFunction = easeOut, FillBehavior = FillBehavior.Stop };
        xAnim.Completed += (_, _) => ItemsTranslate.X = 0;

        ItemsHost.BeginAnimation(UIElement.OpacityProperty, opAnim);
        ItemsTranslate.BeginAnimation(TranslateTransform.XProperty, xAnim);
    }

    public void HideWithAnimation()
    {
        if (!IsVisible || IsFadingOut) return;
        IsFadingOut = true;

        if (!_viewModel.Config.EnableAnimations)
        {
            IsFadingOut = false;
            ItemsHost.BeginAnimation(UIElement.OpacityProperty, null);
            ItemsTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ItemsHost.Opacity = 0;
            ItemsTranslate.X = EnterOffsetX;
            Hide();
            return;
        }

        double startOpacity = ItemsHost.Opacity;
        double startX = ItemsTranslate.X;
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

        var opAnim = new DoubleAnimation(startOpacity, 0, TimeSpan.FromMilliseconds(ExitOpacityMs))
        { EasingFunction = easeIn, FillBehavior = FillBehavior.Stop };

        var xAnim = new DoubleAnimation(startX, ExitOffsetX, TimeSpan.FromMilliseconds(ExitSlideMs))
        { EasingFunction = easeIn, FillBehavior = FillBehavior.Stop };

        opAnim.Completed += (_, _) =>
        {
            IsFadingOut = false;
            Hide();
            ItemsHost.BeginAnimation(UIElement.OpacityProperty, null);
            ItemsTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            ItemsHost.Opacity = 0;
            ItemsTranslate.X = EnterOffsetX;
        };

        ItemsHost.BeginAnimation(UIElement.OpacityProperty, opAnim);
        ItemsTranslate.BeginAnimation(TranslateTransform.XProperty, xAnim);
    }

    // ================ IItemHost 转发 ================
    void IItemHost.OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => Item_MouseLeftButtonDown(sender, e);
    void IItemHost.OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Item_MouseLeftButtonUp(sender, e);
    void IItemHost.OnItemMouseMove(object sender, MouseEventArgs e)
        => Item_MouseMove(sender, e);
    void IItemHost.OnItemMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => Item_MouseRightButtonDown(sender, e);

    void IItemHost.OnSubItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => SubItem_MouseLeftButtonDown(sender, e);
    void IItemHost.OnSubItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => SubItem_MouseLeftButtonUp(sender, e);
    void IItemHost.OnSubItemMouseMove(object sender, MouseEventArgs e)
        => SubItem_MouseMove(sender, e);
    void IItemHost.OnSubItemMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => SubItem_MouseRightButtonDown(sender, e);

    // ================ 辅助 ================
    private Matrix GetDipFromScreenScale()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget == null) return Matrix.Identity;
        return src.CompositionTarget.TransformFromDevice;
    }

    private DesktopItem? FindParentFolder(DesktopItem child)
    {
        foreach (var top in _viewModel.Items)
            if (top.IsFolder && top.Children.Contains(child)) return top;
        return null;
    }

    // ================ 窗口 ================
    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_suppressHide > 0) return;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_suppressHide > 0) return;
            if (!IsVisible) return;

            if (IsForegroundOwnedByThisProcess()) return;

            _lastDeactivated = DateTime.Now;
            HideWithAnimation();
        }), DispatcherPriority.Background);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { HideWithAnimation(); return; }
        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            foreach (var i in _viewModel.Items) i.IsSelected = true;
            e.Handled = true; return;
        }
        if (e.Key == Key.Delete) { DeleteSelected(); e.Handled = true; }
    }

    private void ShowModal(Window dialog)
    {
        _suppressHide++;
        try { dialog.Owner = this; dialog.ShowDialog(); }
        finally { _suppressHide--; }
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ConfigChanged -= OnConfigChanged;

        foreach (var w in _openFolderWindows.ToList())
            try { w.Close(); } catch { }
        _viewModel.StopMonitoring();
        _viewModel.Save();
        base.OnClosed(e);
    }

    private void OnConfigChanged()
    {
        Dispatcher.InvokeAsync(() =>
        {
            try { ReloadFromConfig(); } catch { }
        });
    }

    public void ReloadFromConfig()
    {
        foreach (var w in _openFolderWindows.ToList())
        {
            try { w.Close(); } catch { }
        }
        _openFolderWindows.Clear();

        _viewModel.StopMonitoring();
        _viewModel.LoadConfig();
        _viewModel.LoadDesktopItems();
        _viewModel.StartMonitoring();
    }

    // ================ 桌面级拖拽 ================
    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DesktopItem item) return;
        e.Handled = true;

        if (e.ClickCount == 2)
        {
            if (item.IsFolder)
            {
                if (!item.IsGroupMode)
                    OpenFolderWindow(item);
            }
            else OpenItem(item);
            return;
        }

        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl || shift) item.IsSelected = true;
        else if (!item.IsSelected) { ClearSelection(); item.IsSelected = true; }

        _isDragging = true;
        _dragMoved = false;
        _dragSourceFolder = null;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragOrigins = _viewModel.Items.Where(i => i.IsSelected)
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

        UpdateMergeHintAt(e.GetPosition(RootGrid));
        UpdateSnapPreview();
    }

    private void UpdateMergeHintAt(Point mouseInRoot)
    {
        _mergeTarget = null;

        if (!_viewModel.Config.AutoCreateFolder || _dragOrigins == null || _dragOrigins.Count == 0)
        { MergeHint.Visibility = Visibility.Collapsed; return; }

        var dragged = _dragOrigins.Keys.ToHashSet();
        if (dragged.Any(d => d.IsFolder && d.IsGroupMode))
        { MergeHint.Visibility = Visibility.Collapsed; return; }

        double testX = mouseInRoot.X - CanvasOffset;
        double testY = mouseInRoot.Y - CanvasOffset;

        foreach (var other in _viewModel.Items)
        {
            if (dragged.Contains(other)) continue;
            if (other.IsSystemIcon) continue;

            double w = other.LayoutWidth, h = other.LayoutHeight;
            if (new Rect(other.X, other.Y, w, h).Contains(testX, testY))
            {
                _mergeTarget = other;
                MergeHint.Width = w;
                MergeHint.Height = h;
                Canvas.SetLeft(MergeHint, CanvasOffset + other.X);
                Canvas.SetTop(MergeHint, CanvasOffset + other.Y);
                MergeHint.Visibility = Visibility.Visible;
                return;
            }
        }
        MergeHint.Visibility = Visibility.Collapsed;
    }

    private void UpdateSnapPreview()
    {
        if (_dragOrigins == null || _dragOrigins.Count == 0)
        {
            SnapPreview.Visibility = Visibility.Collapsed;
            return;
        }

        if (_mergeTarget != null && _mergeTarget.IsFolder && _mergeTarget.IsGroupMode)
        {
            SnapPreview.Width = _mergeTarget.GroupWidth;
            SnapPreview.Height = _mergeTarget.GroupHeight;
            Canvas.SetLeft(SnapPreview, CanvasOffset + _mergeTarget.X);
            Canvas.SetTop(SnapPreview, CanvasOffset + _mergeTarget.Y);
            SnapPreview.Visibility = Visibility.Visible;
            return;
        }

        if (!_viewModel.Config.SnapToGrid)
        {
            SnapPreview.Visibility = Visibility.Collapsed;
            return;
        }

        var first = _dragOrigins.Keys.First();
        var snap = _viewModel.SnapPoint(first.X, first.Y);

        SnapPreview.Width = first.LayoutWidth;
        SnapPreview.Height = first.LayoutHeight;

        Canvas.SetLeft(SnapPreview, CanvasOffset + snap.X);
        Canvas.SetTop(SnapPreview, CanvasOffset + snap.Y);
        SnapPreview.Visibility = Visibility.Visible;
    }

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (Mouse.Captured != null) Mouse.Capture(null);
        SnapPreview.Visibility = Visibility.Collapsed;
        MergeHint.Visibility = Visibility.Collapsed;

        bool moved = _dragMoved;
        bool handled = false;

        if (moved && _mergeTarget != null && _dragOrigins != null)
        {
            var dragged = _dragOrigins.Keys.ToList();
            var target = _mergeTarget;

            foreach (var kv in _dragOrigins)
            {
                kv.Key.X = kv.Value.X;
                kv.Key.Y = kv.Value.Y;
            }

            if (target.IsFolder)
            {
                var added = new List<DesktopItem>();
                foreach (var d in dragged)
                {
                    if (d == target) continue;
                    if (d.IsSystemIcon) continue;
                    _viewModel.Items.Remove(d);
                    target.Children.Add(d);
                    added.Add(d);
                    handled = true;
                }
                if (added.Count > 0)
                    _viewModel.AppendChildrenToFolder(target, added);
            }
            else
            {
                dragged.Add(target);
                handled = _viewModel.CreateFolderFrom(dragged) != null;
            }
        }

        _dragOrigins = null;
        _dragMoved = false;
        _mergeTarget = null;
        _dragSourceFolder = null;

        if (handled) { _viewModel.Save(); return; }

        if (moved)
        {
            var sel = _viewModel.Items.Where(i => i.IsSelected).ToList();
            if (sel.Count > 0)
                _viewModel.SnapSelectedToGrid(sel);
            _viewModel.Save();
        }
    }

    private void Item_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DesktopItem item) return;
        if (!item.IsSelected) { ClearSelection(); item.IsSelected = true; }
        _contextItem = item;

        // ★ 系统图标（无真实路径）或虚拟文件夹（::folder:）→ 走 WPF 菜单
        //   路径无法被 Shell 解析，不能走原生菜单
        if ((item.IsSystem && string.IsNullOrEmpty(item.FullPath)) || IsVirtualFolder(item))
        {
            fe.ContextMenu = BuildItemContextMenu(item);
            return;
        }

        ShowNativeContextMenu(item);
        e.Handled = true;
    }

    private void ShowNativeContextMenu(DesktopItem item)
    {
        GetCursorPos(out var pt);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        var custom = new List<(string, Action)>();

        if (item.IsFolder)
        {
            custom.Add(("重命名文件夹", () => RenameFolder(item)));
            custom.Add(("文件夹样式：组模式", () =>
                _viewModel.SetFolderMode(item, FolderMode.Group)));
            custom.Add(("文件夹样式：全屏模式", () =>
                _viewModel.SetFolderMode(item, FolderMode.Fullscreen)));
            custom.Add(("文件夹样式：跟随默认", () =>
                _viewModel.SetFolderMode(item, null)));
            custom.Add(("解压到桌面", () =>
            {
                _viewModel.UnpackFolder(item);
                _viewModel.Save();
            }
            ));
        }
        else
        {
            custom.Add(("重命名", () => RenameItem(item)));
            custom.Add(("将选中项合并为新文件夹", () =>
            {
                var sel = _viewModel.Items.Where(i => i.IsSelected && !i.IsSystemIcon).ToList();
                if (sel.Count < 2) return;
                if (_viewModel.CreateFolderFrom(sel) != null) _viewModel.Save();
            }
            ));
        }

        _suppressHide++;
        try
        {
            ShellContextMenu.ShowNative(hwnd, new[] { item.FullPath }, custom, pt.X, pt.Y);
        }
        finally { _suppressHide--; }

        _viewModel.Save();
    }

    // ================ 组内子项交互 ================
    private void SubItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DesktopItem item) return;
        var parent = FindParentFolder(item);
        if (parent == null) return;

        e.Handled = true;

        if (e.ClickCount == 2) { OpenItem(item); return; }

        foreach (var c in parent.Children) c.IsSelected = false;
        item.IsSelected = true;

        _isDragging = true;
        _dragMoved = false;
        _dragSourceFolder = parent;
        _dragStartScreen = PointToScreen(e.GetPosition(this));
        _dragOrigins = new Dictionary<DesktopItem, Point>
        {
            [item] = new Point(item.X, item.Y)
        };

        Mouse.Capture(fe, CaptureMode.Element);
    }

    private void SubItem_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isDragging || e.LeftButton != MouseButtonState.Pressed) return;
        if (_dragOrigins == null || _dragSourceFolder == null) return;

        var cur = PointToScreen(e.GetPosition(this));
        var scale = GetDipFromScreenScale();
        var dx = (cur.X - _dragStartScreen.X) * scale.M11;
        var dy = (cur.Y - _dragStartScreen.Y) * scale.M22;

        if (!_dragMoved && (Math.Abs(dx) > 8 || Math.Abs(dy) > 8)) _dragMoved = true;
        if (!_dragMoved) return;

        var mouseInRoot = e.GetPosition(RootGrid);
        double fx = CanvasOffset + _dragSourceFolder.X;
        double fy = CanvasOffset + _dragSourceFolder.Y;
        var groupRect = new Rect(fx, fy,
                                 _dragSourceFolder.GroupWidth,
                                 _dragSourceFolder.GroupHeight);

        if (groupRect.Contains(mouseInRoot))
        {
            SnapPreview.Visibility = Visibility.Collapsed;
            return;
        }

        double px = Math.Max(0, mouseInRoot.X - CanvasOffset - IconWidth / 2);
        double py = Math.Max(0, mouseInRoot.Y - CanvasOffset - IconHeight / 2);

        if (_viewModel.Config.SnapToGrid)
        {
            var snap = _viewModel.SnapPoint(px, py);
            px = snap.X; py = snap.Y;
        }
        SnapPreview.Width = IconWidth;
        SnapPreview.Height = IconHeight;
        Canvas.SetLeft(SnapPreview, CanvasOffset + px);
        Canvas.SetTop(SnapPreview, CanvasOffset + py);
        SnapPreview.Visibility = Visibility.Visible;
    }

    private void SubItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        if (Mouse.Captured != null) Mouse.Capture(null);
        SnapPreview.Visibility = Visibility.Collapsed;

        bool moved = _dragMoved;
        var sourceFolder = _dragSourceFolder;
        var item = _dragOrigins?.Keys.FirstOrDefault();

        _dragOrigins = null;
        _dragMoved = false;
        _dragSourceFolder = null;

        if (!moved || sourceFolder == null || item == null) return;

        var mouseInRoot = e.GetPosition(RootGrid);
        double fx = CanvasOffset + sourceFolder.X;
        double fy = CanvasOffset + sourceFolder.Y;
        if (new Rect(fx, fy, sourceFolder.GroupWidth, sourceFolder.GroupHeight)
                .Contains(mouseInRoot)) return;

        double px = Math.Max(0, mouseInRoot.X - CanvasOffset - IconWidth / 2);
        double py = Math.Max(0, mouseInRoot.Y - CanvasOffset - IconHeight / 2);

        if (_viewModel.Config.SnapToGrid)
        {
            var snap = _viewModel.SnapPoint(px, py);
            px = snap.X;
            py = snap.Y;
        }

        if (!sourceFolder.Children.Remove(item)) return;
        item.X = px;
        item.Y = py;
        _viewModel.Items.Add(item);
        _viewModel.Save();
    }

    private void SubItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not DesktopItem item) return;
        var parent = FindParentFolder(item);
        if (parent == null) return;

        e.Handled = true;
        foreach (var c in parent.Children) c.IsSelected = false;
        item.IsSelected = true;
        _contextItem = item;

        GetCursorPos(out var pt);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        var custom = new List<(string, Action)>
        {
            ("重命名", () => RenameItem(item)),
            ("从组中移除", () =>
            {
                if (!parent.Children.Remove(item)) return;
                var slot = _viewModel.FindFreeSlot();
                item.X = slot.X; item.Y = slot.Y;
                _viewModel.Items.Add(item);
                _viewModel.Save();
            }),
            ("删除", () => DeleteSubItem(item, parent)),
        };

        _suppressHide++;
        try
        {
            ShellContextMenu.ShowNative(hwnd, new[] { item.FullPath }, custom, pt.X, pt.Y);
        }
        finally { _suppressHide--; }

        _viewModel.Save();
    }

    private void DeleteSubItem(DesktopItem item, DesktopItem parent)
    {
        if (item.IsSystemIcon) return;

        if (MessageBox.Show($"确定删除 '{item.Name}' 吗？\n（将移入回收站）",
                "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        try
        {
            MoveToRecycleBin(item.FullPath);
            parent.Children.Remove(item);
            _viewModel.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ================ WPF 菜单（仅系统图标 / 虚拟文件夹用） ================
    private ContextMenu BuildItemContextMenu(DesktopItem item)
    {
        var cm = new ContextMenu();

        if (item.IsFolder)
        {
            if (!item.IsGroupMode)
                cm.Items.Add(MakeMenu("打开", (_, _) => OpenFolderWindow(item)));

            cm.Items.Add(MakeMenu("重命名", (_, _) => RenameFolder(item)));

            cm.Items.Add(new Separator());
            cm.Items.Add(BuildFolderStyleMenu(item));

            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenu("解压到桌面", (_, _) =>
            {
                _viewModel.UnpackFolder(item);
                _viewModel.Save();
            }));

            cm.Items.Add(new Separator());
            cm.Items.Add(MakeMenu("删除文件夹", (_, _) =>
            {
                if (MessageBox.Show($"删除文件夹 '{item.Name}'？图标会回到桌面。",
                        "确认", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    _viewModel.UnpackFolder(item);
                    _viewModel.Save();
                }
            }));
            return cm;
        }

        // 系统图标分支
        cm.Items.Add(MakeMenu("打开", (_, _) => OpenItem(item)));
        cm.Items.Add(new Separator());
        cm.Items.Add(MakeMenu("将选中项合并为新文件夹", (_, _) =>
        {
            var sel = _viewModel.Items.Where(i => i.IsSelected && !i.IsSystemIcon).ToList();
            if (sel.Count < 2)
            {
                MessageBox.Show("请至少选中两个图标。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_viewModel.CreateFolderFrom(sel) != null) _viewModel.Save();
        }));

        return cm;
    }

    private MenuItem BuildFolderStyleMenu(DesktopItem folder)
    {
        var root = new MenuItem { Header = "文件夹样式" };

        var current = folder.FolderModeOverrideValue;
        string defaultLabel = _viewModel.Config.FolderMode == FolderMode.Group
            ? "跟随默认（组模式）"
            : "跟随默认（全屏模式）";

        root.Items.Add(MakeRadioMenu(defaultLabel,
            isChecked: current == null,
            onClick: () => _viewModel.SetFolderMode(folder, null)));

        root.Items.Add(MakeRadioMenu("组模式",
            isChecked: current == FolderMode.Group,
            onClick: () => _viewModel.SetFolderMode(folder, FolderMode.Group)));

        root.Items.Add(MakeRadioMenu("全屏模式",
            isChecked: current == FolderMode.Fullscreen,
            onClick: () => _viewModel.SetFolderMode(folder, FolderMode.Fullscreen)));

        return root;
    }

    private static MenuItem MakeRadioMenu(string header, bool isChecked, Action onClick)
    {
        var mi = new MenuItem
        {
            Header = header,
            IsCheckable = true,
            IsChecked = isChecked,
        };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static MenuItem MakeMenu(string header, RoutedEventHandler handler)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += handler;
        return mi;
    }

    // ================ 图标操作 ================
    private static void OpenItem(DesktopItem item)
    {
        try
        {
            if (item.IsSystem)
                Process.Start("explorer.exe", item.FullPath);
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
            if (item.IsSystemIcon) return;
            if (item.IsDirectory) Process.Start("explorer.exe", $"\"{item.FullPath}\"");
            else Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
        }
        catch { }
    }

    private void RenameItem(DesktopItem item)
    {
        if (item.IsSystemIcon) return;
        var dlg = new RenameDialog(item.Name);
        ShowModal(dlg);
        if (dlg.DialogResult != true || string.IsNullOrWhiteSpace(dlg.NewName)) return;

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
            _viewModel.Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重命名失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RenameFolder(DesktopItem folder)
    {
        var dlg = new RenameDialog(folder.Name);
        ShowModal(dlg);
        if (dlg.DialogResult != true || string.IsNullOrWhiteSpace(dlg.NewName)) return;
        _viewModel.RenameFolder(folder, dlg.NewName.Trim());
        _viewModel.Save();
    }

    private void OpenFolderWindow(DesktopItem folder)
    {
        bool modal = _viewModel.Config.FolderWindowModal;
        var w = new FolderWindow(folder, _viewModel, modal);
        w.Owner = this;

        if (modal)
        {
            w.Topmost = true;
            ShowModal(w);
            _viewModel.Save();
        }
        else
        {
            w.Closed += (_, _) => _openFolderWindows.Remove(w);
            _openFolderWindows.Add(w);
            w.Show();
        }
    }

    private void ClearSelection()
    {
        foreach (var i in _viewModel.Items) i.IsSelected = false;
    }

    private List<DesktopItem> SelectedItems() => _viewModel.Items.Where(i => i.IsSelected).ToList();

    private void DeleteSelected()
    {
        var sel = SelectedItems().Where(i => !i.IsSystemIcon && !i.IsFolder).ToList();
        if (sel.Count == 0) return;

        var confirm = MessageBox.Show(
            $"确定要删除选中的 {sel.Count} 个项目吗？\n（将移入回收站）",
            "删除确认", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in sel)
        {
            try { MoveToRecycleBin(item.FullPath); }
            catch (Exception ex)
            {
                MessageBox.Show($"删除失败：{item.Name}\n{ex.Message}",
                    "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
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
        foreach (var item in _viewModel.Items)
        {
            var itemRect = new Rect(CanvasOffset + item.X, CanvasOffset + item.Y,
                                    item.LayoutWidth, item.LayoutHeight);
            if (rect.IntersectsWith(itemRect)) item.IsSelected = true;
            else if (!ctrl) item.IsSelected = false;
        }
    }

    // ================ 桌面菜单 ================
    private void Menu_Refresh_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.RefreshDesktopItems();
        _viewModel.Save();
    }

    private void Menu_AutoArrange_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.AutoArrange();
        _viewModel.Save();
    }

    private void Menu_NewFolder_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems().Where(i => !i.IsSystemIcon).ToList();
        if (sel.Count < 2)
        {
            MessageBox.Show("请先选中两个或更多图标（Ctrl+单击多选），再点此创建文件夹。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var f = _viewModel.CreateFolderFrom(sel);
        if (f != null) _viewModel.Save();
    }

    private void Menu_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = _viewModel.Config.MonitoredFolders.FirstOrDefault();
        if (!string.IsNullOrEmpty(folder))
            Process.Start("explorer.exe", $"\"{folder}\"");
    }

    private void Menu_Settings_Click(object sender, RoutedEventArgs e)
    {
        var svc = new ConfigService();
        var cfg = svc.LoadConfig();
        var dlg = new SettingsWindow(cfg);
        ShowModal(dlg);
    }

    private void Menu_Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定要退出登录吗？程序将重启", "退出登录",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var arg = $"{StartupCommand.Switch} {StartupCommand.Encode(StartupAction.Logout)}";
        AdminHelper.Restart(arg);
    }
}