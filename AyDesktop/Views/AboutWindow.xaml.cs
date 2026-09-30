using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AyDesktop.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        BuildBody();
    }

    // ==================== 去掉最大化 / 最小化按钮 ====================

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_STYLE = -16;
    private const int WS_MAXIMIZEBOX = 0x00010000;
    private const int WS_MINIMIZEBOX = 0x00020000;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new IntPtr(new WindowInteropHelper(this).Handle.ToInt64());
        int style = GetWindowLong(hwnd, GWL_STYLE);
        style &= ~WS_MAXIMIZEBOX;
        style &= ~WS_MINIMIZEBOX;
        SetWindowLong(hwnd, GWL_STYLE, style);
    }

    // ==================== 正文 ====================

    private void BuildBody()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";

        BodyText.Text =
            "AeYunDian AyDesktop\n" +
            $"版本： {version}\n" +
            "© 2025-2026 韵典 (AeYunDian) 。保留所有权利。\n\n" +
            "AyDesktop 软件受中国的软件著作权和其他待颁布或已颁布的知识产权保护法保护。\n\n\n" +
            "根据 AyDesktop 开源条款 ， 许可如下用户使用本产品：\n\n" +
            Environment.UserName;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Close();
}