namespace ScreenTime.Core;

/// <summary>
/// 键鼠空闲检测。基于 GetLastInputInfo，统计的是整机（所有会话输入设备）的最后输入时间。
/// 注意 dwTime 是 32 位 tick，约 49.7 天回绕一次，因此必须用无符号差值计算。
///
/// **全屏应用不判空闲**：看视频、看网课、玩游戏时长时间不碰键鼠，
/// 但人明明在电脑前。只按输入空闲判定会把这些时间误记成"空闲"，
/// 于是"真正在使用"的统计严重偏低。
/// 这里用 Windows 官方的 SHQueryUserNotificationState 判断是否存在
/// 全屏/演示状态的前台窗口，是则视为活跃。
/// </summary>
public sealed class IdleWatcher
{
    /// <summary>超过该秒数无输入即判为空闲。</summary>
    public int IdleThresholdSeconds { get; set; } = 60;

    /// <summary>是否把全屏应用视为活跃（不判空闲）。</summary>
    public bool TreatFullscreenAsActive { get; set; } = true;

    private NativeMethods.LASTINPUTINFO _lii;

    public IdleWatcher()
    {
        _lii = new NativeMethods.LASTINPUTINFO
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LASTINPUTINFO>(),
        };
    }

    /// <summary>距最后一次键鼠输入的秒数。取不到时返回 0（宁可保守判为活跃）。</summary>
    public uint IdleSeconds()
    {
        if (!NativeMethods.GetLastInputInfo(ref _lii)) return 0;
        uint now = NativeMethods.GetTickCount();
        // 无符号差值天然处理 32 位回绕
        return (now - _lii.dwTime) / 1000u;
    }

    /// <summary>
    /// 当前是否有全屏应用占据屏幕（视频播放器、游戏、全屏浏览器等）。
    ///
    /// **实现方式换过一次，原因值得记下来。**
    ///
    /// 最早用 Windows 官方的 SHQueryUserNotificationState()，它在采集循环里
    /// 每秒被调用一次，运行约 90 秒后抛 AccessViolationException——
    /// 事件日志里是 `Application Error 0xc0000005`，栈顶正是那个函数。
    /// 这类异常在 .NET 里**无法被 catch 捕获**，进程直接死亡，
    /// 用户看到的现象就是"挂到托盘没多久就自己关了"。
    ///
    /// 现在改用**窗口几何判定**，只依赖 GetForegroundWindow / GetWindowRect /
    /// MonitorFromWindow / GetMonitorInfoW 这几个从 Windows 2000 就存在、
    /// 极其稳定的 API：
    ///   前台窗口矩形 覆盖 它所在显示器的完整矩形 → 认为处于全屏
    ///
    /// 用 2 像素容差，因为部分全屏窗口的矩形会与显示器差一两个像素。
    /// 判据是"覆盖整个显示器"而非"覆盖工作区"，
    /// 这样最大化窗口（不覆盖任务栏）不会被误判成全屏。
    /// </summary>
    public bool IsFullscreenAppRunning()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT wr)) return false;

            IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return false;

            // cbSize 必须先填对：这个 API 靠它判断结构体版本，
            // 填错会写坏内存——"结构体没初始化就传给 Win32"的经典陷阱。
            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfoW(mon, ref mi)) return false;

            const int tol = 2;
            return wr.Left <= mi.rcMonitor.Left + tol &&
                   wr.Top <= mi.rcMonitor.Top + tol &&
                   wr.Right >= mi.rcMonitor.Right - tol &&
                   wr.Bottom >= mi.rcMonitor.Bottom - tol;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 是否空闲。
    /// 无输入超过阈值**且**没有全屏应用时才成立——
    /// 全屏看视频不算离开电脑。
    /// </summary>
    public bool IsIdle()
    {
        if (IdleSeconds() < (uint)Math.Max(1, IdleThresholdSeconds)) return false;
        if (TreatFullscreenAsActive && IsFullscreenAppRunning()) return false;
        return true;
    }
}
