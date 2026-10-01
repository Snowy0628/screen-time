using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using ScreenTime.Core;

// WPF 与 WinForms 都有 Brush/Color/Brushes，固定用 WPF 的
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace ScreenTime.App;

/// <summary>
/// 周 / 月视图的数据源。
///
/// 与单日视图共用同一套口径：
///   * 状态汇总走 UsageStore.GetStateTotalsRange（一条 SQL 覆盖整个区间）；
///   * 应用排行走 GetAppTotalsRange。
/// 不逐日循环查询，避免"周视图发 7 次查询、月视图发 31 次"。
///
/// 月的热力图需要逐日数值，因此引入一次按天分桶的查询（GetRawSpans 覆盖整月），
/// 在内存里分桶——比 31 次独立查询更省，也不会因为跨越月边界而算错。
/// </summary>
internal sealed class RangeViewModel
{
    private readonly UsageStore _store;

    public RangeMode Mode { get; private set; } = RangeMode.Day;

    /// <summary>当前区间的任意一天（用于定位周/月）。</summary>
    public DateTime Anchor { get; private set; } = DateTime.Today;

    public ObservableCollection<DayBarVm> DayBars { get; } = new();
    public ObservableCollection<HeatCellVm> HeatCells { get; } = new();
    public ObservableCollection<AppRowVm> Ranking { get; } = new();

    public string RangeTitle { get; private set; } = "";
    public string SummaryText { get; private set; } = "";
    public string ComparisonText { get; private set; } = "";
    public Brush ComparisonBrush { get; private set; } = Brushes.Gray;
    public bool HasComparison { get; private set; }

    public string WeekChartTitle { get; private set; } = "";
    public string MonthHeatTitle { get; private set; } = "";
    public string RankingTitle { get; private set; } = "";
    public string RankingHint { get; private set; } = "";

    public RangeViewModel(UsageStore store) => _store = store;

    public void SetMode(RangeMode mode)
    {
        Mode = mode;
        Refresh();
    }

    /// <summary>在周/月区间内前后翻页。</summary>
    public void Shift(int direction)
    {
        if (direction == 0) return;
        Anchor = Mode switch
        {
            RangeMode.Week => Anchor.AddDays(7 * direction),
            RangeMode.Month => Anchor.AddMonths(direction),
            _ => Anchor.AddDays(direction),
        };

        // 不允许翻到未来
        if (Mode == RangeMode.Week && WeekStart(Anchor) > DateTime.Today) Anchor = Anchor.AddDays(-7);
        if (Mode == RangeMode.Month && new DateTime(Anchor.Year, Anchor.Month, 1) > DateTime.Today)
            Anchor = Anchor.AddMonths(-1);

        Refresh();
    }

    public void GoCurrent()
    {
        Anchor = DateTime.Today;
        Refresh();
    }

    // ------------------------------------------------------------------
    // 区间计算
    // ------------------------------------------------------------------

    /// <summary>周一为一周的第一天（符合国内习惯）。</summary>
    private static DateTime WeekStart(DateTime d) => d.Date.AddDays(-MondayOffset(d));

    /// <summary>
    /// 该日期距离本周一的天数（周一=0 … 周日=6）。
    ///
    /// 注意：这里曾经写成 `=> MondayOffset(d)`，即**调用自己**。
    /// 无限递归抛出的 StackOverflowException 在 .NET 里无法被捕获，
    /// 进程会直接终止——表现出来就是"点周/月视图立刻卡死"。
    /// 因为周/月视图完全依赖它，这个错误让那两个视图从未可用过。
    /// </summary>
    private static int MondayOffset(DateTime d) => ((int)d.DayOfWeek + 6) % 7;

    private (DateTime From, DateTime To) CurrentRange() => Mode switch
    {
        RangeMode.Week => (WeekStart(Anchor), WeekStart(Anchor).AddDays(6)),
        RangeMode.Month => (new DateTime(Anchor.Year, Anchor.Month, 1),
                            new DateTime(Anchor.Year, Anchor.Month, 1).AddMonths(1).AddDays(-1)),
        _ => (Anchor.Date, Anchor.Date),
    };

