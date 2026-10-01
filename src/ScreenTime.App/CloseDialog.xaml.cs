using System.Windows;
using System.Windows.Controls;

namespace ScreenTime.App;

/// <summary>用户在关闭对话框里的选择。</summary>
internal enum CloseChoice
{
    Cancel,
    MinimizeToTray,
    Exit,
}

/// <summary>
/// 点窗口关闭按钮时的询问对话框。
///
/// 为什么必须有：只有托盘图标的应用如果直接关掉窗口就"消失"了，
/// 用户无法判断程序是退出了还是还在记录，也无法重新打开界面。
/// </summary>
internal sealed partial class CloseDialog : Window
{
    internal CloseChoice Choice { get; private set; } = CloseChoice.Cancel;

    /// <summary>用户是否勾选了"记住我的选择"。</summary>
    internal bool Remember => RememberCheck.IsChecked == true;

    internal CloseDialog(bool isRecording)
    {
        InitializeComponent();

        SubText.Text = isRecording
            ? "当前正在记录中"
            : "当前已暂停记录";
    }

    private void OnTray(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.MinimizeToTray;
        DialogResult = true;
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Exit;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        Choice = CloseChoice.Cancel;
        DialogResult = false;
    }
}
