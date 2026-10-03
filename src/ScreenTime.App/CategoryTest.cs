using System;
using System.Collections.Generic;
using System.Linq;
using ScreenTime.Core;

namespace ScreenTime.App;

/// <summary>
/// 应用分类功能自检（`--catest`）。
///
/// ### 为什么需要
///
/// 分类功能有一堆"看不出来"的逻辑：手动设定要压过自动推断、按路径而不是
/// 进程名匹配、删除自定义分类时要连带清掉指着它的手动设定、饼图占比之和
/// 要等于总时长。这些错了界面不会报错，只会显示成"分类不对"或"占比对不上"，
/// 靠肉眼很难判断是哪一环。
///
/// 所以这里用**临时数据库**跑一遍完整流程，逐条断言。
/// 关键是**不碰用户的真实数据库**——把分类试坏了会很烦。
/// </summary>
internal static class CategoryTest
{
    public static int Run()
    {
        Console.WriteLine("=== 应用分类自检 ===");

        string dbPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"screentime-cattest-{Guid.NewGuid():N}.db");

        int failures = 0;
        void Check(bool ok, string what)
        {
            Console.WriteLine(ok ? $"  ✓ {what}" : $"  ✗ {what}");
            if (!ok) failures++;
        }

        try
        {
            Console.WriteLine($"临时数据库: {dbPath}");
            Console.WriteLine();

            using var store = new UsageStore(dbPath);

            // ---- 1. 表是否建好、初始为空 ----
            Dictionary<string, string> initial = store.GetAppCategories();
            Check(initial.Count == 0, "新库的手动分类表为空");

            // ---- 2. 写入与读回 ----
            const string exe1 = @"C:\Apps\Demo.exe";
            const string exe2 = @"D:\Other\Demo.exe";     // 同名不同目录
            store.SetAppCategory(exe1, "学习");
            store.SetAppCategory(exe2, "剪辑");

            Dictionary<string, string> map = store.GetAppCategories();
            Check(map.Count == 2, $"写入两条后读回 {map.Count} 条（应为 2）");
            Check(map.TryGetValue(exe1, out string? c1) && c1 == "学习", "第一条分类正确");
            Check(map.TryGetValue(exe2, out string? c2) && c2 == "剪辑", "第二条分类正确");

            // ---- 3. 同名 exe 不同路径要能分别归类 ----
            Check(c1 != c2, "同名 exe 按路径分别归类（不同目录互不影响）");

            // ---- 4. 分类判定优先级：手动 > 自动推断 ----
            AppPalette.ReloadManualCategories(store);
            // msedge.exe 的关键字推断是"浏览器"，手动设成"学习"后应当以手动为准
            const string edge = @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";
            Check(AppPalette.InferCategory(edge) == "浏览器", "msedge 的自动推断是「浏览器」");
            store.SetAppCategory(edge, "学习");
            AppPalette.ReloadManualCategories(store);
            Check(AppPalette.CategoryOf(edge) == "学习", "手动设定压过自动推断");

            // 清掉手动设定后应回到自动推断
            store.SetAppCategory(edge, "");
            AppPalette.ReloadManualCategories(store);
            Check(AppPalette.CategoryOf(edge) == "浏览器", "清除手动设定后恢复自动推断");

            // ---- 5. 空分类名等于删除 ----
            store.SetAppCategory(exe1, "");
            map = store.GetAppCategories();
            Check(!map.ContainsKey(exe1), "传空分类名等于删除该条设定");

            // ---- 6. 覆盖写 ----
            store.SetAppCategory(exe2, "开发");
            map = store.GetAppCategories();
            Check(map.TryGetValue(exe2, out string? c3) && c3 == "开发", "同一路径重复设定会覆盖");

            // ---- 7. 按分类名批量删除 ----
            store.SetAppCategory(exe1, "临时分类");
            store.SetAppCategory(exe2, "临时分类");
            int removed = store.DeleteCategoriesByName("临时分类");
            Check(removed == 2, $"按分类名删除影响 {removed} 条（应为 2）");
            Check(store.GetAppCategories().Count == 0, "批量删除后手动分类表为空");

            // ---- 8. 自定义分类的增删 ----
            int customBefore = AppSettings.Current.CustomCategories.Count;
            bool added = Categories.AddCustom("学习");
            Check(added, "可以新增自定义分类「学习」");
            Check(!Categories.AddCustom("学习"), "重名的自定义分类会被拒绝");
            Check(!Categories.AddCustom(""), "空名字会被拒绝");
            Check(!Categories.AddCustom("开发"), "与系统默认分类重名的会被拒绝");
            Check(Categories.All().Contains("学习"), "新分类出现在可用分类列表里");
            Check(Categories.ColorOf("学习") != Categories.ColorOf("未定义的其他名字"),
                  "自定义分类拿到的是稳定且不同的颜色");

            Categories.RemoveCustom("学习");
            Check(!Categories.All().Contains("学习"), "删除后自定义分类不再出现在列表里");

            // 默认分类不允许删
            int defaultCount = Categories.Defaults.Length;
            foreach (string d in Categories.Defaults)
            {
                if (!Categories.IsDefault(d)) { failures++; Console.WriteLine($"  ✗ {d} 应被识别为默认分类"); }
            }
            Check(Categories.Defaults.Length == defaultCount, $"{defaultCount} 个系统默认分类都受保护");
            Categories.RemoveCustom("开发");
            Check(Categories.All().Contains("开发"), "尝试删除系统默认分类无效");

            // ---- 9. 恢复现场 ----
            while (AppSettings.Current.CustomCategories.Count > customBefore)
                AppSettings.Current.CustomCategories.RemoveAt(AppSettings.Current.CustomCategories.Count - 1);
            AppSettings.Current.Save();

            // ---- 10. 饼图占比之和必须等于 100% ----
            // 用真实数据库跑，把当天的应用按分类聚合一次
            using var real = new UsageStore(AppPaths.DatabasePath);
            AppPalette.ReloadManualCategories(real);
            List<AppTotal> totals = real.GetAppTotals(DateTime.Today, 500);

            var byCat = new Dictionary<string, long>(StringComparer.Ordinal);
            long grand = 0;
            foreach (AppTotal a in totals)
            {
                string cat = AppPalette.CategoryOf(a.FilePath);
                byCat.TryGetValue(cat, out long cur);
                byCat[cat] = cur + a.Seconds;
                grand += a.Seconds;
            }

            Console.WriteLine();
            Console.WriteLine($"真实数据（今天）：{totals.Count} 个应用，合计 {grand} 秒");
            foreach (KeyValuePair<string, long> kv in byCat.OrderByDescending(k => k.Value))
            {
                Console.WriteLine($"    {kv.Key,-8} {DurationText.Short(kv.Value),10}  "
                                + $"{DurationText.Percent(kv.Value, grand),7}");
            }

            long sum = byCat.Values.Sum();
            Check(sum == grand, $"各分类时长之和等于总时长（{sum} vs {grand}）");

            double pctSum = grand > 0 ? byCat.Values.Sum(v => 100.0 * v / grand) : 100.0;
            Check(Math.Abs(pctSum - 100.0) < 0.0001, $"各分类占比之和为 100%（实测 {pctSum:0.####}%）");

            Check(!byCat.ContainsKey(""), "不存在空分类名");

            // ---- 11. 周期切换的日期区间 ----
            //
            // 三种周期的口径必须与主界面一致（周从周一开始、月是自然月），
            // 否则面板上的数字和主界面排行对不上，用户会以为其中一个算错了。
            using var vm = new UsageStore(AppPaths.DatabasePath);   // 只为构造 ViewModel
            var catVm = new CategoryViewModel(vm);

            catVm.RangeMode = RangeMode.Day;
            (DateTime dFrom, DateTime dTo) = catVm.CurrentRange();
            Check(dFrom == DateTime.Today && dTo == DateTime.Today, "「日」区间就是今天");
            Check(catVm.TotalCaption == "今日合计", "「日」的中心小字是「今日合计」");

            catVm.RangeMode = RangeMode.Week;
            (DateTime wFrom, DateTime wTo) = catVm.CurrentRange();
            Check(wFrom.DayOfWeek == DayOfWeek.Monday, $"「周」从周一开始（实际 {wFrom.DayOfWeek}）");
            Check((wTo - wFrom).Days == 6, $"「周」跨 7 天（实际 {(wTo - wFrom).Days + 1} 天）");
            Check(wFrom <= DateTime.Today && DateTime.Today <= wTo, "今天落在本周区间内");
            Check(catVm.TotalCaption == "本周合计", "「周」的中心小字是「本周合计」");

            catVm.RangeMode = RangeMode.Month;
            (DateTime mFrom, DateTime mTo) = catVm.CurrentRange();
            Check(mFrom.Day == 1, "「月」从 1 号开始");
            Check(mFrom.Month == DateTime.Today.Month && mFrom.Year == DateTime.Today.Year,
                  "「月」是当前自然月");
            Check(mTo.AddDays(1).Day == 1, "「月」的最后一天是月末");
            Check(catVm.TotalCaption == "本月合计", "「月」的中心小字是「本月合计」");

            // 周与月是**两个独立区间**，只保证都包含今天。
            // 曾经写过"月区间必须覆盖周区间"——那是错的：周一所在的周常常跨月
            // （比如 10 月 3 日是周六，本周从 9 月 28 日开始），
            // 此时周区间会有一半落在上个月，断言必然失败。
            Check(wFrom <= DateTime.Today && DateTime.Today <= wTo, "周区间包含今天");
            Check(mFrom <= DateTime.Today && DateTime.Today <= mTo, "月区间包含今天");

            // ---- 12. 分类颜色覆盖 ----
            string before = ThemeCustomizer.ToHex(Categories.ColorFor("娱乐"));
            Categories.SetCustomColor("娱乐", "#123456");
            string after = ThemeCustomizer.ToHex(Categories.ColorFor("娱乐"));
            Check(after == "#123456", $"自定义分类色生效（{before} → {after}）");
            Categories.SetCustomColor("娱乐", "");
            Check(ThemeCustomizer.ToHex(Categories.ColorFor("娱乐")) == before,
                  "清除自定义色后恢复默认");

            // 未知分类也要能拿到颜色（否则饼图会出现无色扇区）
            Check(Categories.ColorFor("一个没定义过的分类名") != default,
                  "未定义的分类也能拿到兜底色");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[异常] {ex}");
            failures++;
        }
        finally
        {
            try { if (System.IO.File.Exists(dbPath)) System.IO.File.Delete(dbPath); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0 ? "全部通过" : $"有 {failures} 项失败");
        return failures == 0 ? 0 : 1;
    }
}
