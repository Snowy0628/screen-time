using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Windows.Media.Control;

namespace ScreenTime.Core;

/// <summary>
/// 媒体播放观察器：判断**前台程序是否正在播放媒体**。
///
/// ### 它解决什么问题
///
/// 原来的空闲判据是"前台窗口是否占据整屏（最大化/全屏）"。这有两个盲区：
///   · 最大化发呆 → 被判为在使用（其实人已经走了）
///   · 窗口化看视频 → 被判为空闲（其实人在看）
///
/// 现在改为看"前台程序有没有在放东西"：
///   · 前台在放媒体        → 使用中（不管窗口大小）
///   · 放媒体但不在前台    → 照常按空闲计时（后台听歌不该算使用）
///   · 暂停/停止           → 不算在放
///
/// ### 为什么用 SMTC 而不是音频会话 API
///
/// 先试过核心音频 API（IAudioSessionManager2）读"哪个进程在出声"。两个问题：
///   1. 那个接口要手写 COM vtable 才能可靠调用，`[ComImport]` 声明一路抛
///      InvalidCastException、看不出原因（最后用原始 vtable 调才发现是
///      接口不支持，不是索引写错）
///   2. 更根本的是，它只能告诉我"有声"，区分不了"看视频"和"听音乐"
///
/// SMTC（系统媒体传输控件，任务栏那个媒体小弹窗的数据源）直接给出：
///   · 是哪个程序在播（SourceAppUserModelId）
///   · 播放状态（Playing / Paused / Stopped）
/// 拿它和**前台进程**比对，就正好实现了需要的规则，而且不需要维护任何
/// 播放器白名单——用户明确担心过白名单的兼容性。
///
/// ### 线程模型
///
/// `IsIdle()` 是同步调用（每秒一次，在采集循环里），而 SMTC 只有异步 API。
/// 所以这里在后台线程周期性查询、把结果缓存到一个 volatile 字段，
/// 同步读缓存。这样采集循环永远不会被网络/COM 调用阻塞。
/// </summary>
public sealed class MediaWatcher : IDisposable
{
    /// <summary>两次查询之间的间隔。3 秒足够灵敏，又不会把 SMTC 打得太频繁。</summary>
    private const int PollIntervalMs = 3000;

    /// <summary>
    /// 日志回调。Core 层不依赖 App 层的 Log 类，所以用委托把消息递出去；
    /// 传 null 就完全不记录（Core 也被自检程序用，那些场景不需要日志）。
    /// </summary>
    private readonly Action<string, bool>? _log;

    private Thread? _thread;
    private volatile bool _stop;

    /// <summary>最近一次查询的结论：前台进程是否正在播放媒体。</summary>
    private volatile bool _foregroundPlaying;

    /// <summary>最近一次判断用到的前台进程名，仅供诊断输出。</summary>
    private volatile string _lastForeground = "";

    /// <summary>最近一次判定为"正在播放"的会话名，仅供诊断输出。</summary>
    private volatile string _lastPlayingSession = "";

    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    /// <param name="log">日志回调：(消息, 是否警告)。可为 null。</param>
    public MediaWatcher(Action<string, bool>? log = null)
    {
        _log = log;
        try
        {
            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "MediaWatcher",
            };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        catch (Exception ex)
        {
            Warn($"媒体观察器启动失败（将退化为不使用该判据）：{ex.GetType().Name}: {ex.Message}");
            _thread = null;
        }
    }

    private void Info(string m) => _log?.Invoke(m, false);
    private void Warn(string m) => _log?.Invoke(m, true);

    /// <summary>前台进程当前是否正在播放媒体。同步读缓存，不会阻塞。</summary>
    public bool ForegroundIsPlaying => _foregroundPlaying;

    /// <summary>最近一次查询时前台进程的名字。仅供诊断。</summary>
    public string LastForegroundProcess => _lastForeground;

    /// <summary>最近一次判定为"正在播放"的会话名。仅供诊断。</summary>
    public string LastPlayingSession => _lastPlayingSession;

