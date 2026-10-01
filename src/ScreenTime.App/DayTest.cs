using ScreenTime.Core;

namespace ScreenTime.App;

/// <summary>
/// 单日视图的数据换算自检。
///
/// 为什么需要它：有些错误只有把界面画出来才看得到，例如"时间轴被已记录的片段拉满
/// 整个 24 小时"——纯粹看代码不容易发现，而截图又依赖肉眼判断。
/// 这里直接把 ViewModel 算出的片段时长求和，与一天的总秒数比对。
///
/// 校验项：
///   1. 时间轴各段时长之和 == 86400 秒（时间轴必须代表完整的一天）；
///   2. 分时柱状图恰好 24 根柱子；
///   3. 单日各状态时长之和 <= 86400（不应出现负时长或超出一天）。
/// </summary>
internal static class DayTest
{
    private static string Describe(System.Windows.Media.Color? c) =>
        c is { } v ? $"#{v.R:X2}{v.G:X2}{v.B:X2}" : "无";

    public static int Run(string dbPath)
    {
        Console.WriteLine("=== 单日视图数据换算自检 ===");
        Console.WriteLine($"数据库: {dbPath}");
        Console.WriteLine();

        using var store = new UsageStore(dbPath);
        var vm = new DayViewModel(store, () => new DayTotals(0, 0, 0, 0, 0));
        vm.Refresh();

        int failures = 0;

        // ---- 1. 时间轴必须覆盖完整一天 ----
        long timelineTotal = 0;
        foreach (SegmentVm s in vm.Timeline) timelineTotal += (long)s.Seconds;

        // 时间轴各段必须首尾相接、不重叠。
        // 重叠会让总时长虚高（曾经出现总和 86472 > 一天的 86400），
        // 而光看"总和"很难发现，所以单独查一遍。
        int overlaps = 0;
        string? prevEnd = null;
        foreach (SegmentVm s in vm.Timeline)
        {
            string[] parts = s.Tooltip.Split('–');
            if (parts.Length < 2) continue;
            string start = parts[0].Trim();
            string end = parts[1].Trim().Split('　')[0].Trim();
            if (prevEnd is not null && string.CompareOrdinal(start, prevEnd) < 0)
            {
                overlaps++;
                if (overlaps <= 3)
                    Console.WriteLine($"  ✗ 片段重叠：上一段结束于 {prevEnd}，本段却从 {start} 开始");
            }
            prevEnd = end;
        }

        // 时间轴**始终代表完整的 24 小时**：
        // 已有记录按状态着色，无记录的空隙补成"熄屏/睡眠"或"无记录"，
        // 今天尚未到来的时段也画出来（用更淡的底色），这样色条永远是满的。
        // 早期断言写死"到此刻为止"，与后来的设计不符。
        const long DaySeconds = 86400;
        Console.WriteLine($"时间轴片段数: {vm.Timeline.Count}");
        Console.WriteLine($"时间轴总时长: {timelineTotal}s（应为 {DaySeconds}s，完整一天）");
        if (Math.Abs(timelineTotal - DaySeconds) > 2)
        {
            Console.WriteLine($"  ✗ 时间轴未覆盖完整一天，差值 {timelineTotal - DaySeconds}s");
            failures++;
        }
        else
        {
            Console.WriteLine("  ✓ 时间轴覆盖完整一天");
        }

        if (overlaps > 0)
        {
            Console.WriteLine($"  ✗ 存在 {overlaps} 处片段重叠（会导致总时长虚高）");
            failures++;
        }
        else
        {
            Console.WriteLine("  ✓ 片段首尾相接，无重叠");
        }

        // 打印前若干段，便于人工核对
        Console.WriteLine("  片段明细（最多 10 条）:");
        int shown = 0;
        foreach (SegmentVm s in vm.Timeline)
        {
            if (shown++ >= 10) { Console.WriteLine("    …"); break; }
            string tip = s.Tooltip.Replace("\n", " / ");
            Console.WriteLine($"    {s.Seconds,7:F0}s  {tip}");
        }

        // 最后几段：尾部空隙最容易算错（今天不该画到未来）
        Console.WriteLine("  末 3 段:");
        foreach (SegmentVm s in vm.Timeline.Skip(Math.Max(0, vm.Timeline.Count - 3)))
        {
            Console.WriteLine($"    {s.Seconds,7:F0}s  {s.Tooltip.Replace("\n", " / ")}");
        }

        // ---- 2. 柱状图恰好 24 根 ----
        Console.WriteLine();
        Console.WriteLine($"分时柱状图柱数: {vm.Hours.Count}（应为 24）");
        if (vm.Hours.Count != 24)
        {
            Console.WriteLine("  ✗ 柱数不等于 24");
            failures++;
        }
        else
        {
            Console.WriteLine("  ✓ 柱数正确");
        }

        // 有数据的小时
        var withData = vm.Hours.Where(h => h.HasData).ToList();
        Console.WriteLine($"  其中有数据的小时: {withData.Count} 根");
        foreach (HourBarVm h in withData)
        {
            Console.WriteLine($"    {h.HourLabel} 时: 柱高 {h.BarHeight:F1}px, " +
                              $"{h.Slices.Count} 个应用色块, {h.TopIcons.Count} 个图标");
        }

        // ---- 2b. 图标提取能力：柱顶图标全部依赖它，取不到就什么都不显示 ----
        Console.WriteLine();
        Console.WriteLine("图标提取检查（柱顶图标依赖）：");
        int iconOk = 0, iconFail = 0;
        foreach (string path in store.GetAllAppPaths().Take(12))
        {
            var icon = AppIconCache.Get(path, 16);
            var color = AppIconCache.GetDominantColor(path);
            if (icon is not null)
            {
                iconOk++;
                Console.WriteLine($"  ✓ {icon.Width}x{icon.Height}  主色={Describe(color)}  " +
                                  $"{Path.GetFileName(path)}");
            }
            else
            {
                iconFail++;
                // 取不到图标不一定是缺陷：有些应用装在别的机器上，
                // 演示库里的路径在本机不存在是正常的。只有"图标在、颜色取不到"才算问题。
                Console.WriteLine($"  · 无图标（路径在本机可能不存在）  主色={Describe(color)}  " +
                                  $"{Path.GetFileName(path)}");
            }
        }
        if (iconOk + iconFail == 0) Console.WriteLine("  （库里没有应用记录）");
        int coloredCount = 0;
        foreach (string p in store.GetAllAppPaths().Take(12))
            if (AppIconCache.GetDominantColor(p) is not null) coloredCount++;
        Console.WriteLine($"  可提取主色的应用: {coloredCount} 个（时间轴与柱状图用图标主色上色）");

        // 柱顶图标总数：为 0 说明界面不可能画出图标
        int totalIcons = vm.Hours.Sum(h => h.TopIcons.Count);
        Console.WriteLine($"  柱顶图标合计 {totalIcons} 个（为 0 则界面画不出图标）");

        // ---- 3. 状态时长不得超过一天 ----
        Console.WriteLine();
        DayTotals t = store.GetDayTotals(vm.SelectedDate);
        long all = t.ActiveSeconds + t.IdleSeconds + t.LockedSeconds + t.OffSeconds;
        Console.WriteLine($"各状态合计: {all}s（活跃 {t.ActiveSeconds} / 空闲 {t.IdleSeconds} / " +
                          $"锁屏 {t.LockedSeconds} / 熄屏 {t.OffSeconds}）");
        if (all > DaySeconds)
        {
            Console.WriteLine("  ✗ 合计超过一天，数据异常");
            failures++;
        }
        else if (t.ActiveSeconds < 0 || t.IdleSeconds < 0 || t.LockedSeconds < 0 || t.OffSeconds < 0)
        {
            Console.WriteLine("  ✗ 出现负时长");
            failures++;
        }
        else
        {
            Console.WriteLine("  ✓ 状态时长正常");
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "=== 自检通过 ===" : $"=== 自检失败（{failures} 项）===");
        return failures == 0 ? 0 : 1;
    }
}
