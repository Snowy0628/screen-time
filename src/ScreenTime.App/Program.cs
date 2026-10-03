using System.Threading;
using System.Windows.Forms;
using ScreenTime.Core;

namespace ScreenTime.App;

internal static class Program
{
    private const string MutexName = @"Global\ScreenTime.SingleInstance.v1";

    /// <summary>
    /// 初始化 WinForms 运行时，并对失败做降级。
    ///
    /// 为什么不能直接调用 ApplicationConfiguration.Initialize()：
    /// 它内部的 Application.EnableVisualStyles() 会往 %TEMP% 写一个临时 manifest 文件
    /// （ThemingScope.CreateActivationContext），在临时目录不可写的受限环境下会抛
    /// UnauthorizedAccessException 并让进程直接崩溃——连日志都来不及写。
    /// 视觉样式只是外观，绝不能因为它拿不到就整个程序起不来。
    /// </summary>
    private static void SafeInitialize()
    {
        try
        {
            ApplicationConfiguration.Initialize();
            return;
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"[警告] 视觉样式初始化失败，尝试降级: {ex.GetType().Name}: {ex.Message}");
            }
            catch
            {
                // 无控制台时忽略
            }
        }

        // 降级：只设置 DPI 模式，放弃视觉样式
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch (Exception ex)
        {
            try
            {
                Console.Error.WriteLine($"[警告] DPI 模式设置失败，使用默认值: {ex.Message}");
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>存活探针：记录启动进度，用于定位"进程静默消失"的崩溃点。</summary>
    private static void Boot(string message) => BootProbe.Write(message);

    /// <summary>
    /// 启动存活探针。默认关闭，设置环境变量 SCREENTIME_BOOT_PROBE=1 后启用，
    /// 输出到 SCREENTIME_PROBE_DIR（默认 %TEMP%）下的 screentime-boot.log。
    /// </summary>
    private static class BootProbe
    {
        private static readonly bool Enabled =
            Environment.GetEnvironmentVariable("SCREENTIME_BOOT_PROBE") is "1" or "true";

        public static void Write(string message)
        {
            if (!Enabled) return;
            try
            {
                string dir = Environment.GetEnvironmentVariable("SCREENTIME_PROBE_DIR") ?? Path.GetTempPath();
                File.AppendAllText(Path.Combine(dir, "screentime-boot.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n",
                    System.Text.Encoding.UTF8);
            }
            catch
            {
                // 探针失败不影响主流程
            }
        }
    }

    /// <summary>已经弹过的异常标识，用于避免同类异常反复弹窗。</summary>
    private static readonly HashSet<string> ReportedFatal = new();

    /// <summary>
    /// 唤醒已运行实例的主窗口。
    ///
    /// 机制：用 RegisterWindowMessage 注册的那个消息名找到老实例的消息窗口，
    /// 给它 PostMessage。老实例的 TaskbarWatcherWindow 收到后调 ShowWindow()——
    /// 那是**它自己进程内的 WPF 调用**，不是跨进程操作别人的窗口，所以可靠。
    ///
    /// **为什么要这条路**：此前的做法是"接管"——请求老实例退出、本进程接手。
    /// 但老实例退出和新实例接管之间有真空期，托盘图标会消失几秒。
    /// 用户看到的现象就是"开机自启成功后，过了几分钟程序自己没了"——
    /// 其实是他又启动了一次，把正在安静记录的实例顶掉了，还留下数据空档。
    ///
    /// 找不到老窗口时返回 false，调用方回退到接管方式（保证功能不会因为
    /// 这条新路出问题而彻底失效）。
    /// </summary>
    private static bool TryWakeExistingInstance()
    {
        try
        {
            uint msg = RegisterWindowMessage(WakeUpMessage);
            if (msg == 0) return false;

            foreach (System.Diagnostics.Process p in
                     System.Diagnostics.Process.GetProcessesByName("ScreenTime.App"))
            {
                if (p.Id == Environment.ProcessId) continue;

                // 发给该进程的**所有**顶层窗口，而不是只挑一个。
                //
                // 为什么这样更稳：老实例至少有两个顶层窗口（WPF 主窗口 +
                // TaskbarWatcherWindow 消息窗口），而 EnumWindows 的返回顺序是
                // Z 序、不保证哪个在前。早先只取第一个，结果拿到的是主窗口——
                // 主窗口虽然也处理这条消息，但实测没反应，窗口始终没出来。
                // 注册消息在系统范围内是唯一的（RegisterWindowMessage 对同一
                // 字符串永远返回同一个 id），所以只有本程序的消息窗口会处理它，
                // 发给多余窗口没有任何副作用。
                var targets = FindTopLevelWindows(p.Id);
                if (targets.Count == 0) continue;

                int sent = 0;
                foreach (IntPtr hwnd in targets)
                {
                    if (PostMessage(hwnd, msg, IntPtr.Zero, IntPtr.Zero)) sent++;
                }

                if (sent > 0)
                {
                    Boot($"已向 PID={p.Id} 的 {sent}/{targets.Count} 个顶层窗口发送唤醒消息");
                    return true;
                }
            }

            Boot("没找到可唤醒的实例窗口");
            return false;
        }
        catch (Exception ex)
        {
            Boot($"唤醒已有实例失败: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>枚举指定进程的全部顶层窗口句柄。</summary>
    private static System.Collections.Generic.List<IntPtr> FindTopLevelWindows(int pid)
    {
        var list = new System.Collections.Generic.List<IntPtr>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                GetWindowThreadProcessId(hwnd, out uint wpid);
                if (wpid == (uint)pid) list.Add(hwnd);
                return true;      // 继续枚举，收集全部
            }, IntPtr.Zero);
        }
        catch
        {
            // 枚举失败当作没找到
        }
        return list;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 请求已运行实例退出，以便本进程接管（"再次双击快捷方式"的兜底路径）。
    ///
    /// 机制：在数据目录写一个接管请求文件，老实例在自己的定时循环里发现它就会
    /// 收尾退出。用文件是因为它不依赖任何进程间通信能力，在受限会话里同样可用。
    ///
    /// 这条路径现在只在 <see cref="TryWakeExistingInstance"/> 失败时才走：
    /// 唤醒是首选（不打断采集、托盘图标不消失），接管是兜底。
    ///
    /// **为什么不直接跨进程把老窗口显示出来**：那条路试过两种写法都出问题——
    /// `SW_RESTORE` 会让界面全黑，换成 `SW_SHOW` 后新进程又可能与老进程互相干扰、
    /// 最终两个都没了。跨进程操作别人的 WPF 窗口状态本身就不可靠；
    /// 所以改成"请求对端自己 ShowWindow"，见 TryWakeExistingInstance。
    /// </summary>
    private static bool TryTakeOverFromExistingInstance()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(AppPaths.TakeoverRequestPath, DateTime.UtcNow.Ticks.ToString());
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 尝试成为唯一实例。成功返回持有的互斥体，失败（已有实例）返回 null。
    ///
    /// 为什么不能直接用 `new Mutex(true, name, out createdNew)`：
    /// 那个构造函数的 initiallyOwned 参数会让**本进程无论如何都持有互斥体**，
    /// 因此即使 createdNew 为 false 也没法用它来判断"我是不是第一个"。
    /// 这里改为：先创建一个不自持有的互斥体，再用 WaitOne(0) 尝试立即获取；
    /// 拿到说明没有别人持有，拿不到说明已有实例在运行。
    /// </summary>
    private static Mutex? TryAcquireInstanceMutex()
    {
        try
        {
            var m = new Mutex(false, MutexName);
            try
            {
                if (m.WaitOne(0)) return m;     // 立即拿到 → 我是唯一实例
            }
            catch (AbandonedMutexException)
            {
                // 上一个持有者异常退出：内核把锁移交给我们，同样算获取成功
                return m;
            }
            m.Dispose();
            return null;
        }
        catch
        {
            // 互斥体本身出错（极少见）：放行，避免程序完全起不来
            return new Mutex(false);
        }
    }

    /// <summary>当前存活的 ScreenTime.App 进程数。</summary>
    private static int GetProcessCount()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("ScreenTime.App").Length;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>唤醒消息名。已运行实例的监听窗口会收到它并显示主窗口。</summary>
    internal const string WakeUpMessage = "ScreenTime.WakeUpWindow.v1";

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string lpString);

    /// <summary>
    /// 记录致命异常并提示用户。托盘程序没有控制台，不落盘就等于"无声消失"。
    /// 同时把堆栈写到独立的 crash 文件，避免被日志滚动覆盖。
    ///
    /// 重要：对话框**最多弹 3 次**。曾经因为 WPF 每个布局周期都抛异常，
    /// 弹窗无限累积把屏幕铺满（一次踩过的坑），必须限流。
    /// </summary>
    private static void ReportFatal(Log? log, string source, Exception? ex)
    {
        string detail = ex?.ToString() ?? "(无异常对象)";
        string key = source + "|" + (ex?.GetType().FullName ?? "") + "|" + (ex?.Message ?? "");

        try
        {
            log?.Error($"【致命】{source}: {detail}");
            string crashPath = Path.Combine(AppPaths.DataDirectory, "crash.log");
            File.AppendAllText(crashPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 【{source}】\n{detail}\n\n",
                System.Text.Encoding.UTF8);
        }
        catch
        {
            // 连日志都写不了时忽略
        }

        bool shouldShow;
        lock (ReportedFatal)
        {
            shouldShow = ReportedFatal.Count < 3 && ReportedFatal.Add(key);
        }
        if (!shouldShow) return;

        try
        {
            MessageBox.Show($"{source}\n\n{ex?.Message}\n\n详细信息已写入 crash.log",
                "屏幕使用时间", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch
        {
            // 忽略
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // 启动存活探针：仅在设置了 SCREENTIME_BOOT_PROBE 时启用。
        // 用途是定位"进程起来了但还没写日志就消失"这类崩溃——
        // 托盘程序没有控制台，没有这个探针就只能靠猜。
        BootProbe.Write($"Main 进入, args=[{string.Join(", ", args)}]");
        // ---- 最先解析 --datadir，它影响后续所有路径 ----
        bool startMinimized = false;
        bool openSettings = false;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--datadir" or "-d")
            {
                AppPaths.OverrideDirectory = args[i + 1];
                break;
            }
        }
        foreach (string a in args)
        {
            if (a is "--minimized" or "--tray" or "-m") startMinimized = true;
            if (a is "--opensettings") openSettings = true;
        }

        // ---- 命令行 ----
        if (args.Length > 0)
        {
            switch (args[0].ToLowerInvariant())
            {
                case "--selftest":
                case "-s":
                    {
                        int seconds = 20;
                        int idleThreshold = 60;
                        string? dbPath = null;

                        for (int i = 1; i < args.Length; i++)
                        {
                            if (args[i] is "--idle" && i + 1 < args.Length && int.TryParse(args[i + 1], out int th))
                            {
                                idleThreshold = th;
                                i++;
                            }
                            else if (int.TryParse(args[i], out int sec) && sec > 0)
                            {
                                seconds = sec;
                            }
                            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
                            {
                                dbPath = args[i];
                            }
                        }

                        ConsoleHelper.Ensure();
                        dbPath ??= Path.Combine(AppPaths.DataDirectory, "selftest.db");                        try
                        {
                            return SelfTest.Run(seconds, dbPath, idleThreshold);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--daytest":
                    {
                        // 校验单日视图的数据换算：时间轴各段秒数之和必须等于一整天。
                        // 这个检查能抓住"时间轴被片段拉满"这类只有看图才发现的错误。
                        ConsoleHelper.Ensure();
                        string db = args.Length > 1 ? args[1] : AppPaths.DatabasePath;
                        try
                        {
                            return DayTest.Run(db);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--idlecheck":
                    {
                        // 空闲判定诊断：打印当前为什么被判为「活跃」或「空闲」。
                        //
                        // 判定涉及四路信号（键鼠 / 手柄 / 前台媒体播放 / 真全屏几何），
                        // 用户很难自己判断是哪一环没匹配上。这个命令把每一路的
                        // 原始读数和结论都列出来，遇到"明明是看视频却被记成空闲"
                        // 或者"窗口挂着不动却算在使用"之类的问题可以直接定位。
                        ConsoleHelper.Ensure();
                        Console.WriteLine("=== 空闲判定诊断 ===");
                        try
                        {
                            var settings = AppSettings.Current;
                            using var media = new ScreenTime.Core.MediaWatcher();
                            using var gamepad = new ScreenTime.Core.GamepadWatcher();

                            var probe = new ScreenTime.Core.IdleWatcher
                            {
                                IdleThresholdSeconds = settings.IdleThresholdSeconds,
                                TreatFullscreenAsActive = settings.FullscreenCountsAsActive,
                                Media = media,
                                Gamepad = gamepad,
                            };

                            Console.WriteLine("── 判据来源 ──");
                            foreach (string line in probe.DescribeInputs())
                                Console.WriteLine("  " + line);

                            Console.WriteLine();
                            Console.WriteLine("── 前台窗口几何 ──");
                            foreach (string line in ScreenTime.Core.IdleWatcher.DescribeForeground())
                                Console.WriteLine("  " + line);

                            // 媒体观察器与手柄观察器都跑在后台线程上，
                            // 要给它俩一点时间出第一个采样结果，否则读到的
                            // 永远是初始值（"没在放"），看起来像功能没生效。
                            Console.WriteLine();
                            Console.WriteLine("  等待 4 秒让后台观察器出首个采样…");
                            System.Threading.Thread.Sleep(4000);

                            Console.WriteLine();
                            Console.WriteLine("── 采样后结论 ──");
                            foreach (string line in probe.DescribeInputs())
                                Console.WriteLine("  " + line);
                            Console.WriteLine();
                            Console.WriteLine($"  最终是否判为空闲 : {(probe.IsIdle() ? "是（这段会记为空闲）" : "否（这段记为前台使用）")}");
                            return 0;
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--mediacheck":
                    {
                        // 媒体检测诊断：把 SMTC 的原始会话数据、名称匹配结果
                        // 和最终判定全部打出来。
                        //
                        // 为什么要单独一条命令：媒体判定的核心是"会话的
                        // AppUserModelId 能不能和前台进程名对上"，而这个映射
                        // 各程序写法不同（PotPlayer 给 "PotPlayerMini64.exe"，
                        // Edge 给 "MSEdge" 而进程名是 msedge）。出问题时
                        // 必须能看到原始字符串才能判断是匹配规则不够宽，
                        // 还是播放器根本没上报。
                        ConsoleHelper.Ensure();
                        Console.WriteLine("=== 媒体播放检测诊断 ===");
                        try
                        {
                            return MediaCheck.Run();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--catest":
                    {
                        // 应用分类功能自检：手动设定优先级、按路径匹配、
                        // 删除分类的连带清理、饼图占比之和。
                        // 这些都是"错了界面也不报错"的逻辑，必须单独守住。
                        ConsoleHelper.Ensure();
                        try
                        {
                            return CategoryTest.Run();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--rangetest":
                    {
                        // 周/月视图自检。这两个视图曾因一个自递归方法抛
                        // StackOverflowException 而整个进程终止，必须单独守住。
                        ConsoleHelper.Ensure();
                        string db = args.Length > 1 ? args[1] : AppPaths.DatabasePath;
                        try
                        {
                            return RangeTest.Run(db);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--autostart":
                    {
                        // 命令行管理开机自启（设置面板之外的另一条路，便于诊断）
                        //   --autostart        查看状态
                        //   --autostart on     启用
                        //   --autostart off    关闭
                        ConsoleHelper.Ensure();
                        string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";
                        Console.WriteLine("=== 开机自启 ===");
                        Console.WriteLine($"当前状态: {(AutoStart.IsEnabled() ? "已启用" : "未启用")}");

                        if (sub is "on" or "enable")
                        {
                            (bool ok, string method, string detail) = AutoStart.Enable(
                                Environment.ProcessPath ?? "");
                            Console.WriteLine(ok
                                ? $"✓ 已启用（方式：{method}）——{detail}"
                                : $"✗ 启用失败：{detail}");

                            // **必须同时把设置里的标志置位**。
                            //
                            // 曾经漏了这一步：命令行只建了任务/快捷方式，却没写标志，
                            // 于是"勾了自启"这个状态在设置里永远是 false，
                            // 依赖该标志的自愈逻辑（TrayContext.ApplyRuntimeSettings）
                            // 永远不会执行——任务一旦丢失就再也没人补回来。
                            // 这正是用户反复遇到"勾了自启、重启却没启动"的根因之一。
                            if (ok)
                            {
                                AppSettings.Current.AutoStart = true;
                                AppSettings.Current.Save();
                                Console.WriteLine("  已记入设置（下次启动会检查并自动修复）");
                            }
                            return ok ? 0 : 1;
                        }
                        if (sub is "off" or "disable")
                        {
                            (bool ok, string detail) = AutoStart.Disable();
                            Console.WriteLine(ok ? $"✓ 已关闭——{detail}" : $"✗ 关闭失败：{detail}");
                            if (ok)
                            {
                                AppSettings.Current.AutoStart = false;
                                AppSettings.Current.Save();
                            }
                            return ok ? 0 : 1;
                        }

                        Console.WriteLine("用法: --autostart [on|off]");
                        return 0;
                    }

                case "--storetest":
                    {
                        ConsoleHelper.Ensure();
                        string db = args.Length > 1 ? args[1] : Path.Combine(AppPaths.DataDirectory, "storetest.db");
                        try
                        {
                            return StoreTest.Run(db);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[异常] {ex}");
                            return 2;
                        }
                    }

                case "--help":
                case "-h":
                    ConsoleHelper.Ensure();
                    Console.WriteLine("屏幕使用时间 M0");
                    Console.WriteLine("  ScreenTime.App                  启动托盘常驻采集");
                    Console.WriteLine("  ScreenTime.App --selftest [秒] [数据库路径] [--idle 秒]");
                    Console.WriteLine("                                  采集链路自检（默认 20 秒、空闲阈值 60 秒）");
                    Console.WriteLine("  ScreenTime.App --storetest [数据库路径]");
                    Console.WriteLine("                                  存储层尾行续接定向测试");
                    Console.WriteLine();
                    Console.WriteLine("通用选项：");
                    Console.WriteLine("  --datadir <目录>                指定数据目录（默认 %LOCALAPPDATA%\\ScreenTime）");
                    Console.WriteLine("  也可用环境变量 SCREENTIME_DATA_DIR");
                    return 0;

                case "--yes-really-run":
                    // 供脚本明确表示"就是要常驻运行"，避免与 --help 混淆
                    break;
            }
        }

        // ---- 单实例 ----
        //
        // 注意 Mutex 的语义：`new Mutex(true, name, out createdNew)` 无论是否新建，
        // **本进程都持有它**（initiallyOwned = true）。因此不能用它自带的实例去
        // WaitOne——那是等自己已经持有的锁，会立即返回，等于没起作用。
        // 正确做法是：先判断，若已有实例则请求它退出，之后**重新创建一个互斥体**
        // 来确认自己是否已成为唯一实例。
        //
        // 为什么不做"跨进程把老窗口显示出来"：那条路走过两次都出问题——
        // 用 SW_RESTORE 会让界面全黑，改用 SW_SHOW 后新进程又可能与老进程互相干扰、
        // 最终两个都没了。**跨进程操作别人的 WPF 窗口状态本身就不可靠**，
        // 而"让老实例退出、新实例用全新窗口启动"每次都能得到可正常渲染的界面。
        Mutex? instanceMutex = TryAcquireInstanceMutex();
        if (instanceMutex is null)
        {
            Boot("检测到已有实例");

            // 先试"唤醒"：让已运行的实例把窗口显示出来，本进程随即退出。
            // 这条路不打断采集，用户也不会看到托盘图标消失。
            bool woke = TryWakeExistingInstance();
            if (woke)
            {
                Boot("已唤醒已有实例的窗口，本进程退出（不接管）");
                // 稍等一下再退出：PostMessage 是异步投递，进程立刻结束虽然
                // 不影响消息送达，但留一点时间更稳妥。
                System.Threading.Thread.Sleep(300);
                return 0;
            }

            // 唤醒失败（找不到窗口、或对端异常）才回退到旧的"接管"方式：
            // 请求老实例退出，本进程接手。代价是中间会有几秒没有托盘图标。
            Boot("唤醒失败，回退到接管方式");
            TryTakeOverFromExistingInstance();

            // 等待老实例退出并释放单实例锁
            for (int i = 0; i < 60 && instanceMutex is null; i++)
            {
                System.Threading.Thread.Sleep(250);
                instanceMutex = TryAcquireInstanceMutex();
            }
            Boot(instanceMutex is null ? "接管失败：老实例未退出" : "接管成功：已取得单实例锁");
        }

        if (instanceMutex is null)
        {
            // 老实例还活着。它仍在正常记录，不该弹窗打断用户——写日志即可。
            Boot("接管失败，本实例退出（老实例仍在运行）");
            return 0;
        }

        // 无论哪条路径，拿到锁后都按正常流程启动：
        // 新进程 = 新窗口 = 全新的渲染目标，界面必然正常。
        using Mutex _instanceLock = instanceMutex;

        // 必须在任何控件创建之前设置异常模式。
        // SafeInitialize() 内部的 EnableVisualStyles 会创建内部控件，
        // 之后再调用会抛 InvalidOperationException:
        //   "Thread exception mode cannot be changed once any Controls are created on the thread."
        try
        {
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        }
        catch (Exception ex)
        {
            // 失败不影响采集，只是未处理异常会走系统默认行为
            Boot($"SetUnhandledExceptionMode 失败（可忽略）: {ex.GetType().Name}");
        }

        SafeInitialize();

        Log? log = null;
        UsageStore? store = null;
        Recorder? recorder = null;
        try
        {
            Boot($"进入主 try 块");
            Directory.CreateDirectory(AppPaths.DataDirectory);
            Boot($"数据目录已就绪: {AppPaths.DataDirectory}");
            log = new Log(AppPaths.LogPath);
            Boot($"日志已打开: {AppPaths.LogPath}");
            log.Info($"===== 启动 ScreenTime {typeof(Program).Assembly.GetName().Version} =====");

            // 记录数据目录的解析过程，便于事后核对"为什么数据存在这里"
            foreach (string line in AppPaths.ResolutionLog) log.Info($"数据目录解析: {line}");
            log.Info($"数据目录: {AppPaths.DataDirectory}{(AppPaths.UsedFallback ? "（降级）" : "")}");
            log.Info($"关闭窗口行为: {AppSettings.Current.CloseAction}（0=询问 1=最小化到托盘 2=彻底退出）");

            store = new UsageStore(AppPaths.DatabasePath);
            recorder = new Recorder(store);

            // 载入用户手动设定的应用分类。
            // 必须在这里（而不是各 ViewModel 内部）做一次：CategoryOf 是静态方法，
            // 排行、时间轴、分类面板都会调它，得先有缓存。
            AppPalette.ReloadManualCategories(store);

            // WPF 应用对象承载消息循环。
            // 必须先创建它：Application.Current 在此之前是 null，
            // 直接订阅 DispatcherUnhandledException 会抛 NullReferenceException。
            var app = new App();
            app.InitializeComponent();

            // 全局异常兜底：界面异常不能让采集悄悄中断，必须把堆栈落盘。
            System.Windows.Forms.Application.ThreadException += (_, e2) =>
                ReportFatal(log, "WinForms 未处理异常", e2.Exception);
            app.DispatcherUnhandledException += (_, e2) =>
            {
                ReportFatal(log, "WPF UI 线程未处理异常", e2.Exception);
                e2.Handled = true;   // 界面异常不应终止进程，采集要继续
            };
            AppDomain.CurrentDomain.UnhandledException += (_, e2) =>
                ReportFatal(log, "非 UI 线程未处理异常", e2.ExceptionObject as Exception);
            TaskScheduler.UnobservedTaskException += (_, e2) =>
                ReportFatal(log, "未观察的任务异常", e2.Exception);

            MainWindow? window = null;
            using var context = new TrayContext(store, recorder, log, () => app.Shutdown());

            // 启动痕迹：每次启动往数据目录追加一行。
            //
            // 用途：排查"开机自启到底有没有被执行"。用户报告"重启后没有自动启动"时，
            // 只看进程列表无法区分两种情况：
            //   1. 计划任务/启动项根本没被触发
            //   2. 触发了，但程序立刻退出（崩溃、单实例互斥、权限问题）
            // 只要这个文件里有一行时间是本次开机时间，就说明任务确实跑过。
            try
            {
                string bootMark = System.IO.Path.Combine(
                    System.IO.Path.GetDirectoryName(log.LogPath) ?? ".", "boot-history.log");
                System.IO.File.AppendAllText(bootMark,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  pid={Environment.ProcessId}  " +
                    $"args=[{string.Join(' ', args)}]  " +
                    $"开机已过={Environment.TickCount64 / 1000}s{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
            catch { }

            // 托盘不可用时（系统拒绝注册），必须保留可见窗口作为唯一入口，
            // 否则用户把窗口关掉/收进托盘后就彻底找不回界面了。
            if (context.TrayUnavailable) startMinimized = false;

            // 启动时应用一次设置里的空闲阈值与落库间隔
            context.ApplyRuntimeSettings();

            // **窗口对象必须始终创建并挂接给 TrayContext，即使静默启动。**
            //
            // 曾经写成"静默模式不创建窗口"，结果是自启动（--minimized）进来的进程里
            // _window / _mainWindow 都是 null：双击托盘图标时 ShowWindow() 直接
            // `if (_window is null) return;` 静默返回，什么都不发生、连日志都不写，
            // 用户看到的就是"双击没反应"。
            // 正确做法：对象建好并挂接，静默模式只是**不调用 Show()**。
            window = new MainWindow(context, log);
            context.AttachWindow(window);

            if (!startMinimized)
            {
                window.Show();
                log.Info("已打开主界面");

                // 诊断用：启动即展开设置面板，便于截图确认渲染
                if (openSettings) window.OpenSettingsForDiagnostics();
            }
            else
            {
                // 静默模式：只驻留托盘，不显示窗口。
                // 双击托盘 / 菜单"打开主界面"时再 Show()。
                log.Info("以静默模式启动（窗口对象已就绪，但未显示）");
            }

            app.MainWindow = window;
            app.Run();

            log.Info("===== 正常退出 =====");
            return 0;
        }
        catch (Exception ex)
        {
            // 关键：把完整堆栈落盘，并且**告诉用户具体路径**——
            // 之前只写"已写入 crash 日志"，结果连我自己都找不到文件。
            string? crashPath = null;
            try
            {
                log?.Error($"【启动失败】{ex}");
                // 注意用 Path.Combine：早前写成 DataDirectory + ".crash.log"，
                // 会生成与目录同级的怪异文件名（ScreenTime.crash.log），极易找不到。
                crashPath = Path.Combine(AppPaths.DataDirectory, "crash.log");
                File.AppendAllText(crashPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 【启动失败】\n{ex}\n\n",
                    System.Text.Encoding.UTF8);
            }
            catch
            {
                crashPath = null;
            }

            string detail =
                $"类型：{ex.GetType().FullName}\n" +
                $"消息：{ex.Message}\n\n" +
                $"堆栈：\n{ex.StackTrace}\n\n" +
                (crashPath is not null ? $"完整信息已写入：\n{crashPath}" : "（崩溃日志写入失败）");

            try
            {
                MessageBox.Show(detail, "屏幕使用时间 · 启动失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch
            {
                // 忽略
            }
            return 1;
        }
        finally
        {
            recorder?.Dispose();
            store?.Dispose();
            log?.Dispose();
        }
    }
}
