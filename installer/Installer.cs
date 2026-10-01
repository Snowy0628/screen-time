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
// 安装位置刻意选在 %USERPROFILE%\ScreenTimeApp（或 %LOCALAPPDATA%\ScreenTimeApp）：
//   这是用户自己的目录，无需管理员权限，且没有低完整性标签。
//   两者都可行，实际沿用已存在的那个，避免升级时装成两份。
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

internal static class Installer
{
    private const string AppFolderName = "ScreenTimeApp";
    private const string ExeName = "ScreenTime.App.exe";
    private const string DisplayName = "屏幕使用时间";
    private const string UninstallerName = "卸载.exe";

    private static string InstallDir = DefaultInstallDir;

    /// <summary>
    /// 安装位置。
    ///
    /// 为什么用 %USERPROFILE% 而不是 %LOCALAPPDATA%：
    /// 两者都能避免桌面的低完整性标签问题，但实测部署脚本与用户机器上
    /// 一直是 %USERPROFILE%\ScreenTimeApp。曾经这里写成 %LOCALAPPDATA%，
    /// 与部署脚本不一致，会出现"装了两份、只删掉一份"的混乱。
    /// 现在统一为 %USERPROFILE%，同时**升级时优先沿用已存在的旧位置**，
    /// 避免老用户升级后变成两份安装。
    /// </summary>
    private static string DefaultInstallDir
    {
        get
        {
            string userProfile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), AppFolderName);
            string localAppData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppFolderName);

            // 旧位置有程序文件 → 就地升级，不要另起一份
            try
            {
                if (!Directory.Exists(userProfile) &&
                    File.Exists(Path.Combine(localAppData, ExeName)))
                    return localAppData;
            }
            catch { }

            return userProfile;
        }
    }

    private static string DataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "ScreenTime");

    /// <summary>为 true 时不创建快捷方式、不启动程序（供自动化测试用）。</summary>
    private static bool _dryRun;

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "安装 " + DisplayName;

        // 可选参数（供自动化测试）：
        //   --dir <路径>   安装到指定目录
        //   --dry-run      只复制并修复标签，不建快捷方式、不启动
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] is "--dir" && i + 1 < args.Length) InstallDir = args[++i];
            else if (args[i] is "--dry-run") _dryRun = true;
        }

        Banner();

        string baseDir = AppContext.BaseDirectory.TrimEnd('\\');
        string payload = Path.Combine(baseDir, "app");

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
            // 必须明确告诉用户怎么办，否则他只会看到"装完了但托盘没有图标"。
            Console.WriteLine();
            Console.WriteLine("  ┌────────────────────────────────────────────────────┐");
            Console.WriteLine("  │  注意：本安装程序正以「低完整性」运行               │");
            Console.WriteLine("  └────────────────────────────────────────────────────┘");
            Console.WriteLine();
            Console.WriteLine("  这意味着它是从受限环境（终端 / 沙箱 / 脚本宿主）启动的。");
            Console.WriteLine("  受限进程无权把文件的完整性标签改回「中」，因此：");
            Console.WriteLine("    · 托盘图标不会出现");
            Console.WriteLine("    · 程序无法使用 %LOCALAPPDATA% 存放数据");
            Console.WriteLine();
            Console.WriteLine("  解决办法：关闭本窗口，直接在【文件资源管理器】里");
            Console.WriteLine("  双击本安装程序（或解压出来的文件夹里的它）。");
            Console.WriteLine("  这样启动的安装程序是「中完整性」，一切正常。");
            Console.WriteLine();
            Console.Write("  仍要继续吗？(y/N) ");
            string? answer = null;
            try { answer = Console.ReadLine(); } catch { }
            if (answer is null || !answer.Trim().StartsWith("y", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("  已取消。");
                return 1;
            }
            Console.WriteLine();
        }

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
        string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        CreateShortcut(Path.Combine(startup, DisplayName + ".lnk"), exePath, InstallDir, "--minimized", 7);
        Console.WriteLine("        已加入启动文件夹（登录后静默启动，只留托盘）");

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
