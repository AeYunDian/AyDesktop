using AyDesktop.Services;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace AyDesktop.Views;

public partial class LoginWindow : Window
{
    private readonly AuthService _auth;
    private bool _networkProbeStarted;   // ★ 防止重复探测

    public LoginWindow(AuthService auth)
    {
        InitializeComponent();
        _auth = auth;
        _auth.AuthorizationReceived += OnAuthorizationReceived;
    }

    protected override void OnClosed(EventArgs e)
    {
        _auth.AuthorizationReceived -= OnAuthorizationReceived;
        base.OnClosed(e);
    }

    // ====================================================================
    // ★ 网络探测：窗口渲染完成后异步检测，不阻塞 UI
    // ====================================================================
    protected override async void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        if (_networkProbeStarted) return;
        _networkProbeStarted = true;

        StatusText.Text = "正在检查网络连接...";

        bool reachable;
        try
        {
            reachable = await NetworkProbe.IsServerReachableAsync();
        }
        catch { reachable = false; }

        // 窗口可能已被关闭
        if (!IsLoaded) return;

        if (reachable)
        {
            StatusText.Text = "未登录";
        }
        else
        {
            StatusText.Text = "无法连接到 Ay 服务。";
            OfflineHint.Visibility = Visibility.Visible;
            LoginButton.Content = "重试登录";
            // 强制重新布局，确保从 Collapsed 恢复的元素立刻绘制
            OfflineHint.InvalidateVisual();
            OfflineHint.UpdateLayout();
        }
    }

    private void OfflineMode_Click(object sender, MouseButtonEventArgs e)
    {
        if (MessageBox.Show(
                "离线模式下将无法使用 Ay 账号相关的功能。\n" +
                "你可以随时在悬浮球右键菜单中重新登录。\n\n确定继续吗？",
                "离线模式",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _auth.EnterOfflineMode();
        DialogResult = true;
    }

    // ====================================================================
    // 授权完成：切文本 + 抢前台
    // ====================================================================
    private void OnAuthorizationReceived()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(OnAuthorizationReceived);
            return;
        }
        if (!IsLoaded) return;

        StatusText.Text = "已授权，请稍后...";
        LoginButton.IsEnabled = false;
        OfflineHint.Visibility = Visibility.Collapsed;   // ★ 登录中不再显示离线入口

        BringToFront();
    }

    private void BringToFront()
    {
        try
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Show();
            Activate();

            Topmost = true;
            Topmost = false;

            ForceForeground();
        }
        catch { }
    }

    // ====================================================================
    // Win32 抢前台
    // ====================================================================
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const int SW_RESTORE = 9;

    private void ForceForeground()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        IntPtr foreground = GetForegroundWindow();
        uint fgThread = foreground != IntPtr.Zero
            ? GetWindowThreadProcessId(foreground, IntPtr.Zero)
            : 0;
        uint myThread = GetCurrentThreadId();

        bool attached = false;
        if (fgThread != 0 && fgThread != myThread)
            attached = AttachThreadInput(fgThread, myThread, true);

        try
        {
            ShowWindow(hwnd, SW_RESTORE);
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(fgThread, myThread, false);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        StatusText.Text = "正在打开浏览器，请在浏览器中完成登录...";
        try
        {
            await _auth.LoginAsync();
            StatusText.Text = "登录成功";
            DialogResult = true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "登录失败：" + ex.Message;
            LoginButton.IsEnabled = true;
            OfflineHint.Visibility = Visibility.Visible;   // ★ 失败后允许离线
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}