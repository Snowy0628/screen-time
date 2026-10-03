namespace ScreenTime.Core;

/// <summary>
/// 空闲检测。三路输入合并判断，任一满足即视为"人在电脑前"：
///
///   1. **键鼠**：GetLastInputInfo（整机最后输入时间）。
///      注意 dwTime 是 32 位 tick，约 49.7 天回绕一次，必须用无符号差值计算。
///
///   2. **手柄**：XInput 轮询。只统计键鼠会误判——用手柄玩游戏时长时间不碰
///      键鼠，明明在玩却会被记成空闲。窗口化玩游戏时尤其明显。
///
///   3. **前台在放媒体**：见 <see cref="MediaWatcher"/>。看视频时既不动键鼠
///      也（可能）不用手柄，但人明明在看。
///
/// 判据：
/// <code>
///   空闲 = 键鼠空闲超阈值 且 手柄空闲超阈值 且 前台没在放媒体
///          （且，如果启用了全屏判据，前台窗口也没占据整屏）
/// </code>
///
/// **关于"前台在放媒体"为什么限定前台**：后台挂个音乐播放器不该算"在使用"，
/// 那是用户明确要求的行为。限定前台之后还有个好处——不用区分"视频还是音频"：
/// 看视频时播放器必然在前台，挂后台听歌时前台是别的程序，靠位置就分开了，
/// 也就不需要维护播放器白名单（用户担心过白名单的兼容性）。
/// </summary>
public sealed class IdleWatcher
{
    /// <summary>超过该秒数无输入即判为空闲。</summary>
    public int IdleThresholdSeconds { get; set; } = 60;

    /// <summary>是否把"前台窗口占据整屏"视为活跃（不判空闲）。</summary>
    public bool TreatFullscreenAsActive { get; set; } = true;

    /// <summary>
    /// 媒体观察器。为 null 时该判据不生效（退化为只看键鼠/手柄/窗口）。
    /// 由外部注入，因为它的生命周期比 IdleWatcher 长。
    /// </summary>
    public MediaWatcher? Media { get; set; }

    /// <summary>
    /// 手柄空闲判定器。为 null 时该判据不生效。
    /// </summary>
    public GamepadWatcher? Gamepad { get; set; }

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

    /// <summary>是否正在使用（三路输入任一活跃）。</summary>
    public bool IsInUse()
    {
        if (IdleSeconds() < (uint)Math.Max(1, IdleThresholdSeconds)) return true;
        if (Gamepad is { } g && !g.IsIdleFor(IdleThresholdSeconds)) return true;
        if (Media is { } m && m.ForegroundIsPlaying) return true;
        if (TreatFullscreenAsActive && IsFullscreenAppRunning()) return true;
        return false;
    }

    /// <summary>
    /// 当前是否有应用占据屏幕（全屏或最大化），且不是桌面/任务栏本身。
    ///
    /// **实现方式换过两次，原因都值得记下来。**
    ///
    /// 第一版用 Windows 官方的 SHQueryUserNotificationState()，它在采集循环里
    /// 每秒被调用一次，运行约 90 秒后抛 AccessViolationException——
    /// 事件日志里是 `Application Error 0xc0000005`，栈顶正是那个函数。
    /// 这类异常在 .NET 里**无法被 catch 捕获**，进程直接死亡，
    /// 用户看到的现象就是"挂到托盘没多久就自己关了"。
    ///
    /// 第二版改用窗口几何判定，但只比较 **rcMonitor（显示器完整矩形）**，
    /// 于是"最大化窗口"和"无边框全屏"（窗口全屏，如 PotPlayer / 网页视频全屏）
    /// 都被判成"没全屏"。它们只覆盖 **rcWork（工作区，不含任务栏）**：
    ///
    ///   实测：最大化窗口  矩形 2048x1104
    ///         显示器完整矩形 2048x1152   ← 覆盖不到，旧判据 False
    ///         显示器工作区   2048x1104   ← 正好覆盖
    ///
    /// 结果就是"窗口化全屏看视频会被记成空闲"。现在两种都算：
    /// 覆盖工作区（最大化 / 无边框全屏）**或** 覆盖完整显示器（F11 真全屏）。
    ///
    /// 另外必须**排除桌面与任务栏**：Progman / WorkerW（桌面）和
    /// Shell_TrayWnd（任务栏）的矩形同样覆盖屏幕，但它们不代表"在用某个应用"，
    /// 不排除的话点一下桌面就会被算成活跃，空闲统计直接失效。
    /// </summary>
    public bool IsFullscreenAppRunning()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;

            // 桌面、任务栏不算"在用应用"
            if (IsShellWindow(hwnd)) return false;

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

            // 最大化 / 无边框全屏：覆盖工作区（不含任务栏）
            bool coversWork =
                wr.Left <= mi.rcWork.Left + tol &&
                wr.Top <= mi.rcWork.Top + tol &&
                wr.Right >= mi.rcWork.Right - tol &&
                wr.Bottom >= mi.rcWork.Bottom - tol;

            // F11 真全屏：覆盖整个显示器（含任务栏区域）
            bool coversMonitor =
                wr.Left <= mi.rcMonitor.Left + tol &&
                wr.Top <= mi.rcMonitor.Top + tol &&
                wr.Right >= mi.rcMonitor.Right - tol &&
                wr.Bottom >= mi.rcMonitor.Bottom - tol;

