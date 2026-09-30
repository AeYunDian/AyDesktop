using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;

namespace AyDesktop.Services;

public static class AdminHelper
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>以管理员身份重启本程序（会弹 UAC）。可选参数透传给新实例。</summary>
    public static void RestartAsAdministrator(string? arguments = null)
    {
        StartProcess(arguments, "runas");
    }

    /// <summary>以当前权限重启本程序（不弹 UAC）。</summary>
    public static void Restart(string? arguments = null)
    {
        StartProcess(arguments, verb: null);
    }

    private static void StartProcess(string? arguments, string? verb)
    {
        var exe = Environment.ProcessPath
                  ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory,
            };
            if (!string.IsNullOrEmpty(verb)) psi.Verb = verb;
            if (!string.IsNullOrEmpty(arguments)) psi.Arguments = arguments;

            Process.Start(psi);
            Application.Current?.Shutdown();
        }
        catch (Win32Exception)
        {
            // 用户取消 UAC 或其他启动失败：静默忽略
        }
    }
}