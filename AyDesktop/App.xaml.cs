using System.Diagnostics;
using System.IO;
using System.Timers;
using System.Windows;
using AyDesktop.Services;
using AyDesktop.Views;

namespace AyDesktop;

public partial class App : Application
{
    public static AuthService Auth { get; } = new();

    public static event Action? ConfigChanged;
    public static void NotifyConfigChanged() => ConfigChanged?.Invoke();

    // ====================================================================
    // 登录状态巡检
    // ====================================================================

    private static readonly TimeSpan LoginCheckInterval = TimeSpan.FromMinutes(5);

    private System.Timers.Timer? _loginWatchdog;
    private int _loginCheckRunning;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (TryHandleStartupCommand(e.Args))
            return;
        try
        {
            AuthService.ValidateConfiguration();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "AyDesktop 启动错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        try
        {
            Directory.CreateDirectory(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database"));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"创建数据目录失败：{ex.Message}", "AyDesktop 启动错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        // ★ 上次是离线模式 → 直接进主程序，跳过登录
        if (!Auth.RestoreOfflineState())
        {
            bool loggedIn = false;
            try
            {
                if (Auth.TryRestoreSession())
                    loggedIn = await Auth.EnsureValidAsync();
            }
            catch { loggedIn = false; }

            if (!loggedIn)
            {
                var login = new LoginWindow(Auth);
                if (login.ShowDialog() != true)
                {
                    Shutdown();
                    return;
                }
                // 登录成功 或 用户选了离线 → 都继续
            }
        }

        var floatWindow = new FloatWindow(Auth);
        MainWindow = floatWindow;
        floatWindow.Show();

        StartLoginWatchdog();
    }

    // ====================================================================
    // 启动命令处理（保持不变）
    // ====================================================================

    private bool TryHandleStartupCommand(string[] args)
    {
        var raw = StartupCommand.ExtractArg(args);
        if (string.IsNullOrEmpty(raw)) return false;

        var action = StartupCommand.Decode(raw);
        if (action == StartupAction.None)
        {
            Shutdown();
            return true;
        }
        if (action == StartupAction.Logout)
        {
            try { Auth.Logout(); } catch { }   // Logout 内部会清 offline 标志
            AdminHelper.Restart();
            return true;
        }
        if (!AdminHelper.IsAdministrator())
        {
            AdminHelper.RestartAsAdministrator(
                $"{StartupCommand.Switch} {raw}");
            return true;
        }

        ExecuteStartupAction(action);

        AdminHelper.Restart();
        return true;
    }

    private void ExecuteStartupAction(StartupAction action)
    {
        var dbDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "database");

        switch (action)
        {
            case StartupAction.DeleteDesktopConfig:
                TryDeleteFile(Path.Combine(dbDir, "desktop.xml"));
                break;

            case StartupAction.DeleteAppConfig:
                TryDeleteFile(Path.Combine(dbDir, "config.xml"));
                break;

            case StartupAction.ResetAll:
                TryDeleteFile(Path.Combine(dbDir, "desktop.xml"));
                TryDeleteFile(Path.Combine(dbDir, "config.xml"));
                try { Auth.Logout(); } catch { }              // 也会清 offline
                try { StartupHelper.SetEnabled(false); } catch { }
                break;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
        catch { }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StopLoginWatchdog();
        try { Auth.Dispose(); } catch { }
        base.OnExit(e);
    }

    // ====================================================================
    // 巡检实现
    // ====================================================================

    private void StartLoginWatchdog()
    {
        if (_loginWatchdog != null) return;

        _loginWatchdog = new System.Timers.Timer(LoginCheckInterval.TotalMilliseconds)
        {
            AutoReset = true,
        };
        _loginWatchdog.Elapsed += OnLoginWatchdogElapsed;
        _loginWatchdog.Start();
    }

    private void StopLoginWatchdog()
    {
        var t = _loginWatchdog;
        _loginWatchdog = null;
        if (t == null) return;

        try
        {
            t.Stop();
            t.Elapsed -= OnLoginWatchdogElapsed;
            t.Dispose();
        }
        catch { }
    }

    private async void OnLoginWatchdogElapsed(object? sender, ElapsedEventArgs e)
    {
        if (Interlocked.Exchange(ref _loginCheckRunning, 1) == 1) return;

        try
        {
            // ★ 离线模式：跳过巡检
            if (Auth.IsOfflineMode) return;

            if (Auth.IsLoggedIn) return;

            bool refreshed = false;
            try
            {
                if (Auth.TryRestoreSession())
                    refreshed = await Auth.EnsureValidAsync();
            }
            catch
            {
                return;
            }

            if (refreshed) return;

            RestartApplication();
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _loginCheckRunning, 0);
        }
    }

    private void RestartApplication()
    {
        StopLoginWatchdog();

        try
        {
            var exe = Environment.ProcessPath
                      ?? Process.GetCurrentProcess().MainModule?.FileName;

            if (!string.IsNullOrEmpty(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                    WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
                });
            }
        }
        catch { }
        finally
        {
            var app = Application.Current;
            if (app != null)
            {
                app.Dispatcher.InvokeAsync(() =>
                {
                    try { app.Shutdown(); } catch { }
                });
            }
        }
    }
}