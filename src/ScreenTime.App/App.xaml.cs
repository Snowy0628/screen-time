using System.Windows;

// WPF 与 WinForms 都有 Application，固定用 WPF 的
using Application = System.Windows.Application;

namespace ScreenTime.App;

/// <summary>
/// WPF 应用入口。
///
/// 原本宿主是 WinForms 的 ApplicationContext（因为 M0 只需要托盘）。
/// 现在有了真正的界面，改用 WPF 的 Application 承载消息循环，
/// 托盘与采集由 TrayContext 继续负责，两者在同一个 UI 线程上协作。
/// ShutdownMode 设为 OnExplicitShutdown：关闭窗口只是缩到托盘，程序继续记录。
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 关闭主窗口不退出程序（由托盘菜单或"退出"按钮显式退出）
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    /// <summary>未处理异常兜底：界面异常不能让采集悄悄中断。</summary>
    protected override void OnLoadCompleted(System.Windows.Navigation.NavigationEventArgs e)
    {
        base.OnLoadCompleted(e);
    }
}
