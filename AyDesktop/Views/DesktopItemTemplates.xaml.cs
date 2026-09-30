using System.Windows;
using System.Windows.Input;

namespace AyDesktop.Views;

/// <summary>
/// 共享图标模板的 code-behind。
/// DataTemplate 内的事件处理器会绑定到这里，再通过 IItemHost 转发给实际承载的窗口。
/// </summary>
public partial class DesktopItemTemplates : ResourceDictionary
{
    public DesktopItemTemplates()
    {
        InitializeComponent();
    }

    private static IItemHost? GetHost(object sender)
    {
        if (sender is not DependencyObject d) return null;
        return Window.GetWindow(d) as IItemHost;
    }

    // ---- 桌面级项 ----
    private void Item_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => GetHost(sender)?.OnItemMouseLeftButtonDown(sender, e);

    private void Item_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => GetHost(sender)?.OnItemMouseLeftButtonUp(sender, e);

    private void Item_MouseMove(object sender, MouseEventArgs e)
        => GetHost(sender)?.OnItemMouseMove(sender, e);

    private void Item_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => GetHost(sender)?.OnItemMouseRightButtonDown(sender, e);

    // ---- 组内子项 ----
    private void SubItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => GetHost(sender)?.OnSubItemMouseLeftButtonDown(sender, e);

    private void SubItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => GetHost(sender)?.OnSubItemMouseLeftButtonUp(sender, e);

    private void SubItem_MouseMove(object sender, MouseEventArgs e)
        => GetHost(sender)?.OnSubItemMouseMove(sender, e);

    private void SubItem_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        => GetHost(sender)?.OnSubItemMouseRightButtonDown(sender, e);
}