    /// <summary>
    /// 取当前前台进程名（诊断用）。
    /// NativeMethods 在 Core 内是 internal，App 层拿不到，所以在这里开个口子。
    /// </summary>
    public static string CurrentForegroundProcessName()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "";
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    /// <summary>取当前前台进程的可执行文件路径（诊断用，可能因权限取不到）。</summary>
    public static string CurrentForegroundProcessPath()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return "";
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0) return "";
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>诊断用：把当前判定依据写成一行文本。</summary>
    public string Describe() =>
        $"前台进程=[{_lastForeground}] 正在播放的会话=[{_lastPlayingSession}] " +
        $"→ 判定={(_foregroundPlaying ? "在放媒体（算使用中）" : "没在放（按空闲计时）")}";

    private void Loop()
    {
        // 首次获取管理器可能要几百毫秒，失败也重试——
        // 某些时刻（刚开机、会话切换）SMTC 会短暂不可用。
        while (!_stop && _manager is null)
        {
            try
            {
                _manager = GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().AsTask().GetAwaiter().GetResult();
                if (_manager is not null) Info("媒体观察器就绪（SMTC 可用）");
            }
            catch (Exception ex)
            {
                Warn($"获取媒体会话管理器失败，10 秒后重试：{ex.GetType().Name}: {ex.Message}");
            }

            if (_manager is null && !_stop) Sleep(10000);
        }

        while (!_stop)
        {
            try
            {
                Sample();
            }
            catch (Exception ex)
            {
                // 单次失败不影响后续：把结论置为"没在放"，退化成旧行为
                _foregroundPlaying = false;
                Warn($"媒体查询失败：{ex.GetType().Name}: {ex.Message}");
            }

            Sleep(PollIntervalMs);
        }
    }

    private void Sleep(int ms)
    {
        // 分片等待，退出时能及时收尾
        int waited = 0;
        while (!_stop && waited < ms)
        {
            Thread.Sleep(100);
            waited += 100;
        }
    }

    private void Sample()
    {
        if (_manager is null) return;

        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            _foregroundPlaying = false;
            _lastForeground = "(无前台窗口)";
            return;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint fgPid);
        if (fgPid == 0)
        {
            _foregroundPlaying = false;
            return;
        }

        string fgName = "";
        try
        {
            using Process p = Process.GetProcessById((int)fgPid);
            fgName = p.ProcessName;
        }
        catch
        {
            // 进程已退出或无权读取：当作没在放
        }
        _lastForeground = fgName.Length > 0 ? fgName : $"(pid {fgPid})";

        // 找出所有"正在播放"的会话，看有没有属于前台进程的
        string playing = "";
        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions = _manager.GetSessions();
        for (int i = 0; i < sessions.Count; i++)
        {
            GlobalSystemMediaTransportControlsSession s = sessions[i];
            GlobalSystemMediaTransportControlsSessionPlaybackInfo? pb = s.GetPlaybackInfo();
            if (pb is null) continue;

            // 只有 Playing 才算"在放"。Paused / Stopped / Closed 都不算——
            // 暂停了通常意味着人已经去干别的了。
            if (pb.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                continue;

            string appId = s.SourceAppUserModelId ?? "";
            if (!NamesMatch(appId, fgName)) continue;

            playing = appId;
            break;
        }

        _lastPlayingSession = playing;
        _foregroundPlaying = playing.Length > 0;
    }

    /// <summary>
    /// 判断某个媒体会话是不是属于前台进程。
    ///
    /// 会话给的是 AppUserModelId，它**不一定等于进程名**：
    ///   PotPlayer → "PotPlayerMini64.exe"  （正好是进程名 + .exe）
    ///   Edge      → "MSEdge"               （进程名是 msedge）
    ///
    /// 所以不能只做相等比较，要做几种宽松匹配。宁可漏判也不要误判：
    /// 误判成"在放"会让真正的空闲被记成使用，那是用户最在意的数据错误。
    ///
    /// 公开出来是为了让 `--mediacheck` 诊断命令复用同一套规则——
    /// 诊断和实际行为必须用同一份实现，否则诊断结果没有参考价值。
    /// </summary>
    public static bool NamesMatch(string appId, string processName)
    {
        if (appId.Length == 0 || processName.Length == 0) return false;

        // 去掉可能存在的路径与 .exe 后缀，统一成"程序名"再比较
        string a = ShortName(appId);
        string b = ShortName(processName);

        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;

        // "MSEdge" vs "msedge"：一边是另一边的子串也算匹配
        if (a.Length >= 4 && b.Length >= 4)
        {
            if (a.Contains(b, StringComparison.OrdinalIgnoreCase)) return true;
            if (b.Contains(a, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    private static string ShortName(string s)
    {
        int slash = s.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) s = s.Substring(slash + 1);
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(0, s.Length - 4);
        return s;
    }

    public void Dispose()
    {
        _stop = true;
        try
        {
            _thread?.Join(2000);
        }
        catch
        {
            // 忽略
        }
        _thread = null;
    }
}
