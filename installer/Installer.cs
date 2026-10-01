// ScreenTime 安装程序
//
// 为什么需要它而不是简单复制文件：
//   1. 从压缩包解压出来的文件可能带「低完整性」标签（Mandatory Label\Low），
//      而从带该标签的文件启动的进程会被 Windows 降为低完整性，
//      导致托盘图标注册被拒（Shell_NotifyIcon 返回"拒绝访问"）、
//      也无法写入 %LOCALAPPDATA%。
//      所以安装时必须显式把整个安装目录设回中完整性。
//   2. 需要正确创建工作目录之外的快捷方式与开机自启项。
//
// 安装位置默认选在 C:\ScreenTime（候选与历史位置见下方 InstallDirCandidates）：
//   这是用户自己的目录，无需管理员权限，且没有低完整性标签。
//   两者都可行，实际沿用已存在的那个，避免升级时装成两份。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Installer
{
    /// <summary>安装目录的文件夹名（默认装到 C:\ScreenTime）。</summary>
    private const string AppFolderName = "ScreenTime";
    private const string ExeName = "ScreenTime.App.exe";
    private const string DisplayName = "屏幕使用时间";
    private const string UninstallerName = "卸载.exe";

    /// <summary>
    /// 实际安装目录。**惰性求值**，不要在字段初始化器里直接调 DefaultInstallDir。
    ///
    /// 踩过的坑：写成 `private static string InstallDir = DefaultInstallDir;`
    /// 时，这行按**声明顺序**先于下面的 InstallDirCandidates 执行，
    /// 于是 getter 里读到的是 null，启动就抛
    /// TypeInitializationException + NullReferenceException。
    /// 用属性惰性求值就不依赖字段顺序了。
    /// </summary>
    private static string? _installDir;
    private static string InstallDir
    {
        get => _installDir ??= DefaultInstallDir;
        set => _installDir = value;
    }

    /// <summary>
    /// 安装位置的候选，按优先级排列。**优先沿用已存在的位置**，
    /// 避免升级时装成两份；都没装过才用首选位置。
    ///
    /// 历来的三个位置及变更原因：
    ///   1. %LOCALAPPDATA%\ScreenTimeApp —— 最早的写法，但部署脚本用的是 %USERPROFILE%，
    ///      不一致会导致"装了两份、只删掉一份"。
    ///   2. %USERPROFILE%\ScreenTimeApp —— 统一后的位置，可用，但路径较深。
    ///   3. C:\ScreenTime —— 现在的首选。路径短、好找，
    ///      也便于把源码等项目文件放在同一个根目录下统一管理。
    /// </summary>
    private static readonly string[] InstallDirCandidates =
    {
        @"C:\" + AppFolderName,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppFolderName),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName),
    };

    private static string DefaultInstallDir
    {
        get
        {
            // 已经装过的地方优先，就地升级
            foreach (string dir in InstallDirCandidates)
            {
                try
                {
                    if (File.Exists(Path.Combine(dir, ExeName))) return dir;
                }
                catch { }
            }

            // 都没装过：用首选位置。写不进去（无权限等）才退回用户目录。
            string first = InstallDirCandidates[0];
            try
            {
                if (!Directory.Exists(first)) Directory.CreateDirectory(first);
                return first;
            }
            catch
            {
                return InstallDirCandidates[1];
            }
        }
    }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ScreenTime");

    /// <summary>为 true 时不创建快捷方式、不启动程序（供自动化测试用）。</summary>
    private static bool _dryRun;

    /// <summary>本次是否由"请求提权重启"而来（避免重复询问）。</summary>
    private static bool _elevated;

    /// <summary>调用方是否已用 --dir 明确指定了安装目录。</summary>
    private static bool _dirSpecified;

    // ================= 安装目录选择 =================

    /// <summary>
    /// "按任意键继续"。
    ///
    /// 输入被重定向时（自动化测试、管道）Console.ReadKey 会抛异常，
    /// 这里统一兜住，避免因此中断流程。
    /// </summary>
    private static void PauseKey()
    {
        if (Console.IsInputRedirected) return;
        Console.Write("  按任意键继续 ...");
        try { Console.ReadKey(true); } catch { }
        Console.WriteLine();
        Console.WriteLine();
    }

    /// <summary>
    /// 安装程序若在桌面上运行，先给出明确提示。
    ///
    /// 桌面带 Mandatory Label\Low 且强制子项继承，所以放在桌面上的
    /// 安装程序本身就是低完整性进程，什么都做不了。
    /// 这里只提示不阻断——真正拦住错误的是后面的完整性检查。
    /// </summary>
    private static void WarnIfRunningFromDesktop(string baseDir)
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrEmpty(desktop)) return;

            string d = Path.GetFullPath(desktop).TrimEnd('\\');
            string b = Path.GetFullPath(baseDir).TrimEnd('\\');
            if (!b.Equals(d, StringComparison.OrdinalIgnoreCase) &&
                !b.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)) return;

            Console.WriteLine("  ┌────────────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  提示：安装程序正在【桌面】上运行                          │");
            Console.WriteLine("  └────────────────────────────────────────────────────────────┘");
            Console.WriteLine();
            Console.WriteLine("  桌面目录带「低完整性」标签并强制子项继承，从桌面运行的安装程序");
            Console.WriteLine("  会被 Windows 降级，通常无法完成安装。");
            Console.WriteLine();
            Console.WriteLine("  推荐做法：改双击「双击这里安装.cmd」，它会自动把文件挪到");
            Console.WriteLine("  临时目录再安装，不受这个问题影响。");
            Console.WriteLine();
            Console.WriteLine("  或把压缩包解压到桌面以外（例如 C:\\ScreenTime\\）再运行本程序。");
            Console.WriteLine();
            PauseKey();
        }
        catch { }
    }

    /// <summary>
    /// 询问安装目录。
    ///
    /// 默认值就是推荐位置，直接回车即可——绝大多数人不需要改。
    /// 但允许自定义，并且在用户选了"桌面"这种**装上去会出问题**的位置时
    /// 明确拦截：桌面带低完整性标签，从那里运行的程序会被降级，
    /// 托盘图标注册不上、数据也写不进 %LOCALAPPDATA%。
    /// </summary>
    private static bool PromptInstallDir()
    {
        string srcDir = AppContext.BaseDirectory;
        while (true)
        {
            Console.WriteLine("  安装位置：");
            Console.WriteLine($"    {InstallDir}");
            Console.WriteLine();
            Console.WriteLine("  直接回车使用上面的位置，或输入其它路径。");
            Console.WriteLine("  【注意】不要把程序装到桌面上（原因见下）。");
            Console.WriteLine();
            Console.Write("  安装到（回车 = 默认）：");
            string? input = null;
            try { input = Console.ReadLine(); } catch { }

            if (input is null) return true;             // 无交互环境：用默认值
            input = input.Trim().Trim('"');
            if (input.Length == 0)
            {
                // 默认位置也要查一遍：默认值有可能是被 --dir 之外的逻辑改过的
                if (!CheckInstallTarget(InstallDir, srcDir)) continue;
                return true;
            }

            string candidate = input;

            // 只给了盘符或末尾是冒号/反斜杠时，补上默认文件夹名
            if (candidate.EndsWith(":") || candidate.EndsWith(":\\") || candidate.EndsWith(":/"))
                candidate = Path.Combine(candidate, AppFolderName);

            if (!CheckInstallTarget(candidate, srcDir)) continue;

            try
            {
                candidate = Path.GetFullPath(candidate);
            }
            catch
            {
                Console.WriteLine();
                Console.WriteLine("  [错误] 这不是一个有效的路径，请重新输入。");
                Console.WriteLine();
                continue;
            }

            InstallDir = candidate;
            Console.WriteLine();
            Console.WriteLine($"  将安装到：{InstallDir}");
            Console.WriteLine();
            return true;
        }
    }

    /// <summary>
    /// 检查一个候选安装目录是否可用。有已知问题就打印说明并让用户确认；
    /// 用户拒绝则返回 false（调用方重新询问）。
    /// </summary>
    private static bool CheckInstallTarget(string candidate, string srcDir)
    {
        // 与安装源重叠 → 直接拒绝，不给"坚持要用"的选项。
        // 这种情况会先删掉源文件再报错，属于必然失败且会损坏安装包。
        if (PathsOverlap(candidate, srcDir))
        {
            Console.WriteLine();
            Console.WriteLine("  ┌────────────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  这个位置不能用                                            │");
            Console.WriteLine("  └────────────────────────────────────────────────────────────┘");
            Console.WriteLine();
            Console.WriteLine("  它和安装包所在目录重叠，安装时会先清空该目录，");
            Console.WriteLine("  导致安装包自己的文件被删掉，安装必然失败。");
            Console.WriteLine();
            Console.WriteLine($"    安装包目录：{srcDir}");
            Console.WriteLine($"    你选择的是：{candidate}");
            Console.WriteLine();
            Console.WriteLine("  请换一个与安装包目录无关的位置，或直接把安装包解压到别处再运行。");
            Console.WriteLine();
            return false;
        }

        string? problem = ExplainBadLocation(candidate);
        if (problem is null) return true;

        Console.WriteLine();
        Console.WriteLine("  ┌────────────────────────────────────────────────────────────┐");
        Console.WriteLine("  │  不建议安装到这个位置                                      │");
        Console.WriteLine("  └────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
        foreach (string line in problem.Split('\n'))
            Console.WriteLine("  " + line);
        Console.WriteLine();
        Console.Write("  仍要装到这里吗？(y/N) ");
        string? force = null;
        try { force = Console.ReadLine(); } catch { }
        Console.WriteLine();
        return force is not null && force.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断某个安装位置是否有已知问题，返回给用户看的说明；没问题则返回 null。
    ///
    /// 核心判据是**桌面**：桌面目录带 Mandatory Label\Low，
    /// 从那里启动的进程会被 Windows 降级为低完整性，直接后果是
    /// 托盘图标注册失败（Shell_NotifyIcon 拒绝访问）、数据写不进 %LOCALAPPDATA%。
    /// </summary>
    private static string? ExplainBadLocation(string dir)
    {
        string full;
        try { full = Path.GetFullPath(dir); }
        catch { return null; }

        var reasons = new List<string>();

        // 桌面（含子目录）
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop))
        {
            string d = Path.GetFullPath(desktop).TrimEnd('\\');
            if (full.Equals(d, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase))
            {
                reasons.Add("桌面目录带「低完整性」标签，而且会强制子项继承。");
                reasons.Add("装在这里的程序一启动就会被 Windows 降级，后果是：");
                reasons.Add("  · 托盘图标注册不上（点关闭后程序就找不回来了）");
                reasons.Add("  · 用不了 %LOCALAPPDATA%，数据只能降级存到安装目录里");
                reasons.Add("");
                reasons.Add("建议改用：");
                reasons.Add($"  {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppFolderName)}");
                reasons.Add("  C:\\" + AppFolderName);
            }
        }

        // 压缩包/临时目录：装完可能被清理掉
        string temp = Path.GetTempPath().TrimEnd('\\');
        if (full.StartsWith(temp + "\\", StringComparison.OrdinalIgnoreCase))
            reasons.Add("这是临时目录，系统或清理软件可能把它删掉，程序会突然消失。");

        // 系统盘根以外的可移动盘不做判断（无法可靠识别），
        // 但 Program Files 需要管理员权限，值得一提。
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf) &&
            full.StartsWith(Path.GetFullPath(pf).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
            reasons.Add("Program Files 需要管理员权限才能写入，安装和以后的升级都会弹出 UAC。");

        return reasons.Count == 0 ? null : string.Join("\n", reasons);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "安装 " + DisplayName;

        // 可选参数（供自动化测试）：
        //   --dir <路径>   安装到指定目录
        //   --dry-run      只复制并修复标签，不建快捷方式、不启动
        //   --elevated     标记"本次是提权后重启的"，避免重复询问
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--dir" && i + 1 < args.Length) { InstallDir = args[++i]; _dirSpecified = true; }
            else if (args[i] is "--dry-run") _dryRun = true;
            else if (args[i] is "--elevated") _elevated = true;
        }

        Banner();

        string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        string payload = Path.Combine(baseDir, "app");

        // 如果安装程序自己就在桌面上，先说明白会发生什么。
        // 这是最常见的失败场景，与其等用户撞上 Access denied 再解释，
        // 不如一开始就讲清楚。
        WarnIfRunningFromDesktop(baseDir);

        // ---------- 定位程序文件 ----------
        if (!File.Exists(Path.Combine(payload, ExeName)))
        {
            // 兼容"直接放在一起"的布局
            if (File.Exists(Path.Combine(baseDir, ExeName))) payload = baseDir;
            else
            {
                Fail($"找不到程序文件。\n\n期望位置：{Path.Combine(payload, ExeName)}\n\n" +
                     "请确认压缩包已完整解压，且 app 子文件夹与本安装程序在一起。");
                return 1;
            }
        }

        // ---------- 选择安装目录 ----------
        if (!_dirSpecified && !_dryRun)
        {
            if (!PromptInstallDir()) return 1;   // 用户取消
        }

        Console.WriteLine($"  程序文件：{payload}");
        Console.WriteLine($"  安装到  ：{InstallDir}");
        Console.WriteLine();

        // ---------- 1. 结束正在运行的实例 ----------
        Step("1/6", "结束正在运行的实例");
        KillRunning();

        // ---------- 2. 复制文件 ----------
        Step("2/6", "复制程序文件");
        try
        {
            if (!Directory.Exists(InstallDir)) Directory.CreateDirectory(InstallDir);
            CopyTree(payload, InstallDir);
            Console.WriteLine($"        已复制 {CountFiles(InstallDir)} 个文件");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
        {
            // 最常见的触发场景：安装程序自己是低完整性（压缩包在桌面解压），
            // 于是连"写入中完整性目录"这一步都过不去。
            // 这里不要只抛一句英文异常了事，要给可执行的建议。
            Fail("复制文件失败：" + ex.Message);
            Console.WriteLine();
            Console.WriteLine("  这通常是「低完整性」导致的：从桌面（或其它带低完整性标签的");
            Console.WriteLine("  目录）解压出来后运行，进程会被 Windows 降级，无权写程序目录。");
            Console.WriteLine();
            Console.WriteLine("  请把压缩包解压到桌面以外的位置再运行，例如：");
            Console.WriteLine($"        {Path.Combine(Path.GetTempPath(), "ScreenTime")}");
            Console.WriteLine("        C:\\ScreenTime\\");
            Console.WriteLine();
            Console.WriteLine("  按任意键退出 ...");
            try { Console.ReadKey(true); } catch { }
            return 1;
        }
        catch (Exception ex)
        {
            Fail("复制文件失败：" + ex.Message);
            return 1;
        }

        // ---------- 3. 清除低完整性标签（关键步骤）----------
        Step("3/6", "修复文件完整性级别（托盘图标的关键）");

        string ownIl = OwnIntegrityLevel();
        if (ownIl.Contains("低"))
        {
            // 受限令牌下改不了标签：这是安装程序**自身**被降级，不是文件的问题。
            Console.WriteLine();
            Console.WriteLine("  ┌────────────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  本安装程序正以「低完整性」运行，无法完成安装              │");
            Console.WriteLine("  └────────────────────────────────────────────────────────────┘");
            Console.WriteLine();
            Console.WriteLine("  原因：压缩包是在【桌面】上解压的。");
            Console.WriteLine("        桌面目录带「低完整性」标签，且会强制子项继承，");
            Console.WriteLine("        所以解压出来的 安装.exe 也带上了这个标签。");
            Console.WriteLine("        从带该标签的文件启动的进程会被 Windows 降级，");
            Console.WriteLine("        无权写入程序目录（就是刚才那个 Access denied）。");
            Console.WriteLine();
            Console.WriteLine("  解压到桌面以外的位置就好了，例如：");
            Console.WriteLine($"        {Path.Combine(Path.GetTempPath(), "ScreenTime")}   （临时目录）");
            Console.WriteLine("        C:\\ScreenTime\\");
            Console.WriteLine("        D:\\ScreenTime\\");
            Console.WriteLine();
            Console.WriteLine("  或者：让本程序尝试自动提权（会弹出 UAC 提示）。");
            Console.WriteLine("        提权后完整性提升，安装可以继续。");
            Console.WriteLine();
            Console.Write("  怎么选？ [e = 尝试提权 / 其它 = 退出] ");
            string? answer = null;
            try { answer = Console.ReadLine(); } catch { }

            if (answer is not null && answer.Trim().StartsWith("e", StringComparison.OrdinalIgnoreCase))
            {
                return TryRelaunchElevated(args);
            }

            Console.WriteLine();
            Console.WriteLine("  已退出。请把压缩包解压到桌面以外再运行。");
            return 1;
        }

        // 提权后仍被判定为低完整性（极少见）：给出提示但不阻断，
        // 让用户至少能看到后续的标签修复警告。
        if (_elevated)
            Console.WriteLine("  （本次以提权方式运行）");

        if (!ResetIntegrity(InstallDir))
        {
            Console.WriteLine("        [警告] 完整性级别修复失败，托盘图标可能无法注册。");
            Console.WriteLine("        可手动执行：");
            Console.WriteLine($"        icacls \"{InstallDir}\" /setintegritylevel (OI)(CI)Medium /T /C");
        }
        else
        {
            Console.WriteLine("        已设为中完整性");
        }

        // ---------- 4. 快捷方式 ----------
        if (_dryRun)
        {
            Step("4/6", "创建快捷方式（已跳过：--dry-run）");
            Step("5/6", "设置开机自启（已跳过：--dry-run）");
            Step("6/6", "启动并验证（已跳过：--dry-run）");
            Console.WriteLine();
            Console.WriteLine("  干跑完成，未改动系统。");
            return 0;
        }

        Step("4/6", "创建快捷方式");
        string exePath = Path.Combine(InstallDir, ExeName);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        CreateShortcut(Path.Combine(desktop, DisplayName + ".lnk"), exePath, InstallDir, "", 1);
        Console.WriteLine($"        桌面：{DisplayName}.lnk");

        // 写下"已安装"标记。
        // 卸载程序靠它区分「真正的安装目录」与「解压出来的分发包 app\ 目录」——
        // 两者的文件几乎一样，没有标记的话，用户运行分发包里的卸载程序
        // 会把整个分发包删掉。
        try
        {
            string marker = Path.Combine(InstallDir, ".installed");
            File.WriteAllText(marker,
                $"installed={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"version={typeof(Installer).Assembly.GetName().Version}\n" +
                $"from={AppContext.BaseDirectory}\n",
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        [提示] 写入安装标记失败（不影响使用）：{ex.Message}");
        }

        // 卸载入口：Start Menu 里放一个，比"让用户去安装目录找卸载.exe"友好得多。
        // 没有 Start Menu 快捷方式的话，普通用户根本不知道该怎么卸载。
        string uninstallerPath = Path.Combine(InstallDir, UninstallerName);
        if (File.Exists(uninstallerPath))
        {
            try
            {
                string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                string menuDir = Path.Combine(programs, DisplayName);
                Directory.CreateDirectory(menuDir);
                CreateShortcut(Path.Combine(menuDir, "卸载 " + DisplayName + ".lnk"),
                               uninstallerPath, InstallDir, "", 1);
                Console.WriteLine($"        开始菜单：{DisplayName} › 卸载 {DisplayName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"        [提示] 开始菜单快捷方式创建失败（不影响使用）：{ex.Message}");
            }
        }

        // ---------- 5. 开机自启 ----------
        Step("5/6", "设置开机自动启动");

        // 自启完全交给程序自己配置（--autostart on），安装程序不再自己建启动项。
        //
        // 为什么改成这样：
        //   1. 安装程序原先只创建「启动文件夹快捷方式」，而实测本机对启动文件夹里的
        //      .lnk 不生效——Windows 从不往 Explorer\StartupApproved\StartupFolder
        //      写本程序的记录（同键里 RK Keyboard.lnk、Ollama.lnk 都有），
        //      说明开机时它根本没被执行。于是每重装一次，自启就退化成这条无效的路——
        //      用户反复反馈"装了还是不能自启"，根因就在这里。
        //   2. 自启逻辑（计划任务 + 启动文件夹脚本，互为保险）集中在程序内部。
        //      安装程序再实现一遍必然出现两份不一致的实现——事实上就出现过：
        //      安装程序建 .lnk，而程序内部已经改用 .vbs 了。
        //      让唯一的实现方去配置，避免这类分叉。
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = InstallDir,
            };
            psi.ArgumentList.Add("--autostart");
            psi.ArgumentList.Add("on");

            using Process? p = Process.Start(psi);
            if (p is not null && p.WaitForExit(30000))
            {
                Console.WriteLine(p.ExitCode == 0
                    ? "        已启用开机自启（计划任务 + 启动文件夹脚本）"
                    : $"        [提示] 自启配置返回码 {p.ExitCode}，可在设置面板里重新勾选");
            }
            else
            {
                Console.WriteLine("        [提示] 自启配置超时，可在设置面板里重新勾选");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"        [提示] 自启配置失败（不影响使用）：{ex.Message}");
        }

        // 清掉旧版本安装程序留下的、已被证明无效的启动文件夹快捷方式
        try
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string oldLnk = Path.Combine(startup, DisplayName + ".lnk");
            if (File.Exists(oldLnk))
            {
                File.Delete(oldLnk);
                Console.WriteLine("        已清理旧版留下的启动文件夹快捷方式（实测无效）");
            }
        }
        catch { }

        // ---------- 6. 启动并验证 ----------
        Step("6/6", "启动并验证");
        try
        {
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = InstallDir });
        }
        catch (Exception ex)
        {
            Fail("启动失败：" + ex.Message);
            return 1;
        }

        Console.WriteLine("        等待程序写出状态报告 ...");
        string statusPath = Path.Combine(DataDir, "tray-status.txt");
        string? verdict = WaitForVerdict(statusPath, TimeSpan.FromSeconds(25));

        Console.WriteLine();
        if (verdict is null)
        {
            Console.WriteLine("  [提示] 未能读到状态报告，请打开主界面自行确认。");
        }
        else if (verdict.Contains("注册成功"))
        {
            Console.WriteLine("  ╔══════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║  安装完成，托盘图标已注册成功                        ║");
            Console.WriteLine("  ╚══════════════════════════════════════════════════════╝");
            Console.WriteLine();
            Console.WriteLine("  托盘图标可能在折叠区里：点击任务栏右下角的 ^ 箭头，");
            Console.WriteLine("  把里面的时钟图标拖到任务栏上，以后就常驻显示了。");
        }
        else
        {
            Console.WriteLine("  [注意] 托盘图标注册未成功：");
            Console.WriteLine("         " + verdict);
            Console.WriteLine($"         详细报告：{statusPath}");
        }

        Console.WriteLine();
        Console.WriteLine($"  程序位置：{InstallDir}");
        Console.WriteLine($"  数据位置：{DataDir}");
        Console.WriteLine();
        Console.WriteLine("  卸载：双击安装目录里的「" + UninstallerName + "」，");
        Console.WriteLine($"        或从开始菜单打开「{DisplayName} › 卸载 {DisplayName}」。");
        Console.WriteLine("        卸载程序会列出所有痕迹并让你选择是否保留使用记录。");
        Console.WriteLine();
        Console.WriteLine("  按任意键关闭本窗口 ...");
        try { Console.ReadKey(true); } catch { }
        return 0;
    }

    // ================= 各步骤实现 =================

    /// <summary>
    /// 以管理员身份重新启动自己（触发 UAC）。提权后的进程是「高完整性」，
    /// 可以正常写程序目录，也能清除文件上的低完整性标签。
    ///
    /// 为什么用 runas 而不是别的办法：
    ///   低完整性进程**无法**自行提升，只能请求系统重新以更高权限启动。
    ///   runas 会弹 UAC，用户同意后新进程以管理员身份运行。
    /// 注意加 --elevated 标记：提权后的进程若还检测到低完整性就会再次询问，
    /// 而管理员进程不可能还是低完整性，加这个标记是为了防止极端情况下的死循环。
    /// </summary>
    private static int TryRelaunchElevated(string[] originalArgs)
    {
        try
        {
            string? self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
            {
                Console.WriteLine("  无法定位安装程序自身，提权失败。");
                return 1;
            }

            var psi = new ProcessStartInfo(self)
            {
                UseShellExecute = true,   // runas 必须走 ShellExecute
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (string a in originalArgs)
            {
                if (!string.Equals(a, "--elevated", StringComparison.OrdinalIgnoreCase))
                    psi.ArgumentList.Add(a);
            }
            psi.ArgumentList.Add("--elevated");

            Process.Start(psi);
            Console.WriteLine();
            Console.WriteLine("  已在新的（管理员）窗口中继续安装，本窗口可以关闭。");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("  提权失败（可能是你取消了 UAC 提示）：" + ex.Message);
            Console.WriteLine("  请改为把压缩包解压到桌面以外再运行。");
            return 1;
        }
    }

    private static void KillRunning()
    {
        try
        {
            foreach (Process p in Process.GetProcessesByName("ScreenTime.App"))
            {
                try
                {
                    p.Kill();
                    p.WaitForExit(5000);
                    Console.WriteLine($"        已结束 PID {p.Id}");
                }
                catch { /* 忽略单个失败 */ }
            }
            System.Threading.Thread.Sleep(1500);
        }
        catch { /* 忽略 */ }
    }

    /// <summary>
    /// 把程序文件复制到安装目录。**支持覆盖升级。**
    ///
    /// 升级安全性由两点保证：
    ///   1. 用户数据不在安装目录里 —— 它在 %LOCALAPPDATA%\ScreenTime，
    ///      安装程序从不触碰；即便安装目录里出现 data\，也会被显式跳过。
    ///   2. 复制前先清掉"旧版本遗留、新版本已不再提供"的程序文件，
    ///      避免废弃的 DLL 残留导致加载到错误的依赖。
    /// </summary>
    private static void CopyTree(string from, string to)
    {
        // 安全护栏：源目录与目标目录重叠时**必须拒绝**，不能继续。
        //
        // 实测过的灾难场景：安装包解压在 C:\ScreenTime\_setup，而默认安装目录是
        // C:\ScreenTime —— 两者重叠。CopyTree 会把 C:\ScreenTime 当作"旧版本目录"，
        // 先删掉其中不在待复制清单里的文件，而清单正是从
        // C:\ScreenTime\_setup\app 读出来的……结果是**安装程序删掉了自己的源文件**，
        // 随后报 "Could not find file ...dll"，安装失败且源文件也没了。
        if (PathsOverlap(from, to))
        {
            throw new InvalidOperationException(
                "安装源目录与安装目录重叠，无法安全复制。\n" +
                $"  源目录  ：{from}\n" +
                $"  安装目录：{to}\n" +
                "请把压缩包解压到与安装目录无关的位置，或换一个安装目录。");
        }

        Directory.CreateDirectory(to);

        // 新版本会提供哪些文件（相对路径）
        var wanted = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(from.Length).TrimStart('\\');
            if (IsUserData(rel)) continue;
            wanted.Add(rel);
        }

        // 清掉旧版本遗留的程序文件（保留用户数据目录）
        int removed = 0;
        foreach (string f in Directory.GetFiles(to, "*", SearchOption.AllDirectories))
        {
            string rel = f.Substring(to.Length).TrimStart('\\');
            if (IsUserData(rel)) continue;
            if (wanted.Contains(rel)) continue;
            try { File.Delete(f); removed++; } catch { /* 被占用则留着 */ }
        }
        if (removed > 0) Console.WriteLine($"        清理旧版本遗留文件 {removed} 个");

        // 建目录
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(from, to));
        }

        // 复制（覆盖同名文件即为升级）
        int copied = 0;
        foreach (string rel in wanted)
        {
            File.Copy(Path.Combine(from, rel), Path.Combine(to, rel), true);
            copied++;
        }
        Console.WriteLine($"        已复制 {copied} 个文件（覆盖升级）");
    }

    /// <summary>
    /// 两个目录是否重叠（一个是另一个的父/子目录，或就是同一个）。
    /// 一律用 FullName 比较，顺带处理 C:\a 与 C:\a\ 这种写法差异。
    /// </summary>
    private static bool PathsOverlap(string a, string b)
    {
        try
        {
            string pa = Path.GetFullPath(a).TrimEnd('\\') + "\\";
            string pb = Path.GetFullPath(b).TrimEnd('\\') + "\\";
            return pa.StartsWith(pb, StringComparison.OrdinalIgnoreCase)
                || pb.StartsWith(pa, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>该相对路径是否属于用户数据（升级时必须原样保留）。</summary>
    private static bool IsUserData(string relativePath)
    {
        string rel = relativePath.TrimStart('\\');
        return rel.Equals("data", StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(@"data\", StringComparison.OrdinalIgnoreCase);
    }

    private static int CountFiles(string dir)
    {
        try { return Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
        catch { return 0; }
    }

    /// <summary>
    /// 把目录树里的文件设回中完整性。
    ///
    /// 两条路都试：
    ///   1. 直接调用 SetNamedSecurityInfo（进程内，不依赖外部程序，最可靠）；
    ///   2. 退回 icacls 逐个文件设置。
    ///
    /// **不能用 `icacls /T /C /setintegritylevel (OI)(CI)Medium`**：
    /// 那个递归形式对文件无效（实测文件仍是 Low），而且退出码仍是 0，
    /// 早期版本因此"看起来成功、实际没改"。
    ///
    /// 另外注意：**进程只能把标签设为不高于自身完整性的级别**。
    /// 所以只要安装程序本身是以中完整性启动的（用户双击即如此），就能成功。
    /// </summary>
    private static bool ResetIntegrity(string dir)
    {
        string[] files;
        try
        {
            files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            Console.WriteLine("        遍历文件失败：" + ex.Message);
            return false;
        }

        int viaApi = 0, viaIcasls = 0;
        var failures = new System.Collections.Generic.List<string>();
        string lastApiError = "";

        Console.WriteLine("        安装程序自身完整性：" + OwnIntegrityLevel());

        foreach (string f in files)
        {
            // 先试 icacls（微软官方工具，行为可预期）
            if (RunIcasls($"\"{f}\" /setintegritylevel Medium"))
            {
                viaIcasls++;
                continue;
            }

            // 再试直接调用 API
            if (TrySetMediumByApi(f, out lastApiError)) viaApi++;
            else failures.Add(Path.GetFileName(f) + "（" + lastApiError + "）");
        }

        Console.WriteLine($"        icacls 方式 {viaIcasls} 个，API 方式 {viaApi} 个" +
                          (failures.Count > 0 ? $"，失败 {failures.Count} 个" : ""));

        if (failures.Count > 0 && failures.Count <= 3)
            foreach (string f in failures) Console.WriteLine("          失败：" + f);

        // 复核：确认没有残留 Low
        var stillLow = new System.Collections.Generic.List<string>();
        foreach (string f in files)
            if (HasLowLabel(f)) stillLow.Add(Path.GetFileName(f));

        if (stillLow.Count > 0)
        {
            Console.WriteLine($"        复核仍有 {stillLow.Count} 个文件为低完整性：");
            Console.WriteLine("          " + string.Join(", ", stillLow));
            Console.WriteLine("        提示：请确认是以普通方式双击运行本安装程序，");
            Console.WriteLine("              而不是从受限的终端或沙箱里启动。");
            return false;
        }

        Console.WriteLine("        复核通过：全部为中完整性");
        return true;
    }

    /// <summary>
    /// 用 Win32 API 把文件的完整性标签设为中。
    ///
    /// 直接构造「中完整性」SID（S-1-16-8192）并作为 LABEL_SECURITY_INFORMATION
    /// 写入对象的安全描述符。比调 icacls 少一层进程创建，也更可靠。
    /// </summary>
    private static bool TrySetMediumByApi(string path, out string error)
    {
        error = "";
        IntPtr sd = IntPtr.Zero;
        IntPtr sid = IntPtr.Zero;
        try
        {
            // 中完整性 SID：S-1-16-8192 = 0x2000
            var sidBytes = new byte[12];
            sidBytes[0] = 1;                                   // Revision
            sidBytes[1] = 1;                                   // SubAuthorityCount
            sidBytes[2] = 0; sidBytes[3] = 0; sidBytes[4] = 0;
            sidBytes[5] = 0; sidBytes[6] = 0; sidBytes[7] = 16; // SECURITY_MANDATORY_LABEL_AUTHORITY
            sidBytes[8] = 0x00; sidBytes[9] = 0x20;            // 0x2000 小端
            sidBytes[10] = 0; sidBytes[11] = 0;

            sid = Marshal.AllocHGlobal(sidBytes.Length);
            Marshal.Copy(sidBytes, 0, sid, sidBytes.Length);

            // ACL：一条 ACE，允许「中完整性 SID」以「无写升级」方式访问
            // ACE 结构：AceType(1) AceFlags(1) AceSize(2) AccessMask(4) Sid...
            const int sidLen = 12;
            const int aceSize = 4 + 4 + sidLen;
            const int aclSize = 8 + aceSize;
            var acl = new byte[aclSize];
            acl[0] = 2;    // ACL_REVISION
            acl[1] = 0;
            BitConverter.GetBytes((ushort)aclSize).CopyTo(acl, 2);
            BitConverter.GetBytes((ushort)0).CopyTo(acl, 4);           // AceCount = 0，稍后填
            BitConverter.GetBytes((ushort)0).CopyTo(acl, 6);

            int off = 8;
            acl[off + 0] = 0x11;   // SYSTEM_MANDATORY_LABEL_ACE_TYPE
            acl[off + 1] = 0;      // AceFlags
            BitConverter.GetBytes((ushort)aceSize).CopyTo(acl, off + 2);
            // 0x1 = SYSTEM_MANDATORY_LABEL_NO_WRITE_UP
            BitConverter.GetBytes(0x00000001).CopyTo(acl, off + 4);
            Array.Copy(sidBytes, 0, acl, off + 8, sidLen);
            BitConverter.GetBytes((ushort)1).CopyTo(acl, 4);            // AceCount = 1

            sd = Marshal.AllocHGlobal(aclSize + 32);
            // 直接构造 SECURITY_DESCRIPTOR：Revision(1) Sbz1(1) Control(2) Owner(4) Group(4) Sacl(4) Dacl(4)
            var sdBytes = new byte[20 + aclSize];
            sdBytes[0] = 1;                                     // SECURITY_DESCRIPTOR_REVISION
            sdBytes[1] = 0;
            BitConverter.GetBytes((ushort)0x8004).CopyTo(sdBytes, 2);   // SE_SACL_PRESENT | SE_SELF_RELATIVE
            BitConverter.GetBytes(20).CopyTo(sdBytes, 16);              // Sacl 偏移
            Array.Copy(acl, 0, sdBytes, 20, aclSize);

            Marshal.FreeHGlobal(sd);
            sd = Marshal.AllocHGlobal(sdBytes.Length);
            Marshal.Copy(sdBytes, 0, sd, sdBytes.Length);

            const uint LABEL_SECURITY_INFORMATION = 0x00000010;
            int rc = SetNamedSecurityInfoW(path, SE_FILE_OBJECT, LABEL_SECURITY_INFORMATION,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sd);
            if (rc != 0)
            {
                error = "API 错误码 " + rc;
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            return false;
        }
        finally
        {
            if (sid != IntPtr.Zero) Marshal.FreeHGlobal(sid);
            if (sd != IntPtr.Zero) Marshal.FreeHGlobal(sd);
        }
    }

    private const int SE_FILE_OBJECT = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int SetNamedSecurityInfoW(
        string objectName, int objectType, uint securityInfo,
        IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    /// <summary>检查文件是否带低完整性标签。用 icacls 查询，兼容性最好。</summary>
    private static bool HasLowLabel(string path)
    {
        if (RunIcasls($"\"{path}\"", out string output))
            return output.Contains("Low Mandatory Level", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>
    /// 通过 cmd.exe 调用 icacls。
    ///
    /// 绕一层 cmd 是刻意的：直接 CreateProcess("icacls") 在某些受限宿主下
    /// 会拿到被进一步约束的令牌，导致"退出码 0 但标签没改"；
    /// 经由 cmd 调用与用户手工执行完全同路径。
    /// </summary>
    private static bool RunIcasls(string arguments)
        => RunIcasls(arguments, out _);

    private static bool RunIcasls(string arguments, out string output)
        => RunHidden("cmd.exe", "/c icacls " + arguments, out output);

    /// <summary>读取本进程自己的完整性级别，用于诊断。</summary>
    private static string OwnIntegrityLevel()
    {
        try
        {
            if (RunHidden("whoami", "/groups", out string o))
            {
                foreach (string line in o.Split('\n'))
                {
                    if (!line.Contains("Mandatory Label", StringComparison.OrdinalIgnoreCase)) continue;
                    if (line.Contains("Medium", StringComparison.OrdinalIgnoreCase)) return "中";
                    if (line.Contains("Low", StringComparison.OrdinalIgnoreCase)) return "低（受限）";
                    if (line.Contains("High", StringComparison.OrdinalIgnoreCase)) return "高";
                    return line.Trim();
                }
            }
        }
        catch { /* 忽略 */ }
        return "未知";
    }

    private static bool RunHidden(string exe, string arguments, out string output)
    {
        output = "";
        try
        {
            var psi = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using Process? p = Process.Start(psi);
            if (p is null) return false;
            output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
            p.WaitForExit(120000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void CreateShortcut(string linkPath, string target, string workingDir,
                                       string arguments, int windowStyle)
    {
        try
        {
            // WScript.Shell 通过 COM 延迟绑定创建 .lnk，避免引入额外依赖
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) return;
            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(linkPath);
            lnk.TargetPath = target;
            lnk.WorkingDirectory = workingDir;
            lnk.Arguments = arguments;
            lnk.Description = DisplayName;
            lnk.IconLocation = target + ",0";
            lnk.WindowStyle = windowStyle;
            lnk.Save();
            Marshal.ReleaseComObject(lnk);
            Marshal.ReleaseComObject(shell);
        }
        catch (Exception ex)
        {
            Console.WriteLine("        [警告] 创建快捷方式失败：" + ex.Message);
        }
    }

    private static string? WaitForVerdict(string statusPath, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(statusPath))
                {
                    foreach (string line in File.ReadAllLines(statusPath, Encoding.UTF8))
                    {
                        if (line.StartsWith("结论", StringComparison.Ordinal))
                            return line.Replace("结论：", "").Trim();
                    }
                }
            }
            catch { /* 文件可能正被写入 */ }
            System.Threading.Thread.Sleep(700);
        }
        return null;
    }

    // ================= 输出 =================

    private static void Banner()
    {
        Console.WriteLine();
        Console.WriteLine("  ╔══════════════════════════════════════════════════════╗");
        Console.WriteLine("  ║         屏幕使用时间  ·  安装程序                    ║");
        Console.WriteLine("  ║         记录电脑使用时长，托盘常驻后台               ║");
        Console.WriteLine("  ╚══════════════════════════════════════════════════════╝");
        Console.WriteLine();
    }

    private static void Step(string n, string text)
    {
        Console.WriteLine($"  [{n}] {text}");
    }

    private static void Fail(string message)
    {
        Console.WriteLine();
        Console.WriteLine("  [安装失败]");
        Console.WriteLine("  " + message.Replace("\n", "\n  "));
        Console.WriteLine();
        Console.WriteLine("  按任意键关闭 ...");
        try { Console.ReadKey(true); } catch { }
    }
}