            return coversWork || coversMonitor;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 诊断用：描述当前前台窗口与空闲判定的依据。
    /// 供 `--idlecheck` 命令输出，便于用户自查"为什么这段被记成空闲"。
    /// </summary>
    public static System.Collections.Generic.List<string> DescribeForeground()
    {
        var lines = new System.Collections.Generic.List<string>();
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                lines.Add("前台窗口       : 无（可能锁屏或切换中）");
                return lines;
            }

            var cls = new System.Text.StringBuilder(128);
            NativeMethods.GetClassNameW(hwnd, cls, cls.Capacity);
            var title = new System.Text.StringBuilder(256);
            NativeMethods.GetWindowTextW(hwnd, title, title.Capacity);

            lines.Add($"前台窗口类名   : {cls}");
            lines.Add($"前台窗口标题   : {title}");

            bool shell = IsShellWindow(hwnd);
            lines.Add($"是否桌面/任务栏: {(shell ? "是（不算在使用应用）" : "否")}");

            if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT wr))
            {
                lines.Add("取窗口矩形     : 失败");
                return lines;
            }
            lines.Add($"窗口矩形       : L={wr.Left} T={wr.Top} R={wr.Right} B={wr.Bottom}"
                      + $"  ({wr.Right - wr.Left}x{wr.Bottom - wr.Top})");

            IntPtr mon = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfoW(mon, ref mi))
            {
                lines.Add("取显示器信息   : 失败");
                return lines;
            }

            lines.Add($"显示器完整矩形 : {mi.rcMonitor.Right - mi.rcMonitor.Left}x{mi.rcMonitor.Bottom - mi.rcMonitor.Top}"
                      + "（F11 真全屏的标准）");
            lines.Add($"显示器工作区   : {mi.rcWork.Right - mi.rcWork.Left}x{mi.rcWork.Bottom - mi.rcWork.Top}"
                      + "（最大化 / 无边框全屏的标准）");

            const int tol = 2;
            bool coversWork =
                wr.Left <= mi.rcWork.Left + tol && wr.Top <= mi.rcWork.Top + tol &&
                wr.Right >= mi.rcWork.Right - tol && wr.Bottom >= mi.rcWork.Bottom - tol;
            bool coversMonitor =
                wr.Left <= mi.rcMonitor.Left + tol && wr.Top <= mi.rcMonitor.Top + tol &&
                wr.Right >= mi.rcMonitor.Right - tol && wr.Bottom >= mi.rcMonitor.Bottom - tol;

            lines.Add($"覆盖工作区     : {(coversWork ? "True" : "False")}");
            lines.Add($"覆盖整个显示器 : {(coversMonitor ? "True" : "False")}");
            lines.Add($"→ 判定占据屏幕 : {(coversWork || coversMonitor ? "是" : "否")}");
        }
        catch (Exception ex)
        {
            lines.Add("诊断失败：" + ex.Message);
        }
        return lines;
    }

    /// <summary>
    /// 是否是桌面/任务栏这类"壳窗口"。
    /// 它们的矩形覆盖整个屏幕，但用户并没有在使用任何应用，必须排除。
    /// </summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        try
        {
            var sb = new System.Text.StringBuilder(64);
            int n = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
            if (n <= 0) return false;

            string cls = sb.ToString();
            return cls is "Progman"          // 桌面
                       or "WorkerW"          // 桌面（壁纸层，部分系统用它）
                       or "Shell_TrayWnd"    // 主任务栏
                       or "Shell_SecondaryTrayWnd";  // 副屏任务栏
        }
        catch
        {
            return false;
        }
    }

    /// <summary>是否空闲。等价于 <c>!IsInUse()</c>，保留此名以免调用方大改。</summary>
    public bool IsIdle() => !IsInUse();

    /// <summary>
    /// 诊断用：把当前三路输入的状态逐条列出来，
    /// 供 `--idlecheck` 回答"为什么这段被记成空闲/使用"。
    /// </summary>
    public System.Collections.Generic.List<string> DescribeInputs()
    {
        var lines = new System.Collections.Generic.List<string>();
        uint kb = IdleSeconds();
        int th = Math.Max(1, IdleThresholdSeconds);

        lines.Add($"空闲阈值       : {th} 秒");
        lines.Add($"键鼠距上次输入 : {kb} 秒  → {(kb < th ? "活跃" : "已空闲")}");

        if (Gamepad is { } g)
            lines.Add($"手柄距上次输入 : {g.IdleSeconds()} 秒  → {(g.IsIdleFor(th) ? "已空闲" : "活跃")}"
                      + (g.Connected ? "" : "（未连接手柄）"));
        else
            lines.Add("手柄检测       : 未启用");

        if (Media is { } m)
            lines.Add("媒体播放       : " + m.Describe());
        else
            lines.Add("媒体播放       : 未启用");

        lines.Add($"全屏判据       : {(TreatFullscreenAsActive ? "启用" : "关闭")}"
                  + (TreatFullscreenAsActive ? $"  → {(IsFullscreenAppRunning() ? "前台占据整屏（算活跃）" : "前台未占据整屏")}" : ""));

        return lines;
    }
}
