using System.Windows;
using System.Windows.Input;

namespace AyDesktop.Views;

public partial class RenameDialog : Window
{
    public string NewName => NameBox.Text.Trim();

    public RenameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        NameBox.SelectAll();
        NameBox.Focus();

        CommandBindings.Add(new CommandBinding(ApplicationCommands.Save,
            (_, _) => { DialogResult = true; }));
        Loaded += (_, _) => { NameBox.SelectAll(); NameBox.Focus(); };
    }
}