using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTime.App;

/// <summary>
/// 开机自启管理。
///
/// 优先用**计划任务**（登录时触发）：由任务计划服务在用户登录时直接拉起进程，
/// 不依赖 explorer 处理"启动"文件夹的时序，因此更可靠；
/// 配合 `--minimized` 参数实现静默启动（只有托盘图标，不弹窗口）。
///
/// 退路是**启动文件夹快捷方式**。
///
/// 实测背景：本机的启动文件夹机制不可靠——Windows 只有在真正处理过某项之后
/// 才会在 `Explorer\StartupApproved\StartupFolder` 留下记录，而程序那一项
/// 从未出现在该键里，说明开机时根本没被执行。
///
/// 实现注意：**状态检测靠读文件，不靠运行 schtasks 并捕获输出**。
/// 受限环境（沙箱）禁止子进程使用命名管道，任何
/// `Process.Start(..., RedirectStandardOutput = true)` 都会永久挂起；
/// 计划任务的 XML 定义可以从 `%SystemRoot%\System32\Tasks\&lt;名称&gt;` 直接读到。
/// </summary>
internal static class AutoStart
{
    private const string TaskName = "ScreenTimeAutoStart";
    private const string TaskArgument = "--minimized";

    /// <summary>
    /// 计划任务定义文件的候选路径。
    /// 直接拼 `%SystemRoot%\System32\Tasks\` 而不走 `SpecialFolder.System` + `..\Tasks\`，
    /// 后者在部分环境下解析出的路径会被拒绝访问。
    /// </summary>
    private static IEnumerable<string> TaskDefinitionPaths()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(root)) root = Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
        yield return Path.Combine(root, "System32", "Tasks", TaskName);
        yield return Path.Combine(root, "Sysnative", "Tasks", TaskName);
    }

    /// <summary>
    /// 判断计划任务是否已注册。
    ///
    /// 只读文件判断，**不运行 schtasks 查询**——受限环境禁止子进程管道，
    /// 捕获输出会永久挂起。
    /// </summary>
    private static bool TaskExists()
    {
        foreach (string p in TaskDefinitionPaths())
        {
            try { if (File.Exists(p)) return true; }
            catch { /* 该路径不可访问，换下一个 */ }
        }
        return false;
    }

    /// <summary>当前是否已配置自启（计划任务或启动文件夹任一存在）。</summary>
    public static bool IsEnabled()
    {
        try { return TaskExists() || File.Exists(StartupShortcutPath); }
        catch { return false; }
    }

    /// <summary>启用了哪种方式，用于界面提示。</summary>
    public static string CurrentMethod()
    {
        try
        {
            bool task = TaskExists();
            bool lnk = File.Exists(StartupShortcutPath);
            if (task && lnk) return "计划任务 + 启动文件夹";
            if (task) return "计划任务";
            if (lnk) return "启动文件夹";
            return "未配置";
        }
        catch
        {
            return "未知";
        }
    }

    /// <summary>启用自启。优先计划任务，失败退回启动文件夹。</summary>
    public static (bool Ok, string Method, string Detail) Enable(string exePath)
    {
        // 两条路都试一遍并都保留：计划任务优先（更可靠），启动文件夹作为双保险。
        bool taskOk = TryCreateTask(exePath, out string taskDetail);
        bool lnkOk = TryCreateStartupShortcut(exePath, out string lnkDetail);

        if (taskOk && lnkOk) return (true, "计划任务 + 启动文件夹", "两条路径都已配置");
        if (taskOk) return (true, "计划任务", taskDetail);
        if (lnkOk) return (true, "启动文件夹", lnkDetail + "（计划任务创建失败：" + taskDetail + "）");
        return (false, "", "两种方式都失败 —— " + taskDetail + " / " + lnkDetail);
    }

    /// <summary>关闭自启：两条路都清掉，避免留下多余项。</summary>
    public static (bool Ok, string Detail) Disable()
    {
        var sb = new StringBuilder();
        bool ok = true;

        if (TaskExists())
        {
            if (TryDeleteTask(out string d)) sb.Append("已删除计划任务");
            else { ok = false; sb.Append("删除计划任务失败：" + d); }
        }

        try
        {
            if (File.Exists(StartupShortcutPath))
            {
                File.Delete(StartupShortcutPath);
                if (sb.Length > 0) sb.Append("；");
                sb.Append("已删除启动项快捷方式");
            }
        }
        catch (Exception ex)
        {
            ok = false;
            sb.Append("；删除快捷方式失败：" + ex.Message);
        }

        if (sb.Length == 0) sb.Append("本来就没有配置自启");
        return (ok, sb.ToString());
    }

    // ================= 计划任务 =================

    /// <summary>
    /// 创建"登录时触发 + 静默启动"的计划任务。
    ///
    /// 改用 **Task Scheduler COM API** 而不是 schtasks.exe：
    /// 后者需要捕获输出才能知道成功与否，而受限环境禁止子进程管道。
    /// COM 方式在进程内完成，返回值直接可得。
    ///
    /// 参数含义：登录时触发；普通权限（不弹 UAC）；仅在该用户登录时运行
    /// （这样进程在用户会话里，才能显示托盘图标）；登录后延迟 20 秒避开开机争抢。
    /// </summary>
    private static bool TryCreateTask(string exePath, out string detail)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("Schedule.Service");
            if (t is null) { detail = "任务计划服务不可用（Schedule.Service 缺失）"; return false; }

            dynamic service = Activator.CreateInstance(t)!;
            service.Connect();

            dynamic root = service.GetFolder("\\");
            dynamic task = service.NewTask(0);

            // 注册信息
            task.RegistrationInfo.Description = "屏幕使用时间 - 登录后静默启动（只在托盘）";
            task.RegistrationInfo.Author = Environment.UserName;

            // 登录时触发
            dynamic triggers = task.Triggers;
            dynamic logon = triggers.Create(9);          // 9 = TASK_TRIGGER_LOGON
            logon.Delay = "PT20S";                       // 延迟 20 秒
            logon.UserId = Environment.UserName;         // 明确指定当前用户

            // 动作：exe --minimized
            dynamic actions = task.Actions;
            dynamic exec = actions.Create(0);            // 0 = TASK_ACTION_EXEC
            exec.Path = exePath;
            exec.Arguments = TaskArgument;
            exec.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";

            // 普通权限运行，且只在用户登录时运行（InteractiveToken）
            task.Principal.RunLevel = 0;                 // 0 = TASK_RUNLEVEL_LUA（普通权限）
            task.Principal.LogonType = 3;                // 3 = TASK_LOGON_INTERACTIVE_TOKEN

            // 设置：允许按需启动；不要因为"没接电源""空闲中"而跳过或中断。
            // 这几项对笔记本尤其重要——默认策略会在用电池时干脆不启动。
            //
            // 注意属性归属：DisallowStartIfOnBatteries 等属于 ITaskSettings，
            // 而 StopOnIdleEnd / RestartOnIdle 属于 **IdleSettings 子对象**。
            // 写错层级会抛 RuntimeBinderException（踩过）。
            dynamic settings = task.Settings;
            settings.Enabled = true;
            settings.DisallowStartIfOnBatteries = false;
            settings.StopIfGoingOnBatteries = false;
            settings.ExecutionTimeLimit = "PT0S";        // 不限时长（常驻程序）
            settings.StartWhenAvailable = true;

            dynamic idle = settings.IdleSettings;
            idle.StopOnIdleEnd = false;                  // 空闲时不要把常驻程序停掉
            idle.RestartOnIdle = false;

            // 注册到根目录（TASK_CREATE_OR_UPDATE = 6）
            root.RegisterTaskDefinition(TaskName, task, 6, null, null, 3);

            Marshal.ReleaseComObject(exec);
            Marshal.ReleaseComObject(actions);
            Marshal.ReleaseComObject(logon);
            Marshal.ReleaseComObject(triggers);
            Marshal.ReleaseComObject(idle);
            Marshal.ReleaseComObject(settings);
            Marshal.ReleaseComObject(task);
            Marshal.ReleaseComObject(root);
            Marshal.ReleaseComObject(service);

            detail = "已创建登录触发的计划任务（延迟 20 秒，静默启动）";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"COM 创建失败：{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryDeleteTask(out string detail)
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

    // ================= 启动文件夹（退路） =================

    private static string StartupShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                     "屏幕使用时间.lnk");

    private static bool TryCreateStartupShortcut(string exePath, out string detail)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t is null) { detail = "WScript.Shell 不可用"; return false; }

            dynamic shell = Activator.CreateInstance(t)!;
            dynamic lnk = shell.CreateShortcut(StartupShortcutPath);
            lnk.TargetPath = exePath;
            lnk.Arguments = TaskArgument;
            lnk.WorkingDirectory = Path.GetDirectoryName(exePath) ?? "";
            lnk.Description = "屏幕使用时间 - 登录后静默启动";
            lnk.IconLocation = exePath + ",0";
            lnk.WindowStyle = 7;   // 最小化
            lnk.Save();
            Marshal.ReleaseComObject(lnk);
            Marshal.ReleaseComObject(shell);

            detail = "已创建启动文件夹快捷方式";
            return true;
        }
        catch (Exception ex)
        {
            detail = "创建快捷方式失败：" + ex.Message;
            return false;
        }
    }
}
