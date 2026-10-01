using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTime.Core;

/// <summary>
/// 把前台窗口解析为"用户实际在用的应用"。
/// 需要处理的关键边界：
///   1. UWP / 商店应用的真身藏在 ApplicationFrameHost.exe 的子窗口里，必须解包；
///   2. 提权进程（任务管理器等）读不到路径，只能降级为"未知"；
///   3. 壳进程（开始菜单、搜索、锁屏）需要过滤，否则会污染统计。
/// </summary>
public sealed class AppResolver
{
    private const string FrameHostExe = "ApplicationFrameHost.exe";
    private const string ApplicationFrameHostClass = "ApplicationFrameWindow";

    private readonly ConcurrentDictionary<uint, AppIdentity> _byPid = new();
    private readonly ConcurrentDictionary<string, AppIdentity> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 不参与统计的系统壳进程（含 .exe）。
    ///
    /// 判据：这些进程对应的窗口**不是用户"在用的应用"**——
    /// 桌面本身、锁屏、输入法候选框、DWM 合成器等。
    /// 它们出现在前台时返回 null，由调用方记为"未知/未归属"。
    ///
    /// **注意不要把程序自己放进来**：曾经把 `ScreenTime.App.exe` 列在此处，
    /// 结果是用户聚焦到本程序界面时被归为"未知"，
    /// 在时间轴和排行榜里显示成"未知"而不是"屏幕使用时间"。
    /// 用户在看本程序的统计，这本身就是一次真实的使用，应当被记录。
    /// </summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "unknown",
        "LockApp.exe",
        "LogonUI.exe",
        "SearchHost.exe",
        "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe",
        "TextInputHost.exe",
        "Widgets.exe",
        "WidgetService.exe",
        "SystemSettings.exe",
        "explorer.exe",              // 桌面本身不算"使用某个应用"；文件窗口会被单独识别
        "dwm.exe",
        "csrss.exe",
        "winlogon.exe",
    };

    /// <summary>解析当前前台窗口对应的应用。无前台窗口或无法识别时返回 null。</summary>
    public AppIdentity? ResolveForeground()
    {
        IntPtr hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        return ResolveWindow(hwnd);
    }

    /// <summary>把任意窗口解析为应用身份（含 UWP 解包）。</summary>
    public AppIdentity? ResolveWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;

        uint pid = GetWindowPid(hwnd);
        if (pid == 0) return null;

        string processName = GetProcessName(pid);

        // UWP：真身在子窗口里
        if (string.Equals(processName, FrameHostExe, StringComparison.OrdinalIgnoreCase)
            || IsApplicationFrameWindow(hwnd))
        {
            IntPtr child = FindChildAppWindow(hwnd);
            if (child != IntPtr.Zero)
            {
                uint childPid = GetWindowPid(child);
                if (childPid != 0 && childPid != pid)
                {
                    pid = childPid;
                    processName = GetProcessName(pid);
                }
            }
            else
            {
                // 解包失败（窗口正在切换/已销毁），本次不记录，避免误记成 ApplicationFrameHost
                return null;
            }
        }

        if (ShellProcesses.Contains(processName)) return null;

        return GetOrCreate(pid, processName);
    }

    private static uint GetWindowPid(IntPtr hwnd)
    {
        _ = NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    private static string GetProcessName(uint pid)
    {
        try
        {
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            // 提权进程或无权限：GetProcessById 会抛，退化为未知
            return "unknown";
        }
    }

    private static bool IsApplicationFrameWindow(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        int n = NativeMethods.GetClassNameW(hwnd, sb, sb.Capacity);
        return n > 0 && sb.ToString().Equals(ApplicationFrameHostClass, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>在 ApplicationFrameWindow 下找到承载真实应用的子窗口。</summary>
    private static IntPtr FindChildAppWindow(IntPtr parent)
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumChildWindows(parent, (h, _) =>
        {
            var sb = new StringBuilder(256);
            if (NativeMethods.GetClassNameW(h, sb, sb.Capacity) > 0)
            {
                string cls = sb.ToString();
                // Windows.UI.Core.CoreWindow 是 UWP 实际内容窗口
                if (cls.Equals("Windows.UI.Core.CoreWindow", StringComparison.OrdinalIgnoreCase)
                    || cls.StartsWith("Windows.UI.", StringComparison.OrdinalIgnoreCase))
                {
                    found = h;
                    return false;
                }
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private AppIdentity GetOrCreate(uint pid, string processName)
    {
        string path = GetProcessPath(pid) ?? "";

        // 无路径 → 无法稳定区分，返回未知
        if (path.Length == 0)
        {
            if (processName == "unknown") return AppIdentity.Unknown;
            // 有进程名但拿不到路径：用进程名当身份，路径留空
            return _byPid.GetOrAdd(pid, _ => new AppIdentity("", processName, DisplayNameFromProcess(processName)));
        }

        if (_byPath.TryGetValue(path, out AppIdentity? cached))
        {
            _byPid[pid] = cached;
            return cached;
        }

        var identity = new AppIdentity(path, processName, DisplayNameFromPath(path, processName));
        _byPath[path] = identity;
        _byPid[pid] = identity;
        return identity;
    }

    private static string? GetProcessPath(uint pid)
    {
        IntPtr h = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return NativeMethods.QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally
        {
            NativeMethods.CloseHandle(h);
        }
    }

    /// <summary>优先取可执行文件的产品/文件描述（"Visual Studio Code"），退化到清理过的文件名。</summary>
    private static string DisplayNameFromPath(string path, string fallbackProcessName)
    {
        // 本程序自己有专门的显示名：默认会退化成 "ScreenTime.App"，
        // 但用户在排行榜里应该看到的是产品名。
        if (string.Equals(fallbackProcessName, SelfProcessName, StringComparison.OrdinalIgnoreCase))
            return SelfDisplayName;

        try
        {
            FileVersionInfo vi = FileVersionInfo.GetVersionInfo(path);
            string? name = vi.FileDescription;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName!.Trim();
        }
        catch
        {
            // 忽略：退回进程名
        }
        return DisplayNameFromProcess(fallbackProcessName);
    }

    /// <summary>本程序的进程名与显示名。</summary>
    private const string SelfProcessName = "ScreenTime.App.exe";
    private const string SelfDisplayName = "屏幕使用时间";

    private static string DisplayNameFromProcess(string processName)
    {
        if (string.Equals(processName, SelfProcessName, StringComparison.OrdinalIgnoreCase))
            return SelfDisplayName;

        string n = processName;
        if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n[..^4];
        return n.Length == 0 ? "未知" : n;
    }
}
