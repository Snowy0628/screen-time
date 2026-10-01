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
    /// <summary>当前安装目录名（程序装到 C:\ScreenTime）。</summary>
    private const string FolderNameNew = "ScreenTime";
    /// <summary>用过的旧目录名，卸载时要一并查找。</summary>
    private const string FolderNameOld = "ScreenTimeApp";
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

    /// <summary>
    /// 清理助手模式：等指定进程退出后删除指定目录，然后自删。
    ///
    /// 为什么需要这个模式：**Windows 不允许删除或移动正在运行的程序**，
    /// 而卸载程序自己就在要删的安装目录里。实测过三种"就地"方案全部失败：
    ///   · 直接删目录 → 被占用（UnauthorizedAccessException）
    ///   · 改整个目录的名 → 被占用（只要目录里有被占用的文件，整个目录都动不了）
    ///   · 只把自己改名移出去 → 文件动了，但**进程仍从原路径执行**，目录照样删不掉
    ///
    /// 所以采用安装器领域的标准做法（NSIS / Inno Setup 都这么做）：
    /// 把**自己的一份副本**放到临时目录，用那个副本去删安装目录。
    /// 副本不在安装目录里，删除就不会被自己挡住。
    /// </summary>
    private static int CleanupWorker(string targetDir, int parentPid)
    {
        // 先把自己的工作目录挪到临时目录根部。
        //
        // 关键：Windows 会锁住进程的当前工作目录，只要 CWD 还在某个目录里，
        // 那个目录就删不掉（实测报 "being used by another process"）。
        // 清理助手迟早要删掉自己所在的目录，所以这一步必须先做。
        try { Directory.SetCurrentDirectory(Path.GetTempPath()); }
        catch { }

        // 等父进程（真正的卸载程序）退出，它一退出，安装目录里的 exe 就释放了
        try
        {
            Process parent = Process.GetProcessById(parentPid);
            parent.WaitForExit(60000);
        }
        catch { }

        // 再稳一下，确保文件句柄彻底释放
        for (int i = 0; i < 40; i++)
        {
            if (!Directory.Exists(targetDir)) break;
            try
            {
                Directory.Delete(targetDir, true);
                break;
            }
            catch
            {
                Thread.Sleep(250);
            }
        }

        // 万一还是删不掉（比如被杀软占用），登记重启后清理
        if (Directory.Exists(targetDir)) ScheduleDeleteOnReboot(targetDir);

        Diag($"清理助手结束：{targetDir} 存在={Directory.Exists(targetDir)}");

        // 清理助手自己也要自删。
        //
        // 注意：**不能判断 self 是否为 null 就跳过**。
        // 早期版本写了 `if (self is not null) { ... }`，结果在需要清理时
        // 整个分支被跳过、临时目录留下 33 MB 的 exe 且没有任何日志。
        // 现在无论 self 是否取得到都走一遍，并把每一步记进日志。
        try
        {
            string? self = Environment.ProcessPath;
            Diag($"清理助手自删：ProcessPath={(self ?? "(null)")}");

            // 先把 exe 改名到一个只有自己的名字，这样它所在的目录就能删掉
            string alonePath = self is not null
                ? Path.Combine(Path.GetTempPath(),
                    "ScreenTime-uninstaller-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe")
                : "";

            if (self is not null)
            {
                File.Move(self, alonePath);
                Diag($"清理助手已改名：{alonePath}");
            }

            // exe 改名后，原来的临时目录就空了，直接删
            string? scratch = self is not null ? Path.GetDirectoryName(self) : null;
            if (!string.IsNullOrEmpty(scratch) && Directory.Exists(scratch))
            {
                try
                {
                    Directory.Delete(scratch, true);
                    Diag($"临时目录已删除：{scratch}");
                }
                catch (Exception ex5)
                {
                    Diag($"临时目录删除失败：{ex5.GetType().Name}: {ex5.Message}");
                    ScheduleDeleteOnReboot(scratch);
                }
            }

            // 改名后的自己仍在运行，只能登记重启后删除
            if (alonePath.Length > 0)
                ScheduleDeleteOnReboot(alonePath);
        }
        catch (Exception ex)
        {
            Diag($"清理助手自删失败：{ex.GetType().Name}: {ex.Message}");
        }

        return 0;
    }

    private static int Main(string[] args)
    {
        // 诊断用：验证 RunOnce 登记是否可用。
        // 不创建任何目录（某些受限环境禁止在临时目录建目录，
        // 探针本身不该因为这个失败）。
        if (args.Length >= 1 && args[0] == "--test-runonce")
        {
            string probe = Path.Combine(Path.GetTempPath(), "ScreenTime-probe-nonexistent");
            bool ok = ScheduleDeleteOnReboot(probe);
            Console.WriteLine($"RunOnce 登记: {(ok ? "成功" : "失败")}");
            Console.WriteLine($"目标: {probe}");
            Console.WriteLine($"日志: {Path.Combine(Path.GetTempPath(), "ScreenTime-uninstall.log")}");
            return ok ? 0 : 1;
        }

        // 清理助手模式（内部使用，不面向用户）
        if (args.Length >= 3 && args[0] == "--cleanup-worker")
        {
            int pid = 0;
            int.TryParse(args[2], out pid);
            return CleanupWorker(args[1], pid);
        }

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
        var notes = new List<string>();
        foreach (string d in installDirs)
        {
            if (TryDeleteDir(d, out string why3))
            {
                // 目录本身没了，但可能留下需重启清理的临时残留，要如实告知
                if (!string.IsNullOrEmpty(why3)) notes.Add(why3);
                Console.WriteLine($"        已删除 {d}");
                Console.WriteLine($"        目录已不存在：{(Directory.Exists(d) ? "否" : "是")}");
            }
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

        // 需要重启才能清理的残留（正在运行的卸载程序自己删不掉自己）
        foreach (string n in notes)
        {
            Console.WriteLine();
            Console.WriteLine("  说明：" + n);
        }

        if (_keepData && dataExists)
        {
            Console.WriteLine();
            Console.WriteLine($"  使用记录仍在：{dataDir}");
            Console.WriteLine("  想彻底清理的话，手动删除这个文件夹即可。");
        }

        Console.WriteLine();
        // 交互模式下先按键再退出：给清理助手留出"等父进程结束"的时间，
        // 用户看完提示再关窗口，安装目录就已经被助手删掉了。
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

        // 程序历来装过的所有位置，一个都不能漏——
        // 漏掉的后果是"卸载完了但旧的那份还在跑"。
        //   1. C:\ScreenTime        当前首选
        //   2. C:\ScreenTimeApp     短暂用过的名字
        //   3. %USERPROFILE%\...    统一前的用户目录位置
        //   4. %LOCALAPPDATA%\...   最早的位置
        foreach (string name in new[] { FolderNameNew, FolderNameOld })
        {
            candidates.Add(@"C:\" + name);
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), name));
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), name));
        }

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
    /// 关键难点：**卸载程序自己就在这个目录里**，
    /// Windows 不允许删除或改名正在运行的可执行文件。
    ///
    /// 所以先做「两阶段删除」：把**除自己以外**的东西全删掉，
    /// 这一步一定能成功（那些文件没被占用）。此时目录里只剩卸载程序自己。
    /// 接着分三级处理这个"只剩下自己"的目录：
    ///   1. 直接删（卸载程序不在这个目录里时走这条，最干净）
    ///   2. 改名挪到临时目录 —— 整体 rename 不受"文件被占用"限制
    ///      （只要不跨卷）。挪完安装目录立刻就不存在了。
    ///      留在临时目录的那份登记重启后删除。
    ///   3. 登记 RunOnce，重启后由系统删除
    ///
    /// 早期版本在这里犯过错：挪走之后删临时副本失败，却仍然返回 true，
    /// 结果用户看到"卸载完成"，但安装目录（或临时目录）里还留着 34 MB 的卸载程序。
    /// 现在**如实返回是否还有残留**，并把残留位置告诉用户。
    /// </summary>
    private static bool TryDeleteDir(string dir, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return true;

        // ---- 阶段 1：先删掉除自己以外的所有内容 ----
        string? selfPath = null;
        try
        {
            string self = Environment.ProcessPath ?? "";
            if (!string.IsNullOrEmpty(self) &&
                self.StartsWith(dir.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                selfPath = self;
        }
        catch { }

        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                try { Directory.Delete(sub, true); } catch { /* 留到下一阶段 */ }
            }
            foreach (string file in Directory.GetFiles(dir))
            {
                if (selfPath is not null &&
                    string.Equals(file, selfPath, StringComparison.OrdinalIgnoreCase))
                    continue;   // 跳过自己，删不掉
                try { File.Delete(file); } catch { /* 留到下一阶段 */ }
            }
        }
        catch { }

        // 目录已空（说明自己不在这个目录里），直接删掉即可
        try
        {
            Directory.Delete(dir, true);
            return true;
        }
        catch (Exception ex1)
        {
            error = ex1.Message;
            Diag($"阶段2 直接删除失败：{ex1.GetType().Name}: {ex1.Message}");
        }

        // ---- 阶段 3：自己就是删不掉的那个 → 交给"清理助手"副本 ----
        //
        // 实测结论：只要卸载程序自己还在安装目录里，就**没有任何就地办法**
        // 能删掉这个目录——删目录被占用、改目录名被占用、
        // 把自己的 exe 改名后进程仍从原路径执行，照样被占用。
        //
        // 唯一可行的路：把副本放到临时目录，用它去删安装目录，自己立即退出。
        try
        {
            string? self = Environment.ProcessPath;
            if (self is not null)
            {
                string scratchDir = Path.Combine(Path.GetTempPath(),
                    "ScreenTime-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                Directory.CreateDirectory(scratchDir);
                string helper = Path.Combine(scratchDir, Path.GetFileName(self));
                File.Copy(self, helper, true);

                var psi = new ProcessStartInfo(helper)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // 工作目录**不能**设成 scratchDir：Windows 会锁住进程的当前目录，
                    // 导致清理助手随后删不掉自己所在的这个目录
                    // （实测报 "being used by another process"）。
                    // 设成临时目录根部，两边互不占用。
                    WorkingDirectory = Path.GetTempPath(),
                };
                psi.ArgumentList.Add("--cleanup-worker");
                psi.ArgumentList.Add(dir);
                psi.ArgumentList.Add(Environment.ProcessId.ToString());

                Process.Start(psi);
                Diag($"阶段3 已启动清理助手：{helper} 目标={dir}");

                error = "安装目录将在本窗口关闭后由清理助手删除";
                return true;
            }
        }
        catch (Exception ex3)
        {
            Diag($"阶段3 启动清理助手失败：{ex3.GetType().Name}: {ex3.Message}");
            error = ex3.Message;
        }

        // ---- 阶段 4：兜底，登记重启后删除 ----
        if (ScheduleDeleteOnReboot(dir))
        {
            Diag($"阶段4 登记 RunOnce：{dir}");
            error = "安装目录中的程序文件已删除；卸载程序自身被系统占用（正在运行），" +
                    "无法立即删除，已登记为下次登录时自动清理：" + dir;
            return true;
        }
        return false;
    }

    // ================= 重启后删除 =================

    /// <summary>
    /// 诊断日志。卸载过程一旦出问题，用户很难描述清楚现象，
    /// 所以把关键步骤的成败与异常原文写到文件里，
    /// 便于事后定位（写到临时目录，因为安装目录可能已经不存在了）。
    /// </summary>
    private static void Diag(string message)
    {
        try
        {
            string path = Path.Combine(Path.GetTempPath(), "ScreenTime-uninstall.log");
            File.AppendAllText(path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch { }
    }

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
            using Microsoft.Win32.RegistryKey? key =
                Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunOnceKey, true);
            if (key is null)
            {
                Diag($"登记 RunOnce 失败：CreateSubKey 返回 null（{RunOnceKey}）");
                return false;
            }
            key.SetValue("ScreenTimeCleanup_" + Guid.NewGuid().ToString("N").Substring(0, 6),
                         cmd, Microsoft.Win32.RegistryValueKind.String);
            Diag($"登记 RunOnce 成功：{cmd}");
            return true;
        }
        catch (Exception ex)
        {
            Diag($"登记 RunOnce 异常：{ex.GetType().Name}: {ex.Message}");
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
