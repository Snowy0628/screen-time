// 诊断：为什么 Shell_NotifyIcon 返回"拒绝访问"？
//
// Shell_NotifyIcon(NIM_ADD) 失败的错误码是 5（ERROR_ACCESS_DENIED），
// 常见成因有几种，必须区分开才能对症下药：
//
//   A. 进程完整性级别高于 explorer（提权进程 + UIPI 阻止跨级别消息）
//   B. 令牌受限（沙箱 / AppContainer / 低完整性）
//   C. 与 shell 不在同一会话（Session 0 隔离）
//   D. 注册表通知区域设置被策略锁定
//   E. 桌面/窗口站（Window Station）权限不足
//
// 这个程序把每一条都测出来，直接给出结论。
using System;
using System.Runtime.InteropServices;
using System.Text;

internal static class TrayWhy
{
    // ---------- 完整性级别 ----------
    private enum TOKEN_INFORMATION_CLASS
    {
        TokenUser = 1,
        TokenIntegrityLevel = 25,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SID_AND_ATTRIBUTES
    {
        public IntPtr Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_MANDATORY_LABEL
    {
        public SID_AND_ATTRIBUTES Label;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint desiredAccess, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, TOKEN_INFORMATION_CLASS cls,
        IntPtr info, uint len, out uint retLen);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr str);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeNameW(string? system, ref long luid, StringBuilder name, ref uint len);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr h);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetProcessWindowStation();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetThreadDesktop(uint threadId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformationW(IntPtr obj, int index,
        StringBuilder info, uint len, out uint needed);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);

    [DllImport("kernel32.dll")]
    private static extern int GetCurrentProcessId();

    // ---------- 结构 ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    private const uint NIM_ADD = 0x0;
    private const uint NIM_DELETE = 0x2;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4;
    private const uint TOKEN_QUERY = 0x0008;

