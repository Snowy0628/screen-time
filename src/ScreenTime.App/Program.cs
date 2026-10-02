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
    /// 请求已运行实例退出，以便本进程接管（"再次双击快捷方式"的唯一路径）。
    ///
    /// 机制：在数据目录写一个接管请求文件，老实例在自己的定时循环里发现它就会
    /// 收尾退出。用文件是因为它不依赖任何进程间通信能力，在受限会话里同样可用。
    ///
    /// **为什么不"跨进程把老窗口显示出来"**：那条路试过两种写法都出问题——
    /// `SW_RESTORE` 会让界面全黑，换成 `SW_SHOW` 后新进程又可能与老进程互相干扰、
    /// 最终两个都没了。跨进程操作别人的 WPF 窗口状态本身就不可靠；
    /// 而"老实例退出 + 新实例全新窗口"每次都能得到可正常渲染的界面。
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
                        // 用户很难自己判断"我这个窗口到底算不算全屏/最大化"，
                        // 而这个判定直接决定时间轴里那段是"前台使用"还是"空闲"。
                        // 有了这个命令，遇到"明明是看视频却被记成空闲"时
                        // 可以直接看出来是哪一环没匹配上。
                        ConsoleHelper.Ensure();
                        Console.WriteLine("=== 空闲判定诊断 ===");
                        try
                        {
                            var probe = new ScreenTime.Core.IdleWatcher
                            {
                                IdleThresholdSeconds = AppSettings.Current.IdleThresholdSeconds,
                                TreatFullscreenAsActive = AppSettings.Current.FullscreenCountsAsActive,
                            };
                            Console.WriteLine($"  空闲阈值        : {probe.IdleThresholdSeconds} 秒");
                            Console.WriteLine($"  全屏算作活跃    : {(probe.TreatFullscreenAsActive ? "是" : "否")}");
                            Console.WriteLine($"  距上次键鼠输入  : {probe.IdleSeconds()} 秒");
                            Console.WriteLine();
                            foreach (string line in ScreenTime.Core.IdleWatcher.DescribeForeground())
                                Console.WriteLine("  " + line);
                            Console.WriteLine();
                            Console.WriteLine($"  是否判为空闲    : {(probe.IsIdle() ? "是（这段会记为空闲）" : "否（这段记为前台使用）")}");
                            Console.WriteLine();
                            Console.WriteLine("  提示：把一个窗口最大化或全屏，再运行一次本命令，");
                            Console.WriteLine("        可以看到「覆盖工作区」是否变成 True。");
                            return 0;
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
            Boot("检测到已有实例，请求它退出以便本进程接管");
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
