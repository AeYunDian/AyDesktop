using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AyDesktop.Models;
using AyDesktop.Services;

namespace AyDesktop.Views;

public partial class FloatWindow : Window
{
    private readonly AuthService _auth;
    private bool _isDragging;
    private Point _startScreenPoint;
    private Point _startWindowPoint;
    private DesktopWindow? _desktopWindow;

    public FloatWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;

        var cfg = new ConfigService().LoadConfig();
        Topmost = cfg.FloatTopmost;
        Opacity = Math.Clamp(cfg.FloatWindowOpacity, 0.2, 1.0);

        if (AdminHelper.IsAdministrator())
            MenuRunAsAdmin.Visibility = Visibility.Collapsed;

        RestorePosition(cfg);
        RefreshAuthMenuItems();

        App.ConfigChanged += OnConfigChanged;
    }

    // ====================================================================
    // 登录/离线 菜单切换
    // ====================================================================
    private void RefreshAuthMenuItems()
    {
        if (_auth.IsLoggedIn)
        {
            MenuLogin.Visibility = Visibility.Collapsed;
            MenuLogout.Visibility = Visibility.Visible;
        }
        else
        {
            MenuLogin.Visibility = Visibility.Visible;
            MenuLogout.Visibility = Visibility.Collapsed;
        }
    }

    private void OnConfigChanged()
    {
        var cfg = new ConfigService().LoadConfig();
        Topmost = cfg.FloatTopmost;
        Opacity = Math.Clamp(cfg.FloatWindowOpacity, 0.2, 1.0);
    }

    protected override void OnClosed(EventArgs e)
    {
        App.ConfigChanged -= OnConfigChanged;
        base.OnClosed(e);
    }

    // ====================================================================
    // 位置持久化
    // ====================================================================
    private void RestorePosition(AppConfig cfg)
    {
        if (cfg.FloatX is double x && cfg.FloatY is double y && IsPositionVisible(x, y))
        {
            Left = x;
            Top = y;
        }
        else
        {
            PositionRight();
        }
    }

    private bool IsPositionVisible(double x, double y)
    {
        double vsLeft = SystemParameters.VirtualScreenLeft;
        double vsTop = SystemParameters.VirtualScreenTop;
        double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
        double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

        return x < vsRight && x + Width > vsLeft
            && y < vsBottom && y + Height > vsTop;
    }

    private void PositionRight()
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right - Width - 8;
        Top = workArea.Top + (workArea.Height - Height) / 2;
    }

    private void SavePosition()
    {
        try
        {
            var svc = new ConfigService();
            var cfg = svc.LoadConfig();
            cfg.FloatX = Left;
            cfg.FloatY = Top;
            svc.SaveConfig(cfg);
        }
        catch { }
    }

    // ====================================================================
    // 拖动 / 单击
    // ====================================================================
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isDragging = false;
        _startScreenPoint = PointToScreen(e.GetPosition(this));
        _startWindowPoint = new Point(Left, Top);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed && IsMouseCaptured)
        {
            var cur = PointToScreen(e.GetPosition(this));
            var src = PresentationSource.FromVisual(this);
            var scale = src?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var dx = (cur.X - _startScreenPoint.X) * scale.M11;
            var dy = (cur.Y - _startScreenPoint.Y) * scale.M22;

            if (!_isDragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4))
                _isDragging = true;

            if (_isDragging)
            {
                Left = _startWindowPoint.X + dx;
                Top = _startWindowPoint.Y + dy;
            }
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (IsMouseCaptured) ReleaseMouseCapture();

        if (!_isDragging)
        {
            ToggleDesktopWindow();
        }
        else
        {
            SavePosition();
        }

        _isDragging = false;
        base.OnMouseLeftButtonUp(e);
    }

    // ====================================================================
    // 桌面窗口切换
    // ====================================================================
    private void ToggleDesktopWindow()
    {
        if (_desktopWindow != null && _desktopWindow.IsVisible && !_desktopWindow.IsFadingOut)
        {
            _desktopWindow.HideWithAnimation();
        }
        else if (_desktopWindow == null || !_desktopWindow.SuppressNextShow)
        {
            ShowDesktopWindow();
        }
    }

    private void ShowDesktopWindow()
    {
        if (_desktopWindow == null)
        {
            _desktopWindow = new DesktopWindow(_auth);
            _desktopWindow.Closed += (_, _) => _desktopWindow = null;
        }

        _desktopWindow.ShowWithAnimation();
    }

    // ====================================================================
    // 菜单
    // ====================================================================
    private void Menu_OpenDesktop_Click(object sender, RoutedEventArgs e)
        => ShowDesktopWindow();

    private void Menu_Settings_Click(object sender, RoutedEventArgs e)
    {
        var svc = new ConfigService();
        var cfg = svc.LoadConfig();
        var dlg = new SettingsWindow(cfg) { Owner = this };
        dlg.ShowDialog();
        // ★ 不再需要 _desktopWindow?.ReloadFromConfig()：
        //   DesktopWindow 会自己订阅 ConfigChanged 并刷新
    }

    private void Menu_RunAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        AdminHelper.RestartAsAdministrator();
    }

    private void Menu_Login_Click(object sender, RoutedEventArgs e)
    {
        var login = new LoginWindow(_auth) { Owner = this };
        if (login.ShowDialog() == true && _auth.IsLoggedIn)
        {
            _auth.ExitOfflineMode();
            RefreshAuthMenuItems();
            ShowDesktopWindow();
        }
    }
    private void Menu_About_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutWindow { Owner = this };
        dlg.ShowDialog();
    }
    private void Menu_Logout_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "确定要退出登录吗？\n\n应用将重启并返回登录页。",
                "退出登录", MessageBoxButton.YesNo, MessageBoxImage.Question)
            != MessageBoxResult.Yes)
            return;

        var arg = $"{StartupCommand.Switch} {StartupCommand.Encode(StartupAction.Logout)}";
        AdminHelper.Restart(arg);
    }
    private void Menu_Restart_Click(object sender, RoutedEventArgs e)
    {
        AdminHelper.Restart();
    }
    private void Menu_Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}