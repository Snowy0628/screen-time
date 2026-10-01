using System.Threading;
using ScreenTime.Core;
using System.Diagnostics;
using System.Windows.Forms;

namespace ScreenTime.App;

/// <summary>自我测试：连续采样若干秒，落库后直接查询并打印，用于验证采集链路。</summary>
internal static class SelfTest
{
    public static int Run(int seconds, string dbPath, int idleThreshold = 60)
    {
        Console.WriteLine("=== ScreenTime 采集链路自检 ===");
        Console.WriteLine($"数据库: {dbPath}");
        Console.WriteLine($"采样时长: {seconds} 秒");
        Console.WriteLine($"空闲阈值: {idleThreshold} 秒");
        Console.WriteLine();

        using var log = new Log(Path.Combine(Path.GetDirectoryName(dbPath)!, "selftest.log"));
        using var store = new UsageStore(dbPath);
        store.Diagnostics = msg => Console.WriteLine("  [诊断]" + msg);

        // 自检时把落库间隔压短，便于观察
        using var recorder = new Recorder(store) { FlushIntervalSeconds = 5 };
        recorder.Idle.IdleThresholdSeconds = idleThreshold;
        recorder.Diagnostics = msg => Console.WriteLine("  [诊断] " + msg);
        recorder.GapDetected += s => Console.WriteLine($"  [缺口] 检测到 {s} 秒睡眠/时钟跳变，已回填");
        recorder.StateChanged += s => Console.WriteLine($"  [状态] {s}");

        long t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        recorder.Start(t0);

        // 每 500 毫秒 Tick 一次，模拟真实计时器节奏
        for (int i = 0; i < seconds * 2; i++)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            recorder.Tick(now);

            if (i % 4 == 0)
            {
                Sample? s = recorder.CurrentSample;
                string stateText = s is { } v ? StateText(v.State) : "—";
                string appText = s is { } v2 ? v2.App.DisplayName : "—";
                Console.WriteLine($"  t+{i / 2,3}s  状态={stateText,-10} 前台={appText}");
            }
            Thread.Sleep(500);
        }

        recorder.Stop();

        // ---- 交叉验证：直接从数据库读回来 ----
        Console.WriteLine();
        Console.WriteLine("=== 数据库回读校验 ===");
        DateTime today = DateTime.Today;
        Dictionary<UsageState, long> totals = store.GetStateTotals(today);
        DayTotals day = store.GetDayTotals(today);

        Console.WriteLine($"  活跃 {Fmt(day.ActiveSeconds)} / 空闲 {Fmt(day.IdleSeconds)} / " +
                          $"锁屏 {Fmt(day.LockedSeconds)} / 熄屏睡眠 {Fmt(day.OffSeconds)}");
        Console.WriteLine($"  屏幕亮着合计: {Fmt(day.ScreenOnSeconds)}");
        Console.WriteLine($"  识别到不同应用: {day.DistinctApps} 个");

        List<AppTotal> apps = store.GetAppTotals(today);
        if (apps.Count > 0)
        {
            Console.WriteLine("  应用排行:");
            foreach (AppTotal a in apps.Take(10))
            {
                Console.WriteLine($"    {a.Seconds,6}s  {a.DisplayName}");
            }
        }
        else
        {
            Console.WriteLine("  应用排行: 空");
        }

        (long rows, long bytes) = store.StoreInfo();
        Console.WriteLine($"  片段总行数: {rows}，数据库大小: {bytes / 1024.0:F1} KB");
        Console.WriteLine($"  本次写入片段: {recorder.TotalSpansWritten}，回填缺口: {recorder.TotalGapsFilled}");
        Console.WriteLine($"  最近心跳: {store.LastHeartbeatUtc()?.ToLocalTime():yyyy-MM-dd HH:mm:ss}");

        // ---- 原始片段 dump：交叉核对合并与归属是否正确 ----
        Console.WriteLine();
        Console.WriteLine("=== app 表 ===");
        foreach (string a in store.DumpApps()) Console.WriteLine("  " + a);
        Console.WriteLine("=== session 表（原始列）===");
        foreach (string s in store.DumpSessionRaw()) Console.WriteLine("  " + s);
        Console.WriteLine("=== 原始片段（直接读 session 表）===");
        List<string> rawRows = store.DumpSessions(today);
        if (rawRows.Count == 0) Console.WriteLine("  （无）");
        foreach (string r in rawRows) Console.WriteLine("  " + r);

        // ---- 一致性检查 ----
        bool ok = true;
        long sumStates = totals.Values.Sum();
        if (sumStates <= 0) { Console.WriteLine("  [失败] 没有采集到任何数据"); ok = false; }
        if (day.ActiveSeconds < 0 || day.IdleSeconds < 0) { Console.WriteLine("  [失败] 出现负时长"); ok = false; }
        if (sumStates > seconds + 30) { Console.WriteLine($"  [警告] 统计时长 {sumStates}s 明显超过采样时长 {seconds}s"); }

        Console.WriteLine();
        Console.WriteLine(ok ? "=== 自检通过 ===" : "=== 自检失败 ===");
        return ok ? 0 : 1;
    }

    private static string StateText(UsageState s) => s switch
    {
        UsageState.Active => "前台使用",
        UsageState.Idle => "空闲",
        UsageState.Locked => "锁屏",
        UsageState.Off => "熄屏/睡眠",
        _ => "未知",
    };

    internal static string Fmt(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = seconds % 3600 / 60, s = seconds % 60;
        return h > 0 ? $"{h}小时{m}分" : m > 0 ? $"{m}分{s}秒" : $"{s}秒";
    }
}
