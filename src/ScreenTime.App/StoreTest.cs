using ScreenTime.Core;

namespace ScreenTime.App;

/// <summary>
/// 存储层定向诊断：验证"尾行续接"是否生效。
/// 直接调用 UsageStore 的内部接口，不经过真实采样，避免噪声。
/// </summary>
internal static class StoreTest
{
    public static int Run(string dbPath)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = dbPath + suffix;
            if (File.Exists(f)) File.Delete(f);
        }

        Console.WriteLine("=== 尾行续接定向诊断 ===");
        Console.WriteLine($"数据库: {dbPath}");

        using var store = new UsageStore(dbPath);

        var off = new Span(1_000_000, 1_000_005, UsageState.Off, AppIdentity.Unknown);
        var off2 = new Span(1_000_005, 1_000_010, UsageState.Off, AppIdentity.Unknown);

        Console.WriteLine();
        Console.WriteLine("--- 第 1 次 WriteSpans（tail=null，应新建行）---");
        TailRow? tail = store.WriteSpans(new[] { off }, null, 0);
        Report(store, tail, "第1次后");

        Console.WriteLine();
        Console.WriteLine("--- 第 2 次 WriteSpans（同状态连续，应续接成 1 行）---");
        TailRow? tail2 = store.WriteSpans(new[] { off2 }, tail, 0);
        Report(store, tail2, "第2次后");

        Console.WriteLine();
        Console.WriteLine("--- TryGetTail 回读 ---");
        TailRow? t3 = store.TryGetTail();
        Console.WriteLine(t3 is null ? "  null" : $"  id={t3.Id} [{t3.StartUtc},{t3.EndUtc}) state={t3.State} mergeKey='{t3.MergeKey}'");

        Console.WriteLine();
        Console.WriteLine("--- 状态切换（Off → Locked，必须新建行）---");
        var locked = new Span(1_000_010, 1_000_015, UsageState.Locked, AppIdentity.Unknown);
        TailRow? tail4 = store.WriteSpans(new[] { locked }, tail2, 0);
        Report(store, tail4, "切换后");

        (long rows, long bytes) = store.StoreInfo();
        Console.WriteLine();
        Console.WriteLine($"最终行数: {rows}（期望 2：熄屏一段 + 锁屏一段），大小 {bytes} 字节");

        bool pass = rows == 2;
        Console.WriteLine();
        Console.WriteLine("片段明细:");
        foreach (string line in store.DumpSessions(DateTime.Today)) Console.WriteLine("  " + line);

        Console.WriteLine();
        Console.WriteLine(pass ? "=== 续接逻辑正确 ===" : "=== 续接逻辑有缺陷 ===");

        // ---- 用注入采样做确定性端到端测试 ----
        return pass && RecorderTest(dbPath + ".rec") ? 0 : 1;
    }

    /// <summary>
    /// 确定性采集测试：注入固定采样序列，验证状态机、合并、落库行数是否符合预期。
    /// 因为不依赖真实前台窗口，所以结果可重复。
    /// </summary>
    private static bool RecorderTest(string dbPath)
    {
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = dbPath + suffix;
            if (File.Exists(f)) File.Delete(f);
        }

        Console.WriteLine();
        Console.WriteLine("=== 采集状态机确定性测试（注入采样）===");

        using var store = new UsageStore(dbPath);
        using var rec = new Recorder(store) { FlushIntervalSeconds = 5 };

        var appA = new AppIdentity(@"C:\apps\Editor.exe", "Editor.exe", "示例编辑器");
        var appB = new AppIdentity(@"C:\apps\Browser.exe", "Browser.exe", "示例浏览器");

        long t = 2_000_000;

        // 时间线设计（每段秒数）：
        //   活跃 A 60s | 空闲 30s | 活跃 B 45s | 锁屏 20s | 熄屏 120s | 活跃 A 10s
        // 期望：状态机把连续相同采样合并，落库行数为 6（每种连续状态+应用一行），
        //       除非落库边界把某段切开——这正是"尾行续接"要保证不发生的。
        var plan = new (UsageState State, AppIdentity App, int Seconds)[]
        {
            (UsageState.Active, appA, 60),
            (UsageState.Idle, AppIdentity.Unknown, 30),
            (UsageState.Active, appB, 45),
            (UsageState.Locked, AppIdentity.Unknown, 20),
            (UsageState.Off, AppIdentity.Unknown, 120),
            (UsageState.Active, appA, 10),
        };

        rec.Start(t);
        foreach ((UsageState state, AppIdentity app, int seconds) in plan)
        {
            var sample = new Sample(state, app, false);
            for (int i = 0; i < seconds; i++)
            {
                t += 1;
                rec.Tick(t, sample);
            }
        }
        rec.Flush();

        (long rows, _) = store.StoreInfo();
        int expectedRows = plan.Length;
        Console.WriteLine($"  注入总时长: {plan.Sum(p => p.Seconds)}s");
        Console.WriteLine($"  落库行数: {rows}（期望 {expectedRows}）");

        Console.WriteLine("  片段明细:");
        foreach (string line in store.DumpSessionRaw(50)) Console.WriteLine("    " + line);

        // 关键校验：活跃总时长必须等于注入值（尾行续接不能丢时间、也不能重复计）
        // 注意：注入的时间戳是 2000000（1970 年），必须按那个日期查询，而不是今天
        DateTime injectDay = DateTimeOffset.FromUnixTimeSeconds(2_000_000).ToLocalTime().Date;
        long expectedActive = plan.Where(p => p.State == UsageState.Active).Sum(p => (long)p.Seconds);
        long actualActive = store.GetDayTotals(injectDay).ActiveSeconds;
        Console.WriteLine($"  活跃时长: 实际 {actualActive}s / 期望 {expectedActive}s（查询日期 {injectDay:yyyy-MM-dd}）");

        bool ok = rows == expectedRows && actualActive == expectedActive;

        // ---- 复现 probe 里的精确时序：Start 后只 Tick 一次就 Flush ----
        Console.WriteLine();
        Console.WriteLine("--- 边界用例：Start 后仅一次 Tick 再 Flush ---");
        string db2 = dbPath + ".single";
        foreach (string suffix in new[] { "", "-wal", "-shm" })
        {
            string f = db2 + suffix;
            if (File.Exists(f)) File.Delete(f);
        }
        using (var store2 = new UsageStore(db2))
        using (var rec2 = new Recorder(store2) { FlushIntervalSeconds = 60 })
        {
            long t0 = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            rec2.Start(t0);
            var sp = new Sample(UsageState.Active, appA, false);
            rec2.Tick(t0 + 1, sp);
            int w = rec2.Flush();
            (long r2, _) = store2.StoreInfo();
            Console.WriteLine($"  单次 Tick 后 Flush 写入 {w} 个片段，库中行数 {r2}（期望 1）");
            if (r2 != 1) ok = false;
        }

        Console.WriteLine(ok ? "=== 状态机测试通过 ===" : "=== 状态机测试失败 ===");
        return ok;
    }

    private static void Report(UsageStore store, TailRow? tail, string label)
    {
        (long rows, _) = store.StoreInfo();
        Console.WriteLine($"  {label}: 库中行数={rows}");
        Console.WriteLine(tail is null
            ? "  返回 tail = null"
            : $"  返回 tail: id={tail.Id} [{tail.StartUtc},{tail.EndUtc}) state={tail.State} key='{tail.MergeKey}'");
    }
}
