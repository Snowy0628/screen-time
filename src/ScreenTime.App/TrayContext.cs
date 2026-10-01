using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using ScreenTime.Core;
using WpfWindow = System.Windows.Window;
using WpfWindowState = System.Windows.WindowState;
using System.Windows;

namespace ScreenTime.App;

/// <summary>
/// M0 宿主：托盘常驻 + 采集循环。
/// M0 阶段不做图表 UI，目标是验证"采集准不准、数据落没落、体积可控不可控"。
/// </summary>
internal sealed class TrayContext : IDisposable
{
    private readonly UsageStore _store;
    private readonly Recorder _recorder;
    private readonly Log _log;
    private readonly DispatcherTimer _timer;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly Action _requestShutdown;

    // 非 readonly：explorer 重启后需要整个重建托盘图标
    private NotifyIcon _tray;

    private DateTime _lastSummaryDay = DateTime.Today;
    private bool _paused;
    private uint _wmTaskbarCreated;
    private TaskbarWatcherWindow? _taskbarWatcher;

    /// <summary>本实例的启动时刻，用于判断接管请求是不是发给自己的。</summary>
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>
    /// 检查是否有另一个实例请求接管。有则收尾退出，让新实例启动。
    /// 返回 true 表示已发起退出，调用方应立即返回。
    /// </summary>
    private bool CheckTakeoverRequest()
    {
        try
        {
            string path = AppPaths.TakeoverRequestPath;
            if (!File.Exists(path)) return false;

            // 只响应"本实例启动之后"写入的请求，避免上次遗留的文件误触发
            DateTime written = File.GetLastWriteTimeUtc(path);
            if (written <= _startedUtc) return false;

            File.Delete(path);
            _log.Info("收到另一个实例的接管请求，本实例退出以便它启动");
            ExitApp();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- 供状态窗口读取的接口 ----

    /// <summary>是否已暂停记录。</summary>
    public bool IsPaused => _paused;

    /// <summary>是否正在退出（用于区分"关闭窗口"与"退出程序"）。</summary>
    public bool IsExiting { get; private set; }

    /// <summary>
    /// 托盘图标是否不可用（系统拒绝注册）。
    /// 为 true 时不应把窗口收进托盘——那会让用户彻底找不到界面。
    /// </summary>
    public bool TrayUnavailable { get; private set; }

    /// <summary>最近一次采样的状态。</summary>
    public Sample? CurrentSample => _recorder.CurrentSample;

    /// <summary>存储层，供界面直接查询历史数据。</summary>
    public UsageStore Store => _store;

    /// <summary>今日统计。窗口每次刷新时顺便把内存中的片段落库，读数才是最新的。</summary>
    public DayTotals TodayTotals()
    {
        try
        {
            _recorder.Flush();
            return _store.GetDayTotals(DateTime.Today);
        }
        catch
        {
            return new DayTotals(0, 0, 0, 0, 0);
        }
    }

    /// <summary>最近一次落库时间。</summary>
    public DateTimeOffset? LastHeartbeat()
    {
        try { return _store.LastHeartbeatUtc(); }
        catch { return null; }
    }

    /// <summary>
    /// 今日累计的前台使用秒数，含**尚未落库的内存片段**。
    ///
    /// 数据库每 60 秒才落一次，只读库会让界面在整分钟内显示 0 秒——
    /// 刚启动或刚重启时尤其明显（库里今天还没有任何行，连尾行都不存在）。
    /// 因此必须把 Recorder 内存里正在累积的片段一并算上。
    /// </summary>
    public long LiveActiveSeconds()
    {
        try
        {
            return LiveTotals().ActiveSeconds;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>今日各状态的实时秒数（库中数据 + 内存中未落库的片段）。</summary>
    public DayTotals LiveTotals()
    {
        try
        {
            DateTime today = DateTime.Today;
            (long dayStartUtc, long dayEndUtc, _) = UsageStore.LocalDayRange(today);

            DayTotals stored = _store.GetDayTotals(today);
            var extra = new Dictionary<UsageState, long>
            {
                [UsageState.Active] = 0,
                [UsageState.Idle] = 0,
                [UsageState.Locked] = 0,
                [UsageState.Off] = 0,
            };

            foreach (Span s in _recorder.PendingSpans())
            {
                long a = Math.Max(s.StartUtc, dayStartUtc);
                long b = Math.Min(s.EndUtc, dayEndUtc);
                if (b > a) extra[s.State] += b - a;
            }

            return new DayTotals(
                stored.ActiveSeconds + extra[UsageState.Active],
                stored.IdleSeconds + extra[UsageState.Idle],
                stored.LockedSeconds + extra[UsageState.Locked],
                stored.OffSeconds + extra[UsageState.Off],
                stored.DistinctApps);
        }
        catch
        {
            return new DayTotals(0, 0, 0, 0, 0);
        }
    }

    /// <summary>底部说明文字：数据位置、占用与心跳。</summary>
    public string FooterInfo()
    {
        try
        {
            (long rows, long bytes) = _store.StoreInfo();
            DateTimeOffset? hb = LastHeartbeat();
            string hbText = hb is null ? "等待首次落库" : hb.Value.ToLocalTime().ToString("HH:mm:ss");
            return $"数据文件：{_store.DatabasePath}　·　当前 {rows} 条片段 / {bytes / 1024.0:F0} KB　·　"
                 + $"最近落库 {hbText}　·　每 {_recorder.FlushIntervalSeconds} 秒落库一次";
        }
        catch
        {
            return "";
        }
    }

    public void TogglePauseFromUi() => TogglePause();

    public void OpenDataFolderFromUi() => OpenDataFolder();

    public void ExitFromUi() => ExitApp();

    /// <summary>
    /// 关闭窗口时的行为。默认"每次询问"。
    /// 用户可以在对话框里勾选"记住我的选择"，之后就不再打扰；
    /// 托盘菜单里提供"恢复关闭询问"以便改回来。
    /// </summary>
    public CloseAction WindowCloseAction
    {
        get => AppSettings.Current.CloseAction;
        set
        {
            AppSettings.Current.CloseAction = value;
            AppSettings.Current.Save();
            _log.Info($"关闭窗口的行为已设为：{DescribeCloseAction(value)}");
        }
    }

    /// <summary>是否正在记录（供对话框显示状态）。</summary>
    public bool IsRecording => !_paused;

    private ToolStripMenuItem? _closeActionItem;

    private ToolStripMenuItem MakeCloseActionItem(string text, CloseAction action)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) =>
        {
            WindowCloseAction = action;
            UpdateCloseActionMenu();
        };
        return item;
    }

    private void UpdateCloseActionMenu()
    {
        if (_closeActionItem is null) return;
        CloseAction cur = WindowCloseAction;
        _closeActionItem.Text = "关闭窗口时：" + DescribeCloseAction(cur);
        foreach (ToolStripItem it in _closeActionItem.DropDownItems)
        {
            if (it is ToolStripMenuItem mi) mi.Checked = mi.Text == DescribeCloseAction(cur);
        }
    }

    private static string DescribeCloseAction(CloseAction a) => a switch
    {
        CloseAction.MinimizeToTray => "最小化到托盘",
        CloseAction.Exit => "彻底退出",
        _ => "每次询问",
    };

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    // ---- 托盘注册自检 ----

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT32 { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER id, out RECT32 rect);

    /// <summary>
    /// 校验托盘图标是否真的注册进了通知区域。
    ///
    /// `NotifyIcon.Visible = true` 在注册被系统拒绝时不会抛异常，
    /// 所以必须自己调一次 Shell_NotifyIcon 拿到原始返回值才知道系统允不允许。
    /// </summary>
    private bool VerifyTrayRegistered()
    {
        // 先确认"能不能创建原生消息窗口"——Shell_NotifyIcon 需要一个窗口句柄
        NativeWindow probeWin;
        try
        {
            probeWin = new NativeWindow();
            probeWin.CreateHandle(new CreateParams { Caption = "ScreenTime.TrayProbe" });
            if (probeWin.Handle == IntPtr.Zero)
            {
                _log.Warn("原生消息窗口创建失败（句柄为空），托盘图标无法注册");
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"原生消息窗口创建抛出异常，托盘图标无法注册：{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        // ---- 关键一步：用本进程自己的窗口句柄做一次真实的 Shell_NotifyIcon 探测 ----
        // NotifyIcon.Visible = true 在注册被拒绝时不抛异常，拿不到原始返回值，
        // 所以只能自己调一次 API 才知道系统到底允不允许注册。
        bool shellAccepted;
        int lastError = 0;
        var probeData = new NOTIFYICONDATA
        {
            cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = probeWin.Handle,
            uID = 0x5C7E,                          // 探测专用 ID，与正式图标区分
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = 0x0400 + 0x5C,
            hIcon = SystemIcons.Application.Handle,
            szTip = "ScreenTime 托盘探测",
        };

        try
        {
            shellAccepted = Shell_NotifyIcon(NIM_ADD, ref probeData);
            if (!shellAccepted) lastError = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (shellAccepted) Shell_NotifyIcon(NIM_DELETE, ref probeData);   // 立刻撤掉探测图标
        }
        catch (Exception ex)
        {
            _log.Warn($"Shell_NotifyIcon 探测异常：{ex.GetType().Name}: {ex.Message}");
            shellAccepted = false;
        }
        finally
        {
            try { probeWin.DestroyHandle(); } catch { /* 忽略 */ }
        }

        if (!shellAccepted)
        {
            _log.Warn("托盘图标注册探测失败：Shell_NotifyIcon 返回 FALSE" +
                      (lastError != 0 ? $"，GetLastError={lastError}" : "") +
                      (lastError == 5 ? "（拒绝访问：本进程无权向通知区域注册图标）" : ""));
            return false;
        }

        _log.Info("托盘图标注册探测通过（Shell_NotifyIcon 返回成功）");

        // ---- 反查位置仅供参考；取不到内部句柄属正常，不影响结论 ----
        try
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            IntPtr hwnd = IntPtr.Zero;
            uint id = 1;

            object? inner = typeof(NotifyIcon).GetField("window", flags)?.GetValue(_tray);
            if (inner is not null)
            {
                hwnd = inner.GetType()
                    .GetProperty("Handle", System.Reflection.BindingFlags.Instance |
                                          System.Reflection.BindingFlags.Public |
                                          System.Reflection.BindingFlags.NonPublic)
                    ?.GetValue(inner) is IntPtr h ? h : IntPtr.Zero;

                if (inner.GetType().GetField("_id", flags)?.GetValue(inner) is int i0) id = (uint)i0;
                else if (inner.GetType().GetField("id", flags)?.GetValue(inner) is int i1) id = (uint)i1;
            }

            if (hwnd == IntPtr.Zero)
            {
                _log.Info("托盘自检：无法取得内部窗口句柄（属正常，跳过位置反查）");
                return true;
            }

            var ident = new NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
                hWnd = hwnd,
                uID = id,
            };

            int hr = Shell_NotifyIconGetRect(ref ident, out RECT32 _);
            if (hr == 1) _log.Info("托盘图标已注册（当前折叠进溢出区，属 Windows 11 正常行为）");
            else if (hr != 0) _log.Info($"托盘图标位置反查返回 0x{hr:X8}（不影响注册结论）");
            return true;
        }
        catch (Exception ex)
        {
            _log.Info($"托盘位置反查未完成（按正常处理）：{ex.GetType().Name}");
            return true;
        }
    }

    // ---- Shell_NotifyIcon 探测所需 ----

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;

    [System.Runtime.InteropServices.StructLayout(
        System.Runtime.InteropServices.LayoutKind.Sequential,
        CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [System.Runtime.InteropServices.MarshalAs(
            System.Runtime.InteropServices.UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    /// <summary>
    /// 托盘图标不可用时的兜底：标记状态，并把关闭行为退回"彻底退出"，
    /// 避免用户把窗口收进一个不存在的托盘后彻底失联。
    ///
    /// 不在这里弹窗/显示窗口：此时主窗口可能还没创建，
    /// 交由 Program 在窗口就绪后处理。
    /// </summary>
    private void KeepWindowVisibleFallback()
    {
        TrayUnavailable = true;
        try
        {
            // 没有托盘可回，就不该提供"最小化到托盘"这个选项
            if (WindowCloseAction == CloseAction.MinimizeToTray)
            {
                WindowCloseAction = CloseAction.Exit;
                _log.Warn("因托盘不可用，关闭行为已自动改为「彻底退出」");
            }
        }
        catch
        {
            // 兜底失败也不影响采集
        }
    }

    /// <summary>
    /// 读取当前进程的完整性级别。
    ///
    /// 为什么需要它：低完整性进程被强制完整性控制（MIC）禁止向中完整性的
    /// explorer 发送窗口消息，而 `Shell_NotifyIcon` 本质就是发消息，
    /// 因此必然返回"拒绝访问"。知道这一点才能给用户可执行的建议，
    /// 而不是让他对着一个永远不出现的托盘图标猜。
    ///
    /// 返回 null 表示读取失败。
    /// </summary>
    private static string? GetIntegrityLevelName()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buf = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out token)) return null;

            uint len;
            GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out len);
            if (len == 0) return null;

            buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)len);
            if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buf, len, out len))
                return null;

            var label = System.Runtime.InteropServices.Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buf);
            IntPtr sidStr;
            if (!ConvertSidToStringSidW(label.Label.Sid, out sidStr)) return null;

            string sid = System.Runtime.InteropServices.Marshal.PtrToStringUni(sidStr) ?? "";
            LocalFree(sidStr);

            int dash = sid.LastIndexOf('-');
            if (dash < 0 || !int.TryParse(sid.Substring(dash + 1), out int v)) return sid;

            return v switch
            {
                0x0000 => "不受信任",
                0x1000 => "低",
                0x2000 => "中",
                0x2100 => "中+",
                0x3000 => "高",
                0x4000 => "系统",
                _ => "0x" + v.ToString("X"),
            };
        }
        catch
        {
            return null;
        }
        finally
        {
            if (buf != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeHGlobal(buf);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private const uint TOKEN_QUERY = 0x0008;

    private enum TOKEN_INFORMATION_CLASS { TokenIntegrityLevel = 25 }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES { public IntPtr Sid; public uint Attributes; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL { public SID_AND_ATTRIBUTES Label; }

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS cls,
        IntPtr info, uint len, out uint retLen);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr str);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr ptr);

    /// <summary>
    /// 把托盘状态写成一个人可读的文件，放在数据目录里。
    ///
    /// 为什么要单独一个文件：日志有几百行，用户不容易找到关键结论；
    /// 而这个问题又高度依赖运行环境，需要用户把结论直接贴回来。
    /// </summary>
    private void WriteTrayStatusFile(string verdict)
    {
        try
        {
            string path = Path.Combine(AppPaths.DataDirectory, "tray-status.txt");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("屏幕使用时间 · 托盘图标状态");
            sb.AppendLine("生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine();
            sb.AppendLine("结论：" + verdict);
            sb.AppendLine();
            string? il = GetIntegrityLevelName();
            bool lowIntegrity = il == "低" || il == "不受信任";

            sb.AppendLine("—— 运行环境 ——");
            sb.AppendLine("进程 ID    ：" + Environment.ProcessId);
            sb.AppendLine("会话 ID    ：" + System.Diagnostics.Process.GetCurrentProcess().SessionId);
            sb.AppendLine("完整性级别 ：" + (il ?? "读取失败") +
                          (lowIntegrity ? "   ← 这是托盘失败的根本原因" : ""));
            sb.AppendLine("数据目录   ：" + AppPaths.DataDirectory);
            sb.AppendLine("目录来源   ：" + (AppPaths.UsedFallback
                ? "降级（标准位置 %LOCALAPPDATA% 被拒绝写入）"
                : "标准位置 %LOCALAPPDATA%"));
            sb.AppendLine("用户名     ：" + Environment.UserName);
            sb.AppendLine("系统版本   ：" + Environment.OSVersion.VersionString);
            sb.AppendLine();
            sb.AppendLine("—— 数据目录解析过程 ——");
            foreach (string line in AppPaths.ResolutionLog) sb.AppendLine("  " + line);
            sb.AppendLine();

            if (lowIntegrity)
            {
                sb.AppendLine("—— 原因（已定位）——");
                sb.AppendLine("本进程运行在「低完整性级别」，而任务栏/托盘由「中完整性」的 explorer.exe 提供。");
                sb.AppendLine("Windows 的强制完整性控制（MIC）禁止低完整性进程向中完整性进程发送窗口消息，");
                sb.AppendLine("而注册托盘图标（Shell_NotifyIcon）本质就是发消息，因此系统返回「拒绝访问」。");
                sb.AppendLine("这是 Windows 的硬性安全边界，程序自身无法绕过。");
                sb.AppendLine("同一原因也会导致：无法写入 %LOCALAPPDATA%、进程间唤醒消息送不到。");
                sb.AppendLine();
                sb.AppendLine("—— 解决办法 ——");
                sb.AppendLine("用正常权限启动本程序（关键：不要从受限终端/沙箱里启动）。任选一种：");
                sb.AppendLine("  1. 在文件资源管理器里双击 dist\\ScreenTime.App.exe；");
                sb.AppendLine("  2. 双击桌面上的「屏幕使用时间」快捷方式；");
                sb.AppendLine("  3. 按 Win+R，粘贴完整路径后回车。");
                sb.AppendLine("在开始菜单搜索框里输入 exe 路径再回车也可以。");
                sb.AppendLine();
                sb.AppendLine("启动成功的标志：本文件的「完整性级别」显示为「中」，");
                sb.AppendLine("且「目录来源」显示为「标准位置 %LOCALAPPDATA%」。");
            }
            else
            {
                sb.AppendLine("—— 说明 ——");
                sb.AppendLine("完整性级别正常。若仍然看不到托盘图标，请在任务栏右下角点 ^ 箭头，");
                sb.AppendLine("把本程序的时钟图标拖到任务栏上固定显示（Windows 11 默认折叠新图标）。");
            }
            sb.AppendLine();
            sb.AppendLine("即使托盘不可用，程序仍会正常记录；窗口关掉后再次双击启动即可唤回。");

            File.WriteAllText(path, sb.ToString(), System.Text.Encoding.UTF8);
            _log.Warn($"托盘状态已写入：{path}");
        }
        catch
        {
            // 写不了就算了，不影响采集
        }
    }

    /// <summary>
    /// 托盘与采集的宿主。界面由 WPF 的 <see cref="MainWindow"/> 承担，
    /// 本类只负责托盘图标、右键菜单与采集循环，并在退出时通知 WPF 关闭消息循环。
    /// </summary>
    /// <param name="requestShutdown">要求整个应用退出的回调（通常是 Application.Shutdown）。</param>
    public TrayContext(UsageStore store, Recorder recorder, Log log, Action requestShutdown)
    {
        _store = store;
        _recorder = recorder;
        _log = log;
        _requestShutdown = requestShutdown;

        _recorder.GapDetected += seconds =>
        {
            _log.Warn($"检测到时间缺口 {seconds} 秒（睡眠或时钟跳变），已按熄屏回填");
            _recorder.Flush();
        };
        _recorder.StateChanged += msg => _log.Info($"状态变化: {msg}");
        _recorder.Heartbeat += written => _log.Info($"采集心跳：本轮写入 {written} 个片段");

        _statusItem = new ToolStripMenuItem("正在记录…") { Enabled = false };
        _pauseItem = new ToolStripMenuItem("暂停记录");
        _pauseItem.Click += (_, _) => TogglePause();

        var openData = new ToolStripMenuItem("打开数据目录");
        openData.Click += (_, _) => OpenDataFolder();

        var openWindow = new ToolStripMenuItem("打开主界面");
        openWindow.Click += (_, _) => ShowWindow();

        var openLog = new ToolStripMenuItem("查看日志");
        openLog.Click += (_, _) => OpenPath(_log.LogPath);

        // 关闭行为：让用户随时改回来，而不是一旦"记住"就再也找不到入口
        _closeActionItem = new ToolStripMenuItem("关闭窗口时：每次询问");
        _closeActionItem.DropDownItems.Add(MakeCloseActionItem("每次询问", CloseAction.Ask));
        _closeActionItem.DropDownItems.Add(MakeCloseActionItem("最小化到托盘继续记录", CloseAction.MinimizeToTray));
        _closeActionItem.DropDownItems.Add(MakeCloseActionItem("彻底退出程序", CloseAction.Exit));
        UpdateCloseActionMenu();

        var exit = new ToolStripMenuItem("退出");
        exit.Click += (_, _) => ExitApp();

        _menu = new ContextMenuStrip();
        _menu.Items.Add(_statusItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(openWindow);
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_closeActionItem);
        _menu.Items.Add(openData);
        _menu.Items.Add(openLog);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(exit);

        _tray = CreateTrayIcon();

        // 关键：通知区域图标归 explorer.exe 所有，explorer 重启会清空所有托盘图标。
        // 系统重启后会广播 TaskbarCreated 消息，必须监听它并重新注册，
        // 否则图标会永久消失（用户会以为程序没在运行）。
        HookTaskbarCreated();

        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;

        _recorder.Start(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _timer.Start();

        _log.Info($"采集已启动，数据库: {_store.DatabasePath}");
        _log.Info($"空闲阈值 {_recorder.Idle.IdleThresholdSeconds} 秒，落库间隔 {_recorder.FlushIntervalSeconds} 秒");

        // 托盘诊断：出问题时需要能区分"注册失败"与"被系统折叠进溢出区"
        try
        {
            using Graphics g = Graphics.FromHwnd(IntPtr.Zero);
            _log.Info($"托盘图标已创建：尺寸 {_tray.Icon?.Width}x{_tray.Icon?.Height}，" +
                      $"Visible={_tray.Visible}，屏幕 DPI={Math.Round(g.DpiX)}（{Math.Round(g.DpiX / 96 * 100)}%）");
        }
        catch
        {
            // 诊断失败不影响运行
        }

        // 主动校验：NotifyIcon.Visible = true 在注册被拒绝时**不会抛异常**，
        // 只会在内部静默失败。若不做这一步，程序"以为"图标在那儿，
        // 用户却既看不到图标、又已经把窗口收进了托盘 —— 彻底失联。
        bool registered = VerifyTrayRegistered();
        WriteTrayStatusFile(registered
            ? "托盘图标注册成功（若界面上看不到，是被 Windows 11 折叠进了溢出区）"
            : "系统拒绝了托盘图标的注册");

        if (!registered)
        {
            _log.Warn("托盘图标注册未生效（受限会话或系统拒绝），将保持主窗口可见以免失联");
            KeepWindowVisibleFallback();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!_paused) _recorder.Tick(now);

            // 另一个实例请求接管（用户又双击了一次快捷方式）。
            // 唤醒消息在受限会话里可能送不到，所以用文件传递请求作为兜底。
            if (CheckTakeoverRequest()) return;

            // 跨天时把昨天固化进 day_summary，并记录一次日志
            if (DateTime.Today != _lastSummaryDay)
            {
                DateTime finished = _lastSummaryDay;
                _lastSummaryDay = DateTime.Today;
                try
                {
                    _recorder.Flush();
                    _store.RebuildDaySummary(finished);
                    _log.Info($"已固化 {finished:yyyy-MM-dd} 的日汇总");
                }
                catch (Exception ex)
                {
                    _log.Error($"固化日汇总失败: {ex.Message}");
                }
            }

            UpdateTray();
        }
        catch (Exception ex)
        {
            _log.Error($"采集循环异常: {ex}");
        }
    }

    private void UpdateTray()
    {
        Sample? s = _recorder.CurrentSample;
        if (s is null) return;

        string state = s.Value.State switch
        {
            UsageState.Active => "使用中",
            UsageState.Idle => "空闲",
            UsageState.Locked => "已锁屏",
            UsageState.Off => "熄屏/睡眠",
            _ => "未知",
        };

        // 托盘提示最多 63 字符（Windows 限制），需要截断
        string text = _paused
            ? "屏幕使用时间 · 已暂停"
            : $"屏幕使用时间 · {state} · {Truncate(s.Value.App.DisplayName, 28)}";
        _tray.Text = Truncate(text, 62);

        _statusItem.Text = _paused
            ? "已暂停记录"
            : $"{state} · {Truncate(s.Value.App.DisplayName, 20)}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private void TogglePause()
    {
        _paused = !_paused;
        _pauseItem.Text = _paused ? "继续记录" : "暂停记录";
        if (_paused)
        {
            _recorder.Stop();   // 收尾并落库，避免暂停期间产生虚假连续时长
            _log.Info("用户暂停记录");
        }
        else
        {
            _recorder.Start(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _log.Info("用户恢复记录");
        }
        UpdateTray();
    }

    /// <summary>在控制台（若可用）打印当天汇总；托盘双击时调用。</summary>
    public void ShowTodaySummary(bool showBalloon)
    {
        try
        {
            _recorder.Flush();
            DateTime today = DateTime.Today;
            DayTotals t = _store.GetDayTotals(today);
            List<AppTotal> apps = _store.GetAppTotals(today);
            (long rows, long bytes) = _store.StoreInfo();

            if (ConsoleHelper.Ensure())
            {
                Console.WriteLine();
                Console.WriteLine($"=== {today:yyyy-MM-dd} 屏幕使用时间 ===");
                Console.WriteLine($"  前台使用      {SelfTest.Fmt(t.ActiveSeconds)}");
                Console.WriteLine($"  空闲          {SelfTest.Fmt(t.IdleSeconds)}");
                Console.WriteLine($"  锁屏          {SelfTest.Fmt(t.LockedSeconds)}");
                Console.WriteLine($"  熄屏 / 睡眠   {SelfTest.Fmt(t.OffSeconds)}");
                Console.WriteLine($"  屏幕亮着合计  {SelfTest.Fmt(t.ScreenOnSeconds)}");
                Console.WriteLine($"  不同应用      {t.DistinctApps} 个");
                Console.WriteLine($"  片段行数      {rows}，数据库 {bytes / 1024.0:F1} KB");
                if (apps.Count > 0)
                {
                    Console.WriteLine("  排行:");
                    foreach (AppTotal a in apps.Take(12))
                    {
                        Console.WriteLine($"    {SelfTest.Fmt(a.Seconds),-12} {a.DisplayName}");
                    }
                }
                Console.WriteLine();
            }

            if (showBalloon)
            {
                _tray.BalloonTipTitle = "今日屏幕使用时间";
                _tray.BalloonTipText = $"前台使用 {SelfTest.Fmt(t.ActiveSeconds)}\n" +
                                       $"熄屏/睡眠 {SelfTest.Fmt(t.OffSeconds)}\n" +
                                       $"共 {t.DistinctApps} 个应用";
                _tray.ShowBalloonTip(4000);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"打印汇总失败: {ex.Message}");
        }
    }

    private static void OpenDataFolder()
    {
        string dir = AppPaths.DataDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            OpenPath(dir);
        }
        catch
        {
            // 忽略
        }
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>
    /// 彻底退出程序（供界面与托盘菜单调用）。
    ///
    /// 先把 IsExiting 置位：这样窗口随后被关闭时不会再弹一次"关闭还是退出"的询问。
    /// </summary>
    public void RequestExit() => ExitApp();

    /// <summary>把窗口收进托盘（隐藏而不是关闭）。</summary>
    public void HideWindow()
    {
        try
        {
            if (_window is { IsVisible: true }) _window.Hide();
        }
        catch (Exception ex)
        {
            _log.Error($"最小化到托盘失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 应用运行时可变设置（空闲阈值、落库间隔）。
    /// 设置面板改动后调用，避免"改了要重启才生效"。
    /// </summary>
    public void ApplyRuntimeSettings()
    {
        try
        {
            AppSettings s = AppSettings.Current;
            _recorder.Configure(s.IdleThresholdSeconds, s.FlushIntervalSeconds,
                                s.FullscreenCountsAsActive);
            _log.Info($"已应用设置：空闲阈值 {s.IdleThresholdSeconds}s，" +
                      $"落库间隔 {s.FlushIntervalSeconds}s，" +
                      $"全屏算作活跃 {(s.FullscreenCountsAsActive ? "是" : "否")}");
        }
        catch (Exception ex)
        {
            _log.Warn($"应用设置失败：{ex.Message}");
        }

        // 自启自愈：把缺失的计划任务补齐。
        //
        // 计划任务会因各种原因消失（卸载程序清理、重装用了旧安装程序、
        // 用户手动删除、Windows 更新等），而启动文件夹那条路在本机不生效，
        // 结果就是"勾了自启但重启后没启动"。
        //
        // 判据不能只看设置里的 AutoStart 标志——那个标志有可能因为
        // 命令行未写回（老版本 --autostart on 就漏了）、设置文件被重置等原因失真。
        // 再加一条：**系统里还存在启动项**（快捷方式或计划任务）也视为用户想自启。
        // 两道判据取并集，比单看标志可靠得多。
        try
        {
            AppSettings s = AppSettings.Current;
            bool userWantsAutoStart = s.AutoStart || AutoStart.AnyEntryExists();

            if (userWantsAutoStart)
            {
                string? exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    if (AutoStart.EnsureTask(exe, out string detail))
                    {
                        _log.Info($"自启检查：{detail}");
                        // 顺带把标志纠正回来，让设置面板的勾选状态与实际情况一致
                        if (!s.AutoStart)
                        {
                            s.AutoStart = true;
                            s.Save();
                            _log.Info("自启检查：设置里的标志与实际不符，已纠正为已启用");
                        }
                    }
                    else
                    {
                        _log.Warn($"自启检查：计划任务缺失且重建失败 —— {detail}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn($"自启自愈失败：{ex.Message}");
        }
    }

    private void ExitApp()
    {
        IsExiting = true;
        _timer.Stop();

        // 如果"关闭询问"对话框正开着，先关掉它，否则它会阻塞窗口关闭
        try { _mainWindow?.ClosePendingDialog(); }
        catch { /* 忽略 */ }

        try
        {
            _recorder.Stop();
            _store.RebuildDaySummary(DateTime.Today);
            _log.Info("采集已停止，数据已落库");
        }
        catch (Exception ex)
        {
            _log.Error($"退出时落库失败: {ex.Message}");
        }

        // 退出时只收尾并请求关闭；真正的释放留给 Program 在消息循环结束后做，
        // 否则界面在关闭过程中还可能访问已释放的数据库/日志对象。
        _tray.Visible = false;
        _tray.Dispose();
        _taskbarWatcher?.Dispose();
        _timer.Stop();

        try { _requestShutdown(); }
        catch { /* 已退出时忽略 */ }
    }

    /// <summary>释放采集与存储资源。由 Program 在消息循环结束后调用。</summary>
    public void Dispose()
    {
        // DispatcherTimer 没有 Dispose，Stop 即解除订阅
        try { _timer.Stop(); } catch { }
        try { _recorder.Dispose(); } catch { }
        try { _store.Dispose(); } catch { }
        try { _log.Dispose(); } catch { }
    }

    /// <summary>
    /// 监听 shell 的 TaskbarCreated 广播，在 explorer 重启后重新注册托盘图标。
    ///
    /// 背景：Shell_NotifyIcon 注册的通知区域图标由 explorer.exe 持有。
    /// explorer 崩溃或被重启时所有图标会被一次性清除，随后系统广播 TaskbarCreated，
    /// 收到它的程序必须重新注册。不处理这个消息的应用，图标就永久消失了。
    ///
    /// 实现要点：用一个**独立的隐藏顶层窗口**接收广播。
    /// 不能去 AssignHandle 抢 NotifyIcon 自己的窗口过程——那会破坏它的正常工作；
    /// 也不能用消息窗口，因为广播消息只发给顶层窗口。
    /// （PowerWatcher 接收电源通知用的是同一套 NativeWindow 模式，已验证可行。）
    /// </summary>
    private void HookTaskbarCreated()
    {
        try
        {
            uint msg = RegisterWindowMessage("TaskbarCreated");
            if (msg == 0)
            {
                _log.Warn("注册 TaskbarCreated 消息失败，explorer 重启后托盘图标可能消失");
                return;
            }

            // 同时注册唤醒消息：另一个实例启动时用它请求唤出本实例的窗口
            uint wake = RegisterWindowMessage(Program.WakeUpMessage);

            _wmTaskbarCreated = msg;
            _taskbarWatcher = new TaskbarWatcherWindow(this, msg, wake);
            _log.Info($"已挂接 TaskbarCreated(0x{msg:X})，explorer 重启后会自动恢复托盘图标");
            if (wake != 0) _log.Info($"已挂接唤醒消息(0x{wake:X})，重复启动时会唤出本窗口");
        }
        catch (Exception ex)
        {
            _log.Warn($"挂接 TaskbarCreated 失败（不影响正常使用）：{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>系统广播：任务栏已重建 → 重新注册托盘图标。</summary>
    private void OnTaskbarCreated()
    {
        try
        {
            // 不用 "Visible=false; Visible=true" 复用旧对象：
            // explorer 重启后 NotifyIcon 的内部注册状态可能已失效，
            // 直接重建一个新的最稳妥（代价可忽略）。
            NotifyIcon? old = _tray;
            try
            {
                old.Visible = false;
                old.Dispose();
            }
            catch
            {
                // 旧对象可能已失效，忽略
            }

            _tray = CreateTrayIcon();
            _log.Info("检测到任务栏重建（explorer 重启），已重新注册托盘图标");
        }
        catch (Exception ex)
        {
            _log.Error($"重新注册托盘图标失败：{ex.Message}");
        }
    }

    /// <summary>创建托盘图标并绑定菜单/事件。启动时与 explorer 重启恢复时共用。</summary>
    private NotifyIcon CreateTrayIcon()
    {
        var tray = new NotifyIcon
        {
            Icon = BuildIcon(),
            Text = "屏幕使用时间",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        tray.DoubleClick += (_, _) => ShowWindow();
        return tray;
    }

    /// <summary>由 Program 注入主窗口引用，供托盘双击唤回。</summary>
    public void AttachWindow(Window window)
    {
        _window = window;
        _mainWindow = window as MainWindow;
    }

    private Window? _window;
    private MainWindow? _mainWindow;

    /// <summary>
    /// 唤回主窗口（托盘双击 / 菜单 / 另一个实例的唤醒广播）。
    ///
    /// 只做 WPF 层面的 Show/Activate，由 WPF 自己完成渲染目标的重建。
    /// **不要跨进程调用 ShowWindow(SW_RESTORE) 去显示别人 Hide() 的窗口**——
    /// 那会让窗口状态与渲染目标不一致，界面变成全黑（踩过的坑）。
    /// </summary>
    private void ShowWindow()
    {
        try
        {
            _log.Info("唤出主界面");
            if (_mainWindow is not null)
            {
                _mainWindow.ShowAndFocus();
                return;
            }

            if (_window is null)
            {
                // 走到这里说明窗口对象没挂接上来（正常不该发生）。
                // 早期版本在静默模式下就是这个状态，导致双击无任何反应，
                // 所以这里必须留下日志，不能再静默返回。
                _log.Warn("唤出主界面失败：窗口对象未挂接");
                return;
            }

            if (!_window.IsVisible) _window.Show();
            if (_window.WindowState == WpfWindowState.Minimized)
                _window.WindowState = WpfWindowState.Normal;
            _window.Activate();
        }
        catch (Exception ex)
        {
            _log.Error($"唤回主窗口失败: {ex.Message}");
        }
    }

    /// <summary>只用于接收 TaskbarCreated 广播的隐藏顶层窗口。</summary>
    private sealed class TaskbarWatcherWindow : NativeWindow, IDisposable
    {
        private readonly TrayContext _owner;
        private readonly uint _msg;
        private readonly uint _wakeMsg;

        public TaskbarWatcherWindow(TrayContext owner, uint msg, uint wakeMsg)
        {
            _owner = owner;
            _msg = msg;
            _wakeMsg = wakeMsg;
            CreateHandle(new CreateParams { Caption = "ScreenTime.TaskbarWatcher" });
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == _msg) _owner.OnTaskbarCreated();
            // 另一个实例请求唤出窗口（用户再次双击了快捷方式）
            else if (_wakeMsg != 0 && m.Msg == _wakeMsg) _owner.ShowWindow();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }

    /// <summary>
    /// 取托盘图标。**必须用 16x16 的小图标**，不能用 ExtractAssociatedIcon
    /// ——它只返回 32x32，托盘按 16x16 渲染时会出现缩放异常甚至不显示。
    /// 优先级：ExtractIconEx 的小图标（16x16）→ 大图标 → 程序内绘制兜底。
    /// </summary>
    private static Icon BuildIcon()
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length > 0 && File.Exists(exe))
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            try
            {
                uint n = ExtractIconEx(exe, 0, large, small, 1);
                if (n > 0)
                {
                    // 托盘用小的；退而求其次用大的
                    IntPtr h = small[0] != IntPtr.Zero ? small[0] : large[0];
                    if (h != IntPtr.Zero)
                    {
                        using var tmp = Icon.FromHandle(h);
                        return (Icon)tmp.Clone();
                    }
                }
            }
            catch
            {
                // 落到绘制兜底
            }
            finally
            {
                if (large[0] != IntPtr.Zero) DestroyIcon(large[0]);
                if (small[0] != IntPtr.Zero) DestroyIcon(small[0]);
            }
        }

        return DrawFallbackIcon();
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    /// <summary>程序内绘制的兜底图标。</summary>
    private static Icon DrawFallbackIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB));
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 3f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            g.DrawLine(pen, 16, 8, 16, 17);
            g.DrawLine(pen, 16, 17, 22, 21);
        }
        IntPtr h = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(h);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(h);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