    private delegate bool EnumProc(IntPtr h, IntPtr l);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumProc cb, IntPtr l);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(uint pid, out uint session);

    [STAThread]
    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("===== 托盘注册失败原因诊断 =====");
        Console.WriteLine($"进程 PID = {GetCurrentProcessId()}");
        Console.WriteLine($"线程 ID  = {GetCurrentThreadId()}");
        Console.WriteLine($"会话 ID  = {System.Diagnostics.Process.GetCurrentProcess().SessionId}");
        Console.WriteLine();

        int verdicts = 0;

        // ---- 1. 完整性级别 ----
        Console.WriteLine("[1] 进程完整性级别（与 explorer 比较，级别不同会被 UIPI 拦住）");
        string? selfIl = GetIntegrityLevel(GetCurrentProcess());
        Console.WriteLine($"    本进程 = {selfIl ?? "读取失败"}");

        IntPtr shell = GetShellWindow();
        uint shellPid = 0;
        if (shell != IntPtr.Zero) GetWindowThreadProcessId(shell, out shellPid);
        Console.WriteLine($"    shell 窗口 PID = {shellPid}");

        string? shellIl = shellPid != 0 ? GetIntegrityLevelByPid(shellPid) : null;
        Console.WriteLine($"    explorer  = {shellIl ?? "读取失败"}");

        if (selfIl is not null && shellIl is not null)
        {
            if (selfIl == shellIl)
                Console.WriteLine("    → 级别相同，不是 UIPI 问题");
            else
            {
                Console.WriteLine("    → ✗ 级别不同！高完整性进程无法向低完整性 shell 注册图标");
                verdicts++;
            }
        }
        Console.WriteLine();

        // ---- 2. 桌面 / 窗口站 ----
        Console.WriteLine("[2] 窗口站与桌面（Session 0 隔离或权限不足会导致失败）");
        IntPtr wsta = GetProcessWindowStation();
        string wstaName = GetObjName(wsta, 2);   // UOI_NAME = 2
        IntPtr desk = GetThreadDesktop(GetCurrentThreadId());
        string deskName = GetObjName(desk, 2);
        Console.WriteLine($"    窗口站 = {wstaName}（0x{wsta.ToInt64():X}）");
        Console.WriteLine($"    桌面   = {deskName}（0x{desk.ToInt64():X}）");
        if (wstaName.Contains("Service-", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("    → ✗ 运行在服务窗口站，根本看不到用户通知区域");
            verdicts++;
        }
        else if (deskName.Length == 0)
        {
            Console.WriteLine("    → ✗ 桌面句柄无效（无交互桌面）");
            verdicts++;
        }
        else
        {
            Console.WriteLine("    → 有交互桌面，可能是权限或策略问题");
        }
        Console.WriteLine();

        // ---- 3. 会话一致性 ----
        Console.WriteLine("[3] 会话一致性");
        uint mySession;
        ProcessIdToSessionId((uint)GetCurrentProcessId(), out mySession);
        uint shellSession = 999;
        if (shellPid != 0) ProcessIdToSessionId(shellPid, out shellSession);
        Console.WriteLine($"    本进程会话 = {mySession}，shell 会话 = {shellSession}");
        if (shellPid == 0)
        {
            Console.WriteLine("    → ✗ 找不到 shell 窗口（GetShellWindow 为空）");
            verdicts++;
        }
        else if (mySession != shellSession)
        {
            Console.WriteLine("    → ✗ 会话不同，跨会话无法注册通知区域图标");
            verdicts++;
        }
        else
        {
            Console.WriteLine("    → 会话一致");
        }
        Console.WriteLine();

        // ---- 4. 直接试探注册 ----
        Console.WriteLine("[4] 直接试探 Shell_NotifyIcon(NIM_ADD)，用不同 uID 试两次");
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = GetShellWindow(),      // 借用一个真实存在的顶层窗口句柄
                uID = (uint)(0x7000 + attempt),
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = 0x0400 + 0x70,
                hIcon = SystemIcons.Application.Handle,
                szTip = "TrayWhy 诊断",
            };
            bool ok = Shell_NotifyIcon(NIM_ADD, ref data);
            int err = Marshal.GetLastWin32Error();
            Console.WriteLine($"    第 {attempt} 次: 返回={ok}  错误码={err} ({DescribeError(err)})");
            if (ok) Shell_NotifyIcon(NIM_DELETE, ref data);
        }
        Console.WriteLine();

        // ---- 5. 相关注册表策略 ----
        Console.WriteLine("[5] 相关策略（只读检查）");
        CheckPolicy(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoTrayItemsDisplay");
        CheckPolicy(@"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoTrayItemsDisplay");
        CheckPolicy(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", "NoAutoTrayNotify");
        Console.WriteLine();

        Console.WriteLine("===== 结论 =====");
        if (verdicts == 0)
        {
            Console.WriteLine("没有发现明显的上下文问题。若 Shell_NotifyIcon 仍报拒绝访问，");
            Console.WriteLine("最可能是运行宿主对进程施加了额外的限制（例如沙箱的令牌约束），");
            Console.WriteLine("请改为在普通桌面会话里直接双击 exe 启动。");
        }
        else
        {
            Console.WriteLine($"发现 {verdicts} 项上下文问题，见上面标 ✗ 的行。");
        }
        return 0;
    }

    private static string GetObjName(IntPtr obj, int index)
    {
        if (obj == IntPtr.Zero) return "";
        var sb = new StringBuilder(256);
        uint needed;
        if (GetUserObjectInformationW(obj, index, sb, (uint)sb.Capacity, out needed)) return sb.ToString();
        return "";
    }

    private static string? GetIntegrityLevel(IntPtr process)
    {
        IntPtr token;
        if (!OpenProcessToken(process, TOKEN_QUERY, out token)) return null;
        try
        {
            uint len;
            GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, IntPtr.Zero, 0, out len);
            IntPtr buf = Marshal.AllocHGlobal((int)len);
            try
            {
                if (!GetTokenInformation(token, TOKEN_INFORMATION_CLASS.TokenIntegrityLevel, buf, len, out len))
                    return null;
                var label = Marshal.PtrToStructure<TOKEN_MANDATORY_LABEL>(buf);
                IntPtr str;
                if (!ConvertSidToStringSidW(label.Label.Sid, out str)) return null;
                string sid = Marshal.PtrToStringUni(str) ?? "";
                LocalFree(str);
                return sid + " (" + DescribeIl(sid) + ")";
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    private static string? GetIntegrityLevelByPid(uint pid)
    {
        IntPtr p = OpenProcess(0x0400 /*PROCESS_QUERY_INFORMATION*/, false, pid);
        if (p == IntPtr.Zero) return "无法打开进程（权限不足）";
        try { return GetIntegrityLevel(p); }
        finally { CloseHandle(p); }
    }

    /// <summary>完整性级别 SID 的末段：0x1000=低 0x2000=中 0x3000=高 0x4000=系统</summary>
    private static string DescribeIl(string sid)
    {
        int dash = sid.LastIndexOf('-');
        if (dash < 0) return "未知";
        if (!int.TryParse(sid.Substring(dash + 1), out int v)) return "未知";
        return v switch
        {
            0x0000 => "不受信任",
            0x1000 => "低",
            0x2000 => "中（普通程序）",
            0x2100 => "中+",
            0x3000 => "高（已提权）",
            0x4000 => "系统",
            _ => "0x" + v.ToString("X"),
        };
    }

    private static void CheckPolicy(string subKey, string value)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                subKey.Replace(@"HKCU\", ""));
            object? v = k?.GetValue(value);
            Console.WriteLine($"    {subKey}\\{value} = {(v is null ? "未设置" : v.ToString())}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    {subKey}\\{value} = 读取失败（{ex.GetType().Name}）");
        }
    }

    private static string DescribeError(int e) => e switch
    {
        0 => "成功",
        5 => "拒绝访问",
        87 => "参数错误",
        1460 => "超时",
        _ => "见 Win32 错误码表",
    };
}
