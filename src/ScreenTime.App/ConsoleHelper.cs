using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTime.App;

/// <summary>
/// WinExe 默认没有控制台窗口。自检模式需要输出，规则：
///   * stdout 已经被重定向（管道 / 文件）—— 直接用它，**不要** AttachConsole，
///     否则会把输出抢到父终端，重定向目标反而为空；
///   * stdout 无效（双击启动）—— 才尝试附加到父控制台，失败则新建一个。
/// </summary>
internal static class ConsoleHelper
{
    private const int ATTACH_PARENT_PROCESS = -1;
    private const int STD_OUTPUT_HANDLE = -11;
    private const int STD_ERROR_HANDLE = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    private static bool _ready;

    public static bool Ensure()
    {
        if (_ready) return true;
        try
        {
            bool stdoutRedirected = IsValid(GetStdHandle(STD_OUTPUT_HANDLE));
            bool stderrRedirected = IsValid(GetStdHandle(STD_ERROR_HANDLE));

            if (!stdoutRedirected && !stderrRedirected)
            {
                if (!AttachConsole(ATTACH_PARENT_PROCESS) && !AllocConsole())
                {
                    return false;
                }
            }

            // 重新绑定输出流，确保编码与自动刷新正确
            var stdout = new StreamWriter(Console.OpenStandardOutput(), System.Text.Encoding.UTF8)
            {
                AutoFlush = true,
            };
            Console.SetOut(stdout);
            if (IsValid(GetStdHandle(STD_ERROR_HANDLE)))
            {
                var stderr = new StreamWriter(Console.OpenStandardError(), System.Text.Encoding.UTF8)
                {
                    AutoFlush = true,
                };
                Console.SetError(stderr);
            }

            _ready = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValid(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);
}
