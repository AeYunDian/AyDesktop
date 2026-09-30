using System.Windows;
using System.Windows.Controls;
using AyDesktop.Models;

namespace AyDesktop.Views;

public class DesktopItemTemplateSelector : DataTemplateSelector
{
    public DataTemplate? NormalTemplate { get; set; }
    public DataTemplate? GroupFolderTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        if (item is DesktopItem di && di.IsFolder && di.IsGroupMode)
            return GroupFolderTemplate;
        return NormalTemplate;
    }
}