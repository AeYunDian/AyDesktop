using System.Windows.Input;

namespace AyDesktop.Views;

/// <summary>
/// 由 DesktopWindow / FolderWindow 实现，供 DesktopItemTemplates 转发鼠标事件。
/// </summary>
public interface IItemHost
{
    // 桌面级项
    void OnItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e);
    void OnItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e);
    void OnItemMouseMove(object sender, MouseEventArgs e);
    void OnItemMouseRightButtonDown(object sender, MouseButtonEventArgs e);

    // 组内子项
    void OnSubItemMouseLeftButtonDown(object sender, MouseButtonEventArgs e);
    void OnSubItemMouseLeftButtonUp(object sender, MouseButtonEventArgs e);
    void OnSubItemMouseMove(object sender, MouseEventArgs e);
    void OnSubItemMouseRightButtonDown(object sender, MouseButtonEventArgs e);
}