// ScreenTime 卸载程序
//
// 为什么要专门写一个，而不是让用户手动删文件夹：
//   1. 程序本体、数据、开机自启、快捷方式散落在**四个不同的位置**，
//      只删其中任何一个，程序都会继续运行或继续自启。
//   2. 开机自启建了**两处**（计划任务 + 启动文件夹），是最容易被漏掉的。
//      只删程序不删自启，下次开机会报"找不到文件"。
//   3. 卸载程序**自己就在要被删掉的目录里**——
//      Windows 不允许删除正在运行的程序文件，必须先把自己复制到临时目录再自删。
//
// 交互设计：
//   先**扫描**并列出所有找到的痕迹，让用户看到"到底装了什么"，
//   再让用户选择是否保留使用记录（这是唯一真正有价值、删了找不回来的东西）。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class Uninstaller
{
    private const string AppFolderName = "ScreenTimeApp";
    private const string DataFolderName = "ScreenTime";
    private const string ExeName = "ScreenTime.App.exe";
    private const string DisplayName = "屏幕使用时间";
    private const string TaskName = "ScreenTimeAutoStart";
    /// <summary>卸载程序自身的文件名（也用作"这是安装目录"的标志）。</summary>
    private const string UninstallerExeName = "卸载.exe";
    /// <summary>安装程序写下的"已安装"标记文件。用来把安装目录与分发包区分开。</summary>
    private const string MarkerName = ".installed";

    /// <summary>跳过所有交互，直接卸载（保留数据）。</summary>
    private static bool _yes;
    /// <summary>保留使用记录（不删数据目录）。</summary>
    private static bool _keepData;
    /// <summary>只扫描并打印，不做任何改动。</summary>
    private static bool _dryRun;
    /// <summary>指定的安装目录（默认自动探测）。</summary>
    private static string? _dirOverride;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "卸载 " + DisplayName;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--yes" or "-y": _yes = true; break;
                case "--keep-data": _keepData = true; break;
                case "--dry-run": _dryRun = true; break;
                case "--dir" when i + 1 < args.Length: _dirOverride = args[++i]; break;
            }
        }

        Banner();

        // ---------- 1. 扫描 ----------
        var installDirs = FindInstallDirs();
        string dataDir = DataDir;
        bool dataExists = Directory.Exists(dataDir);

        var startupLinks = FindStartupShortcuts();
        var desktopLinks = FindDesktopShortcuts();
        bool hasTask = TaskExists();

        Console.WriteLine("  检测到以下内容：");
        Console.WriteLine();

        if (installDirs.Count == 0)
            Console.WriteLine("    · 程序目录        未找到");
        else
            foreach (string d in installDirs)
                Console.WriteLine($"    · 程序目录        {d}  ({CountFiles(d)} 个文件)");

        if (dataExists)
            Console.WriteLine($"    · 使用记录        {dataDir}  ({SizeText(dataDir)})");
        else
            Console.WriteLine("    · 使用记录        未找到");

        if (hasTask)
            Console.WriteLine($"    · 开机自启(计划任务) {TaskName}");
        if (startupLinks.Count > 0)
            foreach (string l in startupLinks)
                Console.WriteLine($"    · 开机自启(启动项)  {l}");
        if (!hasTask && startupLinks.Count == 0)
            Console.WriteLine("    · 开机自启        未配置");

        foreach (string l in desktopLinks)
            Console.WriteLine($"    · 桌面快捷方式      {Path.GetFileName(l)}");

        Console.WriteLine();

        if (installDirs.Count == 0 && !dataExists && !hasTask && startupLinks.Count == 0)
        {
            Console.WriteLine("  没有找到任何安装痕迹，无需卸载。");
            Console.WriteLine();
            Pause();
            return 0;
        }

        if (_dryRun)
        {
            Console.WriteLine("  （--dry-run：只扫描，未做任何改动）");
            return 0;
        }

        // ---------- 2. 是否保留使用记录 ----------
        if (dataExists && !_keepData && !_yes)
        {
            Console.WriteLine("  ┌──────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  使用记录（usage.db）里是你的历史屏幕使用数据        │");
            Console.WriteLine("  │  删掉之后就找不回来了。                               │");
            Console.WriteLine("  └──────────────────────────────────────────────────────┘");
            Console.WriteLine();
            Console.Write("  保留使用记录吗？(Y = 保留 / n = 一起删除) [Y] ");
            string? ans = null;
            try { ans = Console.ReadLine(); } catch { }
            _keepData = ans is null || !ans.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine(_keepData ? "  → 将保留使用记录" : "  → 将删除使用记录");
            Console.WriteLine();
        }

        if (!_yes)
        {
            Console.Write("  确认卸载？(Y/n) [Y] ");
            string? ok = null;
            try { ok = Console.ReadLine(); } catch { }
            if (ok is not null && ok.Trim().StartsWith("n", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  已取消。");
                Console.WriteLine();
                Pause();
                return 1;
            }
            Console.WriteLine();
        }

        // ---------- 3. 结束进程 ----------
        Step("1/5", "结束正在运行的程序");
        int killed = KillRunning();
        Console.WriteLine(killed > 0 ? $"        已结束 {killed} 个进程" : "        程序未在运行");

        // ---------- 4. 撤销开机自启 ----------
        Step("2/5", "撤销开机自启");
        bool taskGone = true;
        if (hasTask)
        {
            taskGone = DeleteTask(out string why);
            Console.WriteLine(taskGone
                ? "        已删除计划任务 ScreenTimeAutoStart"
                : "        [警告] 删除计划任务失败：" + why);
        }
        else Console.WriteLine("        计划任务本来就不存在");

        foreach (string l in startupLinks) TryDelete(l, "启动项");
        if (startupLinks.Count == 0) Console.WriteLine("        启动项本来就不存在");

        // ---------- 5. 删除快捷方式 ----------
        Step("3/5", "删除快捷方式");
        if (desktopLinks.Count == 0) Console.WriteLine("        桌面快捷方式本来就不存在");
        foreach (string l in desktopLinks) TryDelete(l, "桌面快捷方式");

        // ---------- 6. 删除数据（可选） ----------
        Step("4/5", _keepData ? "保留使用记录" : "删除使用记录");
        if (!dataExists) Console.WriteLine("        本来就没有使用记录");
        else if (_keepData)
            Console.WriteLine($"        已保留：{dataDir}");
        else
        {
            // WAL 文件可能还被占用，删不掉时不影响卸载结果，只提示
            if (TryDeleteDir(dataDir, out string why2)) Console.WriteLine("        已删除");
            else Console.WriteLine("        [警告] 未能完全删除：" + why2);
        }

        // ---------- 7. 删除程序目录（含自删） ----------
        Step("5/5", "删除程序文件");
        var failed = new List<string>();
        foreach (string d in installDirs)
        {
            if (TryDeleteDir(d, out string why3)) Console.WriteLine($"        已删除 {d}");
            else
            {
                failed.Add($"{d}（{why3}）");
                Console.WriteLine($"        [警告] 未能完全删除 {d}：{why3}");
            }
        }

        Console.WriteLine();
        if (failed.Count == 0 && taskGone)
        {
            Console.WriteLine("  ╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  卸载完成                                            ║");
            Console.WriteLine("  ╚══════════════════════════════════════════════════════╝");
        }
        else
        {
            Console.WriteLine("  卸载基本完成，但有项目未能删除：");
            foreach (string f in failed) Console.WriteLine("    · " + f);
            if (!taskGone) Console.WriteLine("    · 计划任务（可在「任务计划程序」里手动删除）");
            Console.WriteLine();
            Console.WriteLine("  提示：重启后再运行一次本程序通常就能删干净。");
        }

        if (_keepData && dataExists)
        {
            Console.WriteLine();
            Console.WriteLine($"  使用记录仍在：{dataDir}");
            Console.WriteLine("  想彻底清理的话，手动删除这个文件夹即可。");
        }

        Console.WriteLine();
        Pause();
        return failed.Count == 0 ? 0 : 2;
    }

    // ================= 探测 =================

    /// <summary>
    /// 找出程序可能被装在哪。
    ///
    /// 判据是**标志文件**（卸载程序自身），不是主程序 exe。
    /// 曾经用"目录里有 ScreenTime.App.exe"判断，结果把**分发包的 app\ 子目录**
    /// 也认成了安装目录——用户解压后直接运行里面的卸载程序，
    /// 会把整个分发包一起删掉。真正的安装目录一定有安装程序放进去的「卸载.exe」，
    /// 而 dist\（发布产物）和 app\（分发包载荷）里都没有。
    ///
    /// 查多个候选位置是因为安装程序与部署脚本用过不同的默认目录
    /// （%LOCALAPPDATA%\ScreenTimeApp 与 %USERPROFILE%\ScreenTimeApp），
    /// 老版本装在这边、新版本装那边，都要能卸干净。
    /// </summary>
    private static List<string> FindInstallDirs()
    {
        var list = new List<string>();
        var candidates = new List<string>();

        // 显式指定的目录优先（也用于自动化测试）
        if (!string.IsNullOrWhiteSpace(_dirOverride)) candidates.Add(_dirOverride!);

        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppFolderName));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName));

        // 卸载程序自己所在的目录：只有确实是"安装"过的目录才算
        string selfDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        if (!string.IsNullOrEmpty(selfDir)) candidates.Add(selfDir);

        foreach (string c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c)) continue;
            if (list.Exists(x => string.Equals(x, c, StringComparison.OrdinalIgnoreCase))) continue;
            if (LooksLikeInstallDir(c)) list.Add(c);
        }
        return list;
    }

    /// <summary>
    /// 这是不是一个"被安装过"的目录。
    ///
    /// 为什么要这么绕：**分发包解压出来的 app\ 目录和真正的安装目录长得几乎一样**
    /// （都有 ScreenTime.App.exe，现在也都有 卸载.exe）。
    /// 如果判据只是"有程序文件"，用户解压后运行 app\ 里的卸载程序，
    /// 就会把整个分发包删掉。
    ///
    /// 所以判据分两种：
    ///   · 安装程序的标准位置（%USERPROFILE% / %LOCALAPPDATA% 下的 ScreenTimeApp）——
    ///     必须是安装程序写下的标记文件，或（兼容老版本）卸载程序+主程序都在。
    ///     分发包解压出来绝不会正好叫这个名字，因此不会误判。
    ///   · 显式 --dir 指定的目录 —— 放宽为只要有主程序（自动化测试用）。
    /// </summary>
    private static bool LooksLikeInstallDir(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;

            // 显式指定：信任调用方
            if (!string.IsNullOrWhiteSpace(_dirOverride) &&
                string.Equals(Path.GetFullPath(dir), Path.GetFullPath(_dirOverride!),
                              StringComparison.OrdinalIgnoreCase))
                return File.Exists(Path.Combine(dir, ExeName));

            // 标准安装位置：优先认标记文件
            if (File.Exists(Path.Combine(dir, MarkerName))) return true;

            // 兼容没有标记文件的老版本安装：卸载程序与主程序同时存在
            return File.Exists(Path.Combine(dir, UninstallerExeName))
                   && File.Exists(Path.Combine(dir, ExeName));
        }
        catch
        {
            return false;
        }
    }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName);

    private static List<string> FindStartupShortcuts()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return FindLinks(dir);
    }

    private static List<string> FindDesktopShortcuts()
    {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return FindLinks(dir);
    }

    /// <summary>在目录里找指向本程序的快捷方式（按名字匹配，不解析 .lnk 内容）。</summary>
    private static List<string> FindLinks(string dir)
    {
        var list = new List<string>();
        try
        {
            if (!Directory.Exists(dir)) return list;
            foreach (string f in Directory.GetFiles(dir, "*.lnk"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (name.Contains(DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("ScreenTime", StringComparison.OrdinalIgnoreCase))
                    list.Add(f);
            }
        }
        catch { }
        return list;
    }

    // ================= 计划任务（COM） =================
    //
    // 刻意不用 schtasks.exe：受限环境禁止子进程使用命名管道，
    // 任何捕获子进程输出的写法都会永久挂起（项目里踩过这个坑）。
    // COM 在进程内完成，返回值直接可得。

    private static IEnumerable<string> TaskDefinitionPaths()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(root))
            root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        yield return Path.Combine(root, "System32", "Tasks", TaskName);
        yield return Path.Combine(root, "Sysnative", "Tasks", TaskName);
    }

    private static bool TaskExists()
    {
        foreach (string p in TaskDefinitionPaths())
        {
            try { if (File.Exists(p)) return true; }
            catch { }
        }
        return false;
    }

    private static bool DeleteTask(out string detail)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) { detail = "任务计划服务不可用"; return false; }

            dynamic service = Activator.CreateInstance(t)!;
            service.Connect();
            dynamic root = service.GetFolder("\\");
            root.DeleteTask(TaskName, 0);
            Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(service);
            detail = "ok";
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    // ================= 删除 =================

    private static int KillRunning()
    {
        int n = 0;
        try
        {
            foreach (Process p in Process.GetProcessesByName("ScreenTime.App"))
            {
                try { p.Kill(); n++; } catch { }
                try { p.WaitForExit(5000); } catch { }
            }
            // 给系统一点时间释放文件句柄
            if (n > 0) Thread.Sleep(1500);
            foreach (Process p in Process.GetProcessesByName("ScreenTime.App"))
            {
                try { p.Kill(); } catch { }
            }
        }
        catch { }
        return n;
    }

    private static void TryDelete(string path, string label)
    {
        try
        {
            if (File.Exists(path)) { File.Delete(path); Console.WriteLine($"        已删除{label} {Path.GetFileName(path)}"); }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        [警告] 删除{label}失败（{ex.Message}）");
        }
    }

    /// <summary>
    /// 删除目录。
    ///
    /// 关键难点：**卸载程序自己可能就在这个目录里**，
    /// Windows 不允许删除正在运行的可执行文件。
    /// 对策分三级，逐级降级：
    ///   1. 直接递归删除（卸载程序在别处时走这条，最干净）
    ///   2. 把整个目录**改名挪走**到临时目录，再从临时目录删
    ///      （改名不受"文件正在使用"限制，只要不跨卷）
    ///   3. 登记到注册表 PendingFileRenameOperations，重启时由系统删除
    /// </summary>
    private static bool TryDeleteDir(string dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return true;

        // ---- 第 1 级：直接删 ----
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception ex1) { error = ex1.Message; }

        // ---- 第 2 级：改名挪走再删 ----
        try
        {
            string graveyard = Path.Combine(Path.GetTempPath(),
                "ScreenTime-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.Move(dir, graveyard);
            try
            {
                Directory.Delete(graveyard, true);
                error = "";
                return true;
            }
            catch (Exception)
            {
                // 挪走了但删不掉：至少原位置干净了。登记重启删除。
                ScheduleDeleteOnReboot(graveyard);
                error = "";
                return true;
            }
        }
        catch (Exception ex3) { error = ex3.Message; }

        // ---- 第 3 级：登记重启后删除 ----
        if (ScheduleDeleteOnReboot(dir)) { error = "已登记为重启后删除"; return true; }
        return false;
    }

    // ================= 重启后删除 =================

    private const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>
    /// 登记"重启后删除"。
    /// 用 RunOnce 而不是 PendingFileRenameOperations：
    /// 后者需要重启前不能被别的程序覆盖写，且要处理 \\??\\ 前缀，容易出错；
    /// RunOnce 只在登录时执行一次、执行完自动清除，语义正好。
    /// </summary>
    private static bool ScheduleDeleteOnReboot(string dir)
    {
        try
        {
            string cmd = $"cmd.exe /c rd /s /q \"{dir}\"";
            using Microsoft.Win32.RegistryKey key =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunOnceKey, true);
            key.SetValue("ScreenTimeCleanup_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                         cmd, Microsoft.Win32.RegistryValueKind.String);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ================= 小工具 =================

    private static int CountFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
        catch { return -1; }
    }

    private static string SizeText(string dir)
    {
        try
        {
            long total = 0;
            foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(f).Length; } catch { }
            }
            return total >= 1024 * 1024
                ? $"{total / 1024.0 / 1024.0:F1} MB"
                : $"{total / 1024.0:F0} KB";
        }
        catch { return "未知大小"; }
    }

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("  ══════════════════════════════════════════════════════");
        Console.WriteLine($"    {DisplayName} · 卸载程序");
        Console.WriteLine("  ══════════════════════════════════════════════════════");
        Console.WriteLine();
    }

    private static void Step(string n, string text)
    {
        Console.WriteLine($"  [{n}] {text}");
    }

    private static void Pause()
    {
        Console.Write("  按任意键关闭本窗口 ... ");
        try { Console.ReadKey(true); } catch { }
        Console.WriteLine();
    }
}
