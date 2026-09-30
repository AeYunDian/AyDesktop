using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AyDesktop.Models;
using AyDesktop.Services;
using Microsoft.Win32;

namespace AyDesktop.Views;

public partial class SettingsWindow : Window
{
    private readonly ConfigService _configService = new();
    public AppConfig Config { get; private set; }

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        Config = config;
        DataContext = this;

        LoadUiFromConfig();
    }

    // ===== UI ↔ Config 同步 =====

    private void LoadUiFromConfig()
    {
        ExtCombo.SelectedIndex = (int)Config.ShowFileExtensions;
        HiddenCombo.SelectedIndex = (int)Config.ShowHiddenFiles;
        FolderModeCombo.SelectedIndex = (int)Config.FolderMode;
        SystemIconsCombo.SelectedIndex = (int)Config.ShowSystemIcons;
        Config.RunAtStartup = StartupHelper.IsEnabled();
    }

    private void SaveUiToConfig()
    {
        Config.ShowFileExtensions = (TriState)Math.Max(0, ExtCombo.SelectedIndex);
        Config.ShowHiddenFiles = (TriState)Math.Max(0, HiddenCombo.SelectedIndex);
        Config.ShowSystemIcons = (TriState)Math.Max(0, SystemIconsCombo.SelectedIndex);
        Config.FolderMode = FolderModeCombo.SelectedIndex == 1 ? FolderMode.Group : FolderMode.Fullscreen;
    }

    // ===== 文件夹 =====

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog() == true)
            AddFolderIfNotExists(dialog.FolderName);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string folder)
            Config.MonitoredFolders.Remove(folder);
        FolderList.Items.Refresh();
    }

    private void QuickAddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;

        var path = ResolveShortcutPath(key);
        if (string.IsNullOrEmpty(path))
        {
            MessageBox.Show($"无法定位该文件夹。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        AddFolderIfNotExists(path);
    }

    private static string? ResolveShortcutPath(string key) => key switch
    {
        "Desktop" => Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "PublicDesktop" => Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        "Documents" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Downloads" => GetDownloadsPath(),
        "Pictures" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "Music" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "Videos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        _ => null
    };

    private static string? GetDownloadsPath()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders");
            var v = k?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}")?.ToString();
            if (!string.IsNullOrEmpty(v) && Directory.Exists(v)) return v;
        }
        catch { }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? null : Path.Combine(home, "Downloads");
    }

    private void AddFolderIfNotExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        if (!Directory.Exists(path))
        {
            MessageBox.Show($"文件夹不存在：\n{path}", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var existing = Config.MonitoredFolders.FirstOrDefault(f =>
            string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            FolderList.SelectedItem = existing;
            FolderList.ScrollIntoView(existing);
            return;
        }

        Config.MonitoredFolders.Add(path);
        FolderList.Items.Refresh();
        FolderList.SelectedItem = path;
        FolderList.ScrollIntoView(path);
    }

    // ===== 保存 / 取消 =====

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveUiToConfig();
        StartupHelper.SetEnabled(Config.RunAtStartup);
        _configService.SaveConfig(Config);
        App.NotifyConfigChanged();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    // ===== 危险操作 =====

    private static bool ConfirmRepeatedly(string title, string message, int times)
    {
        for (int i = 0; i < times; i++)
        {
            var r = MessageBox.Show(
                message, title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (r != MessageBoxResult.Yes) return false;
        }
        return true;
    }

    private void DeleteDesktopConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmRepeatedly(
                "删除桌面配置",
                "确定要删除桌面配置吗？\n\n" +
                "所有图标的位置、自动分组信息都会被清除，且无法撤销。",
                2))
            return;

        DispatchAdminCommand(StartupAction.DeleteDesktopConfig);
    }

    private void DeleteAppConfig_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmRepeatedly(
                "删除应用配置",
                "确定要删除应用配置吗？\n\n" +
                "所有外观、监控文件夹、右键菜单等设置都会被重置，且无法撤销。",
                2))
            return;

        DispatchAdminCommand(StartupAction.DeleteAppConfig);
    }

    private void ResetAll_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmRepeatedly(
                "重置全部",
                "确定要重置全部吗？\n\n" +
                "桌面配置、应用配置和当前登录状态都会被清除，且无法撤销。",
                3))
            return;

        DispatchAdminCommand(StartupAction.ResetAll);
    }

    private static void DispatchAdminCommand(StartupAction action)
    {
        var arg = $"{StartupCommand.Switch} {StartupCommand.Encode(action)}";
        AdminHelper.RestartAsAdministrator(arg);
    }
}