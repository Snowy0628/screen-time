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
    /// SHQueryUserNotificationState 的返回值：
    ///   QUNS_BUSY(2)        全屏应用运行中（F11 全屏、游戏等）
    ///   QUNS_RUNNING_D3D_FULL_SCREEN(3)  全屏 D3D 程序（多数游戏）
    ///   QUNS_PRESENTATION_MODE(4)        演示模式
    ///   QUNS_ACCEPTS_NOTIFICATIONS(5)    正常（未全屏）
    ///   QUNS_QUIET_TIME(6)               "安静时间"
    /// 取 2/3/4 视为全屏。
    /// </summary>
    public bool IsFullscreenAppRunning()
    {
        try
        {
            int state = NativeMethods.SHQueryUserNotificationState();
            return state is 2 or 3 or 4;
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
