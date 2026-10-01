using ScreenTime.Core;

namespace ScreenTime.App;

/// <summary>
/// 周 / 月视图自检。
///
/// 为什么必须有：周/月视图曾经因为一个"调用自己"的静态方法
/// （<c>MondayOffset(d) =&gt; MondayOffset(d)</c>）而抛出 StackOverflowException——
/// 这种异常在 .NET 里**无法被捕获**，进程直接终止，表现出来就是
/// "点一下周视图就卡死"。当时没有任何自检覆盖这两个视图，所以一直没被发现。
///
/// 这里把周/月的关键换算都跑一遍，任何一项异常都会直接失败。
/// </summary>
internal static class RangeTest
{
    public static int Run(string dbPath)
    {
        Console.WriteLine("=== 周 / 月视图自检 ===");
        Console.WriteLine($"数据库: {dbPath}");
        Console.WriteLine();

        int failures = 0;

        using var store = new UsageStore(dbPath);
        var vm = new RangeViewModel(store);

        // ---- 1. 周视图 ----
        Console.WriteLine("[周视图]");
        try
        {
            vm.SetMode(RangeMode.Week);
            Console.WriteLine($"  标题      : {vm.RangeTitle}");
            Console.WriteLine($"  柱数      : {vm.DayBars.Count}（应为 7）");
            Console.WriteLine($"  排行条数  : {vm.Ranking.Count}");
            Console.WriteLine($"  汇总      : {vm.SummaryText}");
            Console.WriteLine($"  对比      : {vm.ComparisonText}");

            if (vm.DayBars.Count != 7) { Console.WriteLine("  ✗ 本周柱数不是 7"); failures++; }
            else Console.WriteLine("  ✓ 周一至周日各一根柱子");

            // 翻页：向前 2 周、向后 2 周
            for (int i = 0; i < 2; i++) vm.Shift(-1);
            Console.WriteLine($"  前翻两周后: {vm.RangeTitle}（{vm.DayBars.Count} 根）");
            for (int i = 0; i < 2; i++) vm.Shift(1);
            Console.WriteLine($"  翻回当前后: {vm.RangeTitle}");

            vm.Shift(1);   // 试图翻到未来，应被挡住
            Console.WriteLine($"  尝试翻到未来: {vm.RangeTitle}");
            vm.GoCurrent();
            Console.WriteLine($"  回到本期  : {vm.RangeTitle}");
            Console.WriteLine("  ✓ 周视图渲染与翻页正常");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 周视图抛异常：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // ---- 2. 月视图 ----
        Console.WriteLine();
        Console.WriteLine("[月视图]");
        int days = DateTime.DaysInMonth(DateTime.Today.Year, DateTime.Today.Month);
        try
        {
            vm.SetMode(RangeMode.Month);
            Console.WriteLine($"  标题      : {vm.RangeTitle}");
            Console.WriteLine($"  热力格数  : {vm.HeatCells.Count}");
            Console.WriteLine($"  排行条数  : {vm.Ranking.Count}");
            Console.WriteLine($"  汇总      : {vm.SummaryText}");
            Console.WriteLine($"  对比      : {vm.ComparisonText}");

            // 热力图是**按整周对齐的日历**（周一开头），因此格数 = 该月覆盖的整周天数，
            // 会多于当月天数（例如 2026 年 10 月是 9/28–11/1 共 41 格）。
            // 判据取 28–42 这个合法区间，而不是"恰好等于当月天数"。
            if (vm.HeatCells.Count < 28 || vm.HeatCells.Count > 42)
            {
                Console.WriteLine($"  ✗ 热力格数 {vm.HeatCells.Count} 不在 28–42 的合理区间");
                failures++;
            }
            else
            {
                Console.WriteLine($"  ✓ 热力图为整周对齐的日历（{vm.HeatCells.Count} 格，" +
                                  $"当月 {days} 天）");
            }

            for (int i = 0; i < 3; i++) vm.Shift(-1);
            Console.WriteLine($"  前翻三月后: {vm.RangeTitle}（{vm.HeatCells.Count} 格）");
            vm.GoCurrent();
            Console.WriteLine($"  回到本期  : {vm.RangeTitle}");

            // 切回单日（这一步曾经因为标题沿用了月标题而露馅）
            vm.SetMode(RangeMode.Day);
            Console.WriteLine($"  切回单日  : 标题 = {vm.RangeTitle}");
            Console.WriteLine("  ✓ 月视图渲染与翻页正常");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 月视图抛异常：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        // ---- 3. 模式互切 ----
        Console.WriteLine();
        Console.WriteLine("[模式互切]");
        try
        {
            vm.SetMode(RangeMode.Day);
            vm.SetMode(RangeMode.Week);
            vm.SetMode(RangeMode.Month);
            vm.SetMode(RangeMode.Day);
            Console.WriteLine("  ✓ 单日/周/月 反复切换无异常");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 切换失败：{ex.GetType().Name}: {ex.Message}");
            failures++;
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "=== 自检通过 ===" : $"=== 自检失败（{failures} 项）===");
        return failures == 0 ? 0 : 1;
    }
}