    public void Refresh()
    {
        try
        {
            (DateTime from, DateTime to) = CurrentRange();

            // 今天之后不算，避免把"未来"算进分母
            DateTime effectiveTo = to > DateTime.Today ? DateTime.Today : to;
            bool empty = effectiveTo < from;

            Dictionary<UsageState, long> totals = empty
                ? new Dictionary<UsageState, long>
                {
                    [UsageState.Active] = 0, [UsageState.Idle] = 0,
                    [UsageState.Locked] = 0, [UsageState.Off] = 0,
                }
                : _store.GetStateTotalsRange(from, effectiveTo);

            long screen = totals[UsageState.Active] + totals[UsageState.Idle] + totals[UsageState.Locked];

            if (Mode == RangeMode.Week)
            {
                RangeTitle = $"{from:yyyy年M月d日} – {to:M月d日}";
                WeekChartTitle = "本周每天的使用时长";
                RankingTitle = "本周应用排行";
                BuildDayBars(from, to);
                BuildComparisonForWeek(from, screen);
            }
            else
            {
                RangeTitle = $"{from:yyyy年M月}";
                MonthHeatTitle = $"{from:yyyy} 年 {from.Month} 月 · 每日使用热力图";
                RankingTitle = "本月应用排行";
                BuildHeatCells(from, to);
                BuildComparisonForMonth(from, screen);
            }

            // 单日模式下这几个标题不该残留周/月的内容——
            // 曾经切回单日时 RangeTitle 还显示"2026年10月"，
            // 因为单日分支没有更新它。这里显式收尾。
            if (Mode == RangeMode.Day)
            {
                RangeTitle = Anchor.Date.ToString("yyyy年M月d日 dddd");
                WeekChartTitle = "";
                MonthHeatTitle = "";
                RankingTitle = "应用排行";
                RankingHint = "与昨天对比";
            }

            SummaryText = $"屏幕前合计 {DayViewModel.Fmt(screen)}　·　" +
                          $"真正使用 {DayViewModel.Fmt(totals[UsageState.Active])}　·　" +
                          $"熄屏/锁屏 {DayViewModel.Fmt(totals[UsageState.Locked] + totals[UsageState.Off])}";

            if (Mode != RangeMode.Day)
            {
                RankingHint = Mode == RangeMode.Week ? "与上周对比" : "与上月对比";
            }
            BuildRanking(from, effectiveTo, totals[UsageState.Active]);
        }
        catch
        {
            // 刷新失败不影响采集，下一轮重试
        }
    }

    // ------------------------------------------------------------------
    // 周：每天一根柱子
    // ------------------------------------------------------------------
    private void BuildDayBars(DateTime from, DateTime to)
    {
        DayBars.Clear();

        var values = new List<(DateTime Day, long Screen)>();
        for (DateTime d = from; d <= to; d = d.AddDays(1))
        {
            if (d > DateTime.Today) { values.Add((d, 0)); continue; }
            DayTotals t = _store.GetDayTotals(d);
            values.Add((d, DayViewModel.ScreenOnOf(t)));
        }

        long max = values.Count > 0 ? values.Max(v => v.Screen) : 0;
        if (max <= 0) max = 1;

        string[] names = { "周一", "周二", "周三", "周四", "周五", "周六", "周日" };
        foreach ((DateTime day, long screen) in values)
        {
            bool isToday = day.Date == DateTime.Today;
            bool future = day > DateTime.Today;
            DayTotals t = future ? new DayTotals(0, 0, 0, 0, 0) : _store.GetDayTotals(day);

            DayBars.Add(new DayBarVm
            {
                Caption = names[MondayOffset(day)],
                AmountText = future ? "—" : DayViewModel.FmtShort(screen),
                HeightRatio = future ? 0 : Math.Clamp(screen / (double)max, 0.02, 1),
                IsToday = isToday,
                Tooltip = $"{day:M月d日} {names[MondayOffset(day)]}" +
                          (future ? "\n（未来）" :
                              $"\n屏幕前 {DayViewModel.Fmt(screen)}" +
                              $"\n真正使用 {DayViewModel.Fmt(t.ActiveSeconds)}" +
                              $"\n熄屏/锁屏 {DayViewModel.Fmt(t.LockedSeconds + t.OffSeconds)}"),
            });
        }
    }

    private void BuildComparisonForWeek(DateTime from, long screen)
    {
        DateTime prevFrom = from.AddDays(-7);
        DateTime prevTo = from.AddDays(-1);
        Dictionary<UsageState, long> prev = _store.GetStateTotalsRange(prevFrom, prevTo);
        long prevScreen = prev[UsageState.Active] + prev[UsageState.Idle] + prev[UsageState.Locked];

        ApplyComparison(screen, prevScreen, "上周");
    }

