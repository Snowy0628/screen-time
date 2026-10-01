// ---------------------------------------------------------------------------
// 原生 Win32 API 声明与常量
// 采集层全部依赖集中在这里，便于审查与将来替换
// ---------------------------------------------------------------------------
using System.Runtime.InteropServices;

namespace ScreenTime.Core;

internal static class NativeMethods
{
    // ---- 窗口 / 进程 ----

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetWindowTextW(IntPtr hWnd, System.Text.StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern int GetClassNameW(IntPtr hWnd, System.Text.StringBuilder className, int maxCount);

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetParent(IntPtr hWnd);

    // ---- 进程查询 ----

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageNameW(
        IntPtr process, uint flags, System.Text.StringBuilder exeName, ref uint size);

    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    // ---- 空闲检测 ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    internal static extern uint GetTickCount();

    /// <summary>
    /// 查询用户通知状态。用于判断"是否有全屏应用在运行"——
    /// 全屏看视频/玩游戏时人还在电脑前，不该判为空闲。
    /// 返回值：2=全屏应用 3=全屏 D3D 4=演示模式 5=正常 6=安静时间 等。
    /// 失败返回负的 HRESULT。
    /// </summary>
    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState();

    // ---- 电源 / 显示状态通知 ----

    [StructLayout(LayoutKind.Sequential)]
    internal struct POWERBROADCAST_SETTING
    {
        public Guid PowerSetting;
        public uint DataLength;
        public byte Data;
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    /// <summary>接收 WM_POWERBROADCAST 的窗口句柄（传 HWND 而非服务句柄）。</summary>
    internal const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0x00000000;

    internal const int WM_POWERBROADCAST = 0x0218;
    internal const int PBT_APMSUSPEND = 0x0004;
    internal const int PBT_APMRESUMESUSPEND = 0x0007;
    internal const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    internal const int PBT_POWERSETTINGCHANGE = 0x8013;

    // ---- 电源设置 GUID ----
    // 注意：这些 GUID 是 Windows SDK 中固定的公开常量（powrprof.h），
    // 与具体电源计划无关，因此可以安全硬编码。

    /// <summary>GUID_CONSOLE_DISPLAY_STATE：数据 0=关闭 1=开启 2=变暗。</summary>
    internal static readonly Guid GUID_CONSOLE_DISPLAY_STATE =
        new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    /// <summary>GUID_MONITOR_POWER_ON：数据 0=显示器关 1=显示器开。</summary>
    internal static readonly Guid GUID_MONITOR_POWER_ON =
        new("02731015-4510-4526-99E6-E5A17EBD1AEA");

    /// <summary>GUID_LIDSWITCH_STATE_CHANGE：数据 0=合盖 1=开盖（笔记本）。</summary>
    internal static readonly Guid GUID_LIDSWITCH_STATE_CHANGE =
        new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    /// <summary>GUID_SESSION_DISPLAY_STATUS：会话显示状态，数据 0=关 1=开。</summary>
    internal static readonly Guid GUID_SESSION_DISPLAY_STATUS =
        new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
}
