namespace ScreenTime.Core;

/// <summary>
/// 空闲检测。四路信号合并判断，任一满足即视为"人在电脑前"：
///
///   1. **键鼠**：GetLastInputInfo（整机最后输入时间）。
///      注意 dwTime 是 32 位 tick，约 49.7 天回绕一次，必须用无符号差值计算。
///
///   2. **手柄**：XInput 轮询。只统计键鼠会误判——用手柄玩游戏时长时间不碰
///      键鼠，明明在玩却会被记成空闲。窗口化玩游戏时尤其明显。
///
///   3. **前台在放媒体**：见 <see cref="MediaWatcher"/>。看视频时既不动键鼠
///      也（可能）不用手柄，但人明明在看。
///      **这一路是"看视频"这个场景的唯一负责人**，任何窗口模式下都有效
///      （窗口化 / 最大化 / 全屏都能查出播放状态）。
///
///   4. **真全屏**（可选，<see cref="TreatFullscreenAsActive"/>）：F11 真全屏
///      或独占全屏游戏。**只认覆盖整个显示器的那种，最大化窗口不算**——
///      最大化只是窗口摆法，不代表用户在做什么。
///
/// 判据：
/// <code>
///   空闲 = 键鼠空闲超阈值 且 手柄空闲超阈值 且 前台没在放媒体
///          （且，如果启用了真全屏判据，前台窗口也不是真全屏）
/// </code>
///
/// ### 第 3 路与第 4 路的分工（曾经重叠过，是个坑）
///
/// 早期版本让第 4 路兜住"窗口化看视频"：把"覆盖工作区（最大化）"也算作活跃。
/// 结果给任何最大化应用开了永久豁免——只要窗口最大化，空闲阈值就永远不生效，
/// 用户实测到 Edge 最大化挂着不动却一直记为"使用中"。
///
/// 第 3 路（v1.6 加入）本来就能更准地覆盖那个场景，第 4 路不该再插手。
/// 现在两者的边界是清楚的：**媒体播放归媒体检测，沉浸式使用归窗口几何。**
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

    /// <summary>
    /// 是否正在使用（四路输入任一活跃）。
    ///
    /// 判定顺序不敏感，但**媒体检测必须在全屏判据之前**理解：
    /// 看视频这件事由媒体检测负责，它在任何窗口模式下都有效
    /// （窗口化、最大化、全屏都能查出来）。全屏判据只负责"沉浸式使用"
    /// 这一种，两者不再重叠。
    ///
    /// 旧实现里全屏判据把"最大化窗口"也算作活跃，等于给任何最大化应用
    /// 开了一个永久豁免——只要窗口是最大化的，空闲阈值就永远不生效。
    /// 那是多余的：它想解决的是"最大化看视频被误判为空闲"，而那个场景
    /// 媒体检测（v1.6）已经覆盖得更准。详见 <see cref="IsFullscreenAppRunning"/>。
    /// </summary>
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
    /// **实现方式换过三次，前两次的教训都值得记下来。**
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
    /// 结果就是"窗口化全屏看视频会被记成空闲"。
    ///
    /// 第三版（当前）把 rcWork 那一半**去掉了**，只认 rcMonitor。
    /// 原因是第二版"覆盖工作区也算"引入了一个更糟的问题：
    ///
    ///   **最大化窗口和"人是否在用"毫无关系**——它只是个窗口摆法。
    ///   判据一旦包含它，等于给任何最大化的应用开了永久豁免：
    ///   只要窗口是最大化的，空闲阈值就永远不生效。
    ///   实测就是用户报的那个现象——Edge 最大化挂着不动，
    ///   既没放视频也没碰键鼠，却一直记为"使用中"。
    ///
    ///   而第二版想解决的"最大化看视频被误判为空闲"，**v1.6 的媒体检测
    ///   已经覆盖得更准了**：查的是系统媒体会话的真实播放状态，
    ///   窗口化、最大化、全屏都有效。所以这一条本来就不该由几何来管。
    ///
    /// 现在的分工：
    ///   · 媒体播放（任何窗口模式）  → 媒体检测负责
    ///   · 沉浸式使用（F11 真全屏） → 本函数负责
    ///   两者不再重叠，也就不会有"哪个说了算"的冲突。
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

            return WindowIsTrueFullscreen(wr, mi.rcMonitor);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断一个窗口矩形是否构成"真全屏"（覆盖整个显示器，含任务栏区域）。
    ///
    /// **抽成纯函数是为了能自检。** 这个判据改错过两次，而两次的错误都只在
    /// 真实窗口上才看得出来——靠肉眼看界面很难确认。做成接收矩形的静态方法之后，
    /// `--idlecheck` 可以直接喂进"最大化窗口""真全屏窗口""普通窗口"三组
    /// 合成数据，把判据钉死。
    ///
    /// 注意**只看 <paramref name="monitor"/>（rcMonitor，显示器完整矩形）**，
    /// 不看 rcWork（工作区）。理由见 <see cref="IsFullscreenAppRunning"/> 注释。
    /// </summary>
    internal static bool WindowIsTrueFullscreen(NativeMethods.RECT window, NativeMethods.RECT monitor)
    {
        const int tol = 2;   // 2 像素容差：边框对齐不总是精确到像素

        return window.Left <= monitor.Left + tol &&
               window.Top <= monitor.Top + tol &&
               window.Right >= monitor.Right - tol &&
               window.Bottom >= monitor.Bottom - tol;
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
            bool coversMonitor = WindowIsTrueFullscreen(wr, mi.rcMonitor);

            lines.Add($"覆盖工作区     : {(coversWork ? "True（最大化，不算活跃）" : "False")}");
            lines.Add($"覆盖整个显示器 : {(coversMonitor ? "True（真全屏，算活跃）" : "False")}");
            lines.Add($"→ 判定占据屏幕 : {(coversMonitor ? "是" : "否")}");

            lines.Add("");
            lines.Add("── 判据自检（合成数据，不依赖当前窗口）──");
            lines.AddRange(SelfTestFullscreenRule());
        }
        catch (Exception ex)
        {
            lines.Add("诊断失败：" + ex.Message);
        }
        return lines;
    }

    /// <summary>
    /// 真全屏判据的自检。用合成矩形跑一遍，不依赖当前桌面状态。
    ///
    /// 这个判据**改错过两次，而且两次的错误都只在真实窗口上才看得出来**——
    /// 靠肉眼看界面很难确认"最大化窗口到底算不算活跃"。
    /// 所以把判断抽成纯函数，再用合成数据把行为钉死：
    ///
    ///   · 最大化窗口（覆盖工作区、不覆盖任务栏）→ **必须为 false**
    ///   · F11 真全屏（覆盖整个显示器）           → 必须为 true
    ///   · 普通窗口                              → 必须为 false
    ///
    /// 第一组就是回归点：它曾经返回 true，导致任何最大化应用都拿到永久豁免、
    /// 空闲阈值形同虚设。
    /// </summary>
    private static System.Collections.Generic.List<string> SelfTestFullscreenRule()
    {
        var lines = new System.Collections.Generic.List<string>();

        // 按用户机器的实际数字构造：显示器 2560x1440，任务栏占 60px
        var monitor = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };
        var workArea = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1380 };

        // 最大化：有 9px 的隐形边框外扩，正好覆盖工作区
        var maximized = new NativeMethods.RECT { Left = -9, Top = -9, Right = 2569, Bottom = 1389 };
        var trueFullscreen = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2560, Bottom = 1440 };
        var normal = new NativeMethods.RECT { Left = 200, Top = 150, Right = 1400, Bottom = 900 };

        void Check(NativeMethods.RECT r, bool expect, string what)
        {
            bool got = WindowIsTrueFullscreen(r, monitor);
            lines.Add($"  {(got == expect ? "✓" : "✗")} {what}：期望 {(expect ? "算活跃" : "不算活跃")}，实际 {(got ? "算活跃" : "不算活跃")}");
        }

        lines.Add($"  （参照：工作区 {workArea.Right - workArea.Left}x{workArea.Bottom - workArea.Top}，"
                  + $"显示器 {monitor.Right - monitor.Left}x{monitor.Bottom - monitor.Top}）");
        Check(maximized, false, "最大化窗口");
        Check(trueFullscreen, true, "F11 真全屏");
        Check(normal, false, "普通窗口");

        // 边界：差 3 像素就不算（容差是 2）
        var almostFull = new NativeMethods.RECT { Left = 0, Top = 0, Right = 2557, Bottom = 1440 };
        Check(almostFull, false, "差 3 像素贴边（超出容差）");

        // 多显示器：窗口在副屏上时，要比对**副屏自己**的矩形。
        // 这里一开始写错了——拿主屏矩形去比副屏窗口，自然不匹配。
        // 生产代码里 MonitorFromWindow 会给出窗口所在的那块屏，所以逻辑本身没问题，
        // 是测试的期望值搭错了对象。改过来之后这条才真正在验"多屏也能认出全屏"。
        var secondMonitorRect = new NativeMethods.RECT { Left = 2560, Top = 0, Right = 5120, Bottom = 1440 };
        bool secondOk = WindowIsTrueFullscreen(secondMonitorRect, secondMonitorRect);
        lines.Add($"  {(secondOk ? "✓" : "✗")} 副屏上的真全屏：期望 算活跃，实际 {(secondOk ? "算活跃" : "不算活跃")}");

        // 而"副屏的全屏窗口"用主屏矩形去比 → 不该算（证明判据确实按传入的屏走）
        bool crossOk = !WindowIsTrueFullscreen(secondMonitorRect, monitor);
        lines.Add($"  {(crossOk ? "✓" : "✗")} 副屏窗口对主屏矩形：期望 不算活跃，实际 {(crossOk ? "不算活跃" : "算活跃")}");

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
    /// 诊断用：把当前各路输入的状态逐条列出来，
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

        lines.Add($"真全屏判据     : {(TreatFullscreenAsActive ? "启用" : "关闭")}"
                  + (TreatFullscreenAsActive
                     ? $"  → {(IsFullscreenAppRunning() ? "前台真全屏（算活跃）" : "前台非真全屏（最大化不算）")}"
                     : "  （只认 F11 真全屏，最大化窗口不算活跃）"));

        return lines;
    }
}