    private void BuildComparisonForMonth(DateTime from, long screen)
    {
        DateTime prevFrom = from.AddMonths(-1);
        DateTime prevTo = from.AddDays(-1);
        Dictionary<UsageState, long> prev = _store.GetStateTotalsRange(prevFrom, prevTo);
        long prevScreen = prev[UsageState.Active] + prev[UsageState.Idle] + prev[UsageState.Locked];

        ApplyComparison(screen, prevScreen, "上月");
    }

    /// <summary>
    /// 月视图要按"日均"比较：本月 30 天对上月 31 天，直接比总量会得出误导性结论。
    /// 周视图天数固定为 7，也可以用日均，口径统一。
    /// </summary>
    private void ApplyComparison(long screen, long prevScreen, string label)
    {
        (DateTime from, DateTime to) = CurrentRange();
        DateTime effectiveTo = to > DateTime.Today ? DateTime.Today : to;
        int days = Math.Max(1, (effectiveTo - from).Days + 1);

        DateTime prevFrom = label == "上周" ? from.AddDays(-7) : from.AddMonths(-1);
        int prevDays = Math.Max(1, (from - prevFrom).Days);

        if (prevScreen <= 0)
        {
            HasComparison = false;
            ComparisonText = $"无{label}数据";
            ComparisonBrush = Brushes.Gray;
            return;
        }

        double curAvg = screen / (double)days;
        double prevAvg = prevScreen / (double)prevDays;
        double diff = curAvg - prevAvg;

        HasComparison = true;
        // 日均用时变多是坏消息 → 涨用红色
        ComparisonText = diff >= 0
            ? $"日均比{label}多 {DayViewModel.FmtShort((long)Math.Abs(diff))}"
            : $"日均比{label}少 {DayViewModel.FmtShort((long)Math.Abs(diff))}";
        ComparisonBrush = new SolidColorBrush(diff > 0
            ? Color.FromRgb(0xD9, 0x53, 0x4F)
            : Color.FromRgb(0x2E, 0x9E, 0x6B));
    }

    // ------------------------------------------------------------------
    // 月：日历热力图
    // ------------------------------------------------------------------
    private void BuildHeatCells(DateTime from, DateTime to)
    {
        HeatCells.Clear();

        // 按天分桶：一次读出整月片段，在内存里聚合，避免 31 次查询
        DateTime rangeStart = from.AddDays(-MondayOffset(from));   // 补齐到周一
        long utcFrom = new DateTimeOffset(rangeStart, TimeZoneInfo.Local.GetUtcOffset(rangeStart)).ToUnixTimeSeconds();
        DateTime endEx = to.AddDays(1);
        long utcTo = new DateTimeOffset(endEx, TimeZoneInfo.Local.GetUtcOffset(endEx)).ToUnixTimeSeconds();
        List<(long Start, long End, int State, string Path, string Process, string Display)> raw =
            _store.GetRawSpans(utcFrom, utcTo);

        // 表头：日 一 二 三 四 五 六（周一开头）
        foreach (string h in new[] { "一", "二", "三", "四", "五", "六", "日" })
        {
            HeatCells.Add(new HeatCellVm { DayText = h, IsBlank = true, TextBrush = Res("Text3Brush") });
        }

        // 首日之前补空格，使日历对齐星期
        int pad = ((int)from.DayOfWeek + 6) % 7;
        for (int i = 0; i < pad; i++)
        {
            HeatCells.Add(new HeatCellVm { IsBlank = true, Fill = Brushes.Transparent });
        }

        // 逐日汇总（只统计屏幕前 = 活跃+空闲+锁屏）
        var perDay = new Dictionary<DateTime, long>();
        long max = 0;
        for (DateTime d = from; d <= to; d = d.AddDays(1))
        {
            long ds = new DateTimeOffset(d, TimeZoneInfo.Local.GetUtcOffset(d)).ToUnixTimeSeconds();
            long de = new DateTimeOffset(d.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(d.AddDays(1))).ToUnixTimeSeconds();

            long sum = 0;
            foreach ((long s, long e, int state, _, _, _) in raw)
            {
                if (state == (int)UsageState.Off) continue;   // 熄屏不计入"屏幕前"
                long a = Math.Max(s, ds), b = Math.Min(e, de);
                if (b > a) sum += b - a;
            }
            perDay[d] = sum;
            if (sum > max) max = sum;
        }
        if (max <= 0) max = 1;

        for (DateTime d = from; d <= to; d = d.AddDays(1))
        {
            long secs = perDay[d];
            double ratio = secs / (double)max;
            bool future = d > DateTime.Today;

            // 五档强度：用强调色按比例混合背景，越深代表用得越久
            Brush fill;
            Brush text;
            if (future || secs <= 0)
            {
                fill = Res("Surface3Brush");
                text = Res("Text3Brush");
            }
            else
            {
                double alpha = 0.18 + ratio * 0.72;
                Color accent = AppThemeAccent();
                fill = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), accent.R, accent.G, accent.B));
                text = alpha > 0.55 ? Brushes.White : Res("Text2Brush");
            }

            HeatCells.Add(new HeatCellVm
            {
                DayText = d.Day.ToString(),
                HourText = future || secs <= 0 ? "" : $"{secs / 3600.0:F1}h",
                Fill = fill,
                TextBrush = text,
                Tooltip = $"{d:yyyy年M月d日} {WeekdayCn(d)}\n" +
                          (future ? "（未来）" : $"屏幕前 {DayViewModel.Fmt(secs)}"),
            });
        }
    }

    // ------------------------------------------------------------------
    // 排行
    // ------------------------------------------------------------------
    private void BuildRanking(DateTime from, DateTime to, long activeSeconds)
    {
        Ranking.Clear();

        if (to < from) return;

        // 上周/上月同期，用于"变化"列
        int days = (to - from).Days + 1;
        DateTime prevFrom = Mode == RangeMode.Week ? from.AddDays(-7) : from.AddMonths(-1);
        DateTime prevTo = prevFrom.AddDays(days - 1);
        if (prevTo > DateTime.Today) prevTo = DateTime.Today;

        List<AppTotal> prev = prevTo >= prevFrom ? _store.GetAppTotalsRange(prevFrom, prevTo) : new List<AppTotal>();
        var prevMap = prev.ToDictionary(p => p.FilePath, p => p.Seconds, StringComparer.OrdinalIgnoreCase);

        List<AppTotal> list = _store.GetAppTotalsRange(from, to, 12);
        double max = list.Count > 0 ? Math.Max(1, list[0].Seconds) : 1;

        for (int i = 0; i < list.Count; i++)
        {
            AppTotal a = list[i];
            // 与其他视图一致：优先用图标主色，取不到才退回哈希调色板
            Color color = DayViewModel.ColorFor(a.FilePath);

            string deltaText = "";
            Brush deltaBrush = Brushes.Gray;
            if (prevMap.TryGetValue(a.FilePath, out long before))
            {
                long diff = a.Seconds - before;
                if (Math.Abs(diff) >= 60)
                {
                    deltaText = (diff > 0 ? "+" : "−") + DayViewModel.FmtShort(Math.Abs(diff));
                    deltaBrush = new SolidColorBrush(diff > 0
                        ? Color.FromRgb(0xD9, 0x53, 0x4F)
                        : Color.FromRgb(0x2E, 0x9E, 0x6B));
                }
                else deltaText = "—";
            }

            Ranking.Add(new AppRowVm
            {
                Rank = i + 1,
                Icon = AppIconCache.Get(a.FilePath, 32),
                Name = a.DisplayName,
                Category = AppPalette.CategoryOf(a.FilePath),
                DurationText = DayViewModel.FmtShort(a.Seconds),
                ShareText = activeSeconds > 0 ? $"{a.Seconds * 100.0 / activeSeconds:F1}%" : "—",
                BarRatio = Math.Clamp(a.Seconds / max, 0, 1),
                BarBrush = new SolidColorBrush(color),
                DeltaText = deltaText,
                DeltaBrush = deltaBrush,
                RankBrush = i switch
                {
                    0 => new SolidColorBrush(Color.FromRgb(0xE0, 0xA3, 0x0B)),
                    1 => new SolidColorBrush(Color.FromRgb(0x96, 0xA0, 0xAD)),
                    2 => new SolidColorBrush(Color.FromRgb(0xC0, 0x84, 0x57)),
                    _ => Brushes.Gray,
                },
            });
        }
    }

    // ------------------------------------------------------------------
    // 取色：跟随主题
    // ------------------------------------------------------------------
    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>当前主题的强调色，用于热力图。</summary>
    private static Color AppThemeAccent()
    {
        if (Application.Current?.TryFindResource("AccentBrush") is SolidColorBrush b) return b.Color;
        return Color.FromRgb(0x25, 0x63, 0xEB);
    }

    private static string WeekdayCn(DateTime d) => "星期" + "日一二三四五六"[(int)d.DayOfWeek];
}
