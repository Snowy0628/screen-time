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
/// 单日视图的数据源：从 UsageStore 读片段，转换成时间轴 / 分时柱状图 / 排行榜所需的元素。
///
/// 数据来源与统计口径：
///   * 时间轴与柱状图都用同一份 session 片段，按 UTC 区间裁剪后统计，因此跨天会话不会算错；
///   * "屏幕前"= 前台使用 + 空闲 + 锁屏（即屏幕亮着的总时长），熄屏/睡眠不计入；
///   * 排行榜的对比基准是前一天同一口径。
/// </summary>
internal sealed class DayViewModel
{
    private const string OtherKey = "其他";

    private readonly UsageStore _store;
    private readonly Func<DayTotals> _liveTotals;

    /// <summary>柱状图满格高度（像素）。</summary>
    /// <summary>
    /// 柱子满高（像素）。
    /// 容器 176px 减去小时标签（约 17px）与少量留白，柱身可用约 155，
    /// 取 150 让最高的柱子看起来饱满。
    /// </summary>
    private const double MaxBarHeight = 150;

    /// <summary>读取任意一天的原始片段。周/月视图复用，避免重复实现。</summary>
    public static List<Span> LoadSpans(UsageStore store, DateTime day)
    {
        (long s, long e, _) = UsageStore.LocalDayRange(day);
        var list = new List<Span>();
        foreach ((long start, long end, int state, string path, string process, string display) in
                 store.GetRawSpans(s, e))
        {
            AppIdentity app = path.Length > 0
                ? new AppIdentity(path, process, display)
                : AppIdentity.Unknown;
            list.Add(new Span(start, end, (UsageState)state, app));
        }
        return list;
    }

    /// <summary>某一天的总体统计（与主界面口径一致）。</summary>
    public static DayTotals TotalsOf(UsageStore store, DateTime day) => store.GetDayTotals(day);

    /// <summary>屏幕亮着总时长 = 前台使用 + 空闲 + 锁屏。</summary>
    public static long ScreenOnOf(DayTotals t) => t.ActiveSeconds + t.IdleSeconds + t.LockedSeconds;

    /// <summary>
    /// 把片段按 [bucketStartUtc, bucketEndUtc) 分桶聚合出各应用时长。
    /// 时间轴与柱状图都用这一份逻辑，保证两处口径一致。
    /// </summary>
    public static Dictionary<string, (double Seconds, string Display)> AggregateApps(
        IEnumerable<Span> spans, long bucketStartUtc, long bucketEndUtc)
    {
        var acc = new Dictionary<string, (double Seconds, string Display)>(StringComparer.OrdinalIgnoreCase);
        foreach (Span s in spans)
        {
            if (s.State != UsageState.Active) continue;
            long a = Math.Max(s.StartUtc, bucketStartUtc);
            long b = Math.Min(s.EndUtc, bucketEndUtc);
            if (b <= a) continue;

            string key = s.App.FilePath.Length > 0 ? s.App.FilePath : "~" + s.App.ProcessName;
            double secs = b - a;
            if (acc.TryGetValue(key, out var cur)) acc[key] = (cur.Seconds + secs, s.App.DisplayName);
            else acc[key] = (secs, s.App.DisplayName);
        }
        return acc;
    }

    /// <summary>本地某天的某个整点到下一个整点对应的 UTC 秒。</summary>
    public static long LocalHourToUtc(DateTime day, int hour)
    {
        DateTime local = day.Date.AddHours(hour);
        return new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local)).ToUnixTimeSeconds();
    }

    // ------------------------------------------------------------------
    // 显示窗口：起始小时（默认 0 点，即自然日）
    //
    // **默认必须是 0 点**：0 点才是一天的自然边界。若默认 6 点，
    // 横轴会覆盖 [当日06:00 → 次日06:00]，凌晨的使用被算进"前一天"，
    // "这一天用了多久"就说不清了。
    // 保留这个开关是为了留出调整余地（比如有人习惯以作息日为单位看数据），
    // 但默认值不改。
    // 注意：这只影响**显示**，数据仍按自然日查询。
    // ------------------------------------------------------------------

    /// <summary>时间轴/柱状图的起始小时（0–23），默认 0。</summary>
    public int ChartStartHour { get; set; }

    /// <summary>时间轴每格多少分钟（30 / 60 / 180）。</summary>
    public int TimelineBucketMinutes { get; set; } = 60;

    /// <summary>显示窗口的起点（UTC 秒）：所选日期当天的 ChartStartHour 点。</summary>
    public long WindowStartUtc => LocalHourToUtc(SelectedDate, ChartStartHour);

    /// <summary>显示窗口的终点（UTC 秒）：次日的 ChartStartHour 点，正好 24 小时。</summary>
    public long WindowEndUtc => LocalHourToUtc(SelectedDate.AddDays(1), ChartStartHour);

    /// <summary>
    /// 第 i 个显示小时对应的 UTC 区间（i = 0..23）。
    /// 例如起始 6 点时，i=0 是当天 06:00–07:00，i=18 是次日 00:00–01:00。
    /// </summary>
    public (long Start, long End, DateTime LocalDay) HourSlot(int i)
    {
        long start = LocalHourToUtc(SelectedDate, ChartStartHour + i);
        long end = LocalHourToUtc(SelectedDate, ChartStartHour + i + 1);
        // 跨过午夜后的时刻属于次日，用于标注
        var local = DateTimeOffset.FromUnixTimeSeconds(start).ToLocalTime().DateTime;
        return (start, end, local.Date);
    }

    public DateTime SelectedDate { get; private set; } = DateTime.Today;

    /// <summary>是否已是最新一天（不允许查看未来）。</summary>
    public bool IsToday => SelectedDate.Date >= DateTime.Today;

    public ObservableCollection<SegmentVm> Timeline { get; } = new();
    public ObservableCollection<HourBarVm> Hours { get; } = new();
    public ObservableCollection<AppRowVm> Ranking { get; } = new();

    // ---- 概览数字 ----
    public string TotalScreenText { get; private set; } = "0 分";
    public string DeltaVsYesterdayText { get; private set; } = "";
    public Brush DeltaBrush { get; private set; } = Brushes.Gray;
    public bool HasDelta { get; private set; }

    public string ActiveText { get; private set; } = "0 分";
    public string ActiveShareText { get; private set; } = "";
    public string OffText { get; private set; } = "0 分";
    public string TopAppText { get; private set; } = "—";
    public string TopAppFootText { get; private set; } = "";

    /// <summary>
    /// 活跃但无法归属到任何应用的时间，占前台使用总时长的比例。
    /// 正常情况下这个比例应该很低（前台是桌面或开始菜单时才会出现）。
    /// 如果它接近 100%，说明前台窗口解析失效——必须让用户看见，
    /// 而不是安静地显示一个空的排行榜。
    /// </summary>
    public string AttributionWarnText { get; private set; } = "";
    public bool ShowAttributionWarn { get; private set; }

    public string DateLabel { get; private set; } = "";
    public string LiveStatusText { get; private set; } = "";
    public Brush LiveStatusBrush { get; private set; } = Brushes.Gray;
    public string LiveAppText { get; private set; } = "";

    public DayViewModel(UsageStore store, Func<DayTotals> liveTotals)
    {
        _store = store;
        _liveTotals = liveTotals;
    }

    /// <summary>切换到相邻日期（n = -1 前一天，+1 后一天；不允许超过今天）。</summary>
    public void ShiftDay(int n)
    {
        DateTime next = SelectedDate.AddDays(n);
        if (next.Date > DateTime.Today) return;
        SelectedDate = next.Date;
        Refresh();
    }

    public void GoToday()
    {
        SelectedDate = DateTime.Today;
        Refresh();
    }

    /// <summary>
    /// 重新计算界面数据。由 UI 定时器调用。
    /// </summary>
    /// <param name="rebuildCollections">
    /// 是否重建时间轴与柱状图等集合。
    /// 这些集合的重建（含图标取色）比 KPI 计算贵得多，而落库间隔是 60 秒，
    /// 因此界面可以每秒刷新 KPI、但以更慢的节奏重建集合。
    /// </param>
    public void Refresh(bool rebuildCollections = true)
    {
        try
        {
            BuildSummary();
            if (rebuildCollections)
            {
                BuildTimeline();
                BuildHourBars();
            }
            BuildRanking();
        }
        catch
        {
            // 界面刷新失败不应影响采集；下一轮会重试
        }
    }

    /// <summary>由主窗口填充实时状态（当前前台应用、记录状态）。</summary>
    public void SetLive(UsageState state, string appName, bool paused)
    {
        if (paused)
        {
            LiveStatusText = "● 已暂停记录";
            LiveStatusBrush = new SolidColorBrush(Color.FromRgb(0xC8, 0x82, 0x00));
        }
        else
        {
            LiveStatusText = state switch
            {
                UsageState.Active => "● 正在记录 · 使用中",
                UsageState.Idle => "● 正在记录 · 空闲",
                UsageState.Locked => "● 正在记录 · 已锁屏",
                UsageState.Off => "● 正在记录 · 熄屏/睡眠",
                _ => "● 正在记录",
            };
            LiveStatusBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x6B));
        }
        LiveAppText = appName;
    }

    // ------------------------------------------------------------------
    // 概览
    // ------------------------------------------------------------------
    private void BuildSummary()
    {
        // 今天：用"库中数据 + 内存未落库片段"的实时值，否则刚启动的整分钟里全显示 0
        DayTotals t = SelectedDate.Date == DateTime.Today
            ? _liveTotals()
            : _store.GetDayTotals(SelectedDate);

        long active = t.ActiveSeconds;
        long screen = t.IdleSeconds + t.LockedSeconds + active;

        TotalScreenText = Fmt(screen);
        ActiveText = Fmt(active);
        ActiveShareText = screen > 0 ? $"占屏幕时间 {active * 100 / screen}%" : "占屏幕时间 —";
        OffText = Fmt(t.LockedSeconds + t.OffSeconds);

        // 排行仍然来自库（内存片段无法归属），但分母用实时值更贴近实际
        List<AppTotal> apps = _store.GetAppTotals(SelectedDate, 200);
        if (apps.Count > 0)
        {
            TopAppText = apps[0].DisplayName;
            int pct = screen > 0 ? (int)(apps[0].Seconds * 100 / screen) : 0;
            TopAppFootText = $"占屏幕时间 {pct}% · 共 {t.DistinctApps} 个应用";
        }
        else
        {
            TopAppText = "—";
            TopAppFootText = "暂无数据";
        }

        // 与昨天对比（同为"屏幕前"口径）
        DateTime yesterday = SelectedDate.AddDays(-1);
        DayTotals y = _store.GetDayTotals(yesterday);
        long yScreen = y.ActiveSeconds + y.IdleSeconds + y.LockedSeconds;
        HasDelta = yScreen > 0;
        if (HasDelta)
        {
            long diff = screen - yScreen;
            DeltaVsYesterdayText = diff >= 0 ? $"↑ {Fmt(Math.Abs(diff))}" : $"↓ {Fmt(Math.Abs(diff))}";
            // 用时变多是坏消息 → 涨用红色
            DeltaBrush = new SolidColorBrush(diff > 0
                ? Color.FromRgb(0xD9, 0x53, 0x4F)
                : Color.FromRgb(0x2E, 0x9E, 0x6B));
        }
        else
        {
            DeltaVsYesterdayText = "无昨日数据";
            DeltaBrush = Brushes.Gray;
        }

        DateLabel = $"{SelectedDate:yyyy年M月d日} {WeekdayCn(SelectedDate)}";
    }

    // ------------------------------------------------------------------
    // 时间轴
    // ------------------------------------------------------------------
    private void BuildTimeline()
    {
        Timeline.Clear();

        // 时间窗口：[所选日期 ChartStartHour 点 → 次日同一时刻]，共 24 小时。
        // 数据要同时覆盖窗口跨越的两个自然日，所以两个日期都取。
        long winStart = WindowStartUtc;
        long winEnd = WindowEndUtc;

        var spans = new List<Span>();
        spans.AddRange(LoadSpans(_store, SelectedDate));
        spans.AddRange(LoadSpans(_store, SelectedDate.AddDays(1)));
        spans.Sort((x, y) => x.StartUtc.CompareTo(y.StartUtc));

        // 不允许画出未来：今天只画到"现在"
        long nowUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long usableEnd = Math.Min(winEnd, Math.Max(winStart, nowUtc));
        if (usableEnd <= winStart) usableEnd = Math.Min(winEnd, winStart + 60);

        int bucketSec = Math.Max(300, TimelineBucketMinutes * 60);
        int totalBuckets = 24 * 3600 / bucketSec;

        for (int i = 0; i < totalBuckets; i++)
        {
            long bs = winStart + (long)i * bucketSec;
            long be = bs + bucketSec;

            // 完全落在未来：标为"今天还未到"
            if (bs >= usableEnd)
            {
                Timeline.Add(new SegmentVm
                {
                    Seconds = bucketSec,
                    Fill = Res("Surface3Brush"),
                    Tooltip = $"{Hm(bs)} – {Hm(be)}　还没到　{FmtShort(bucketSec)}",
                });
                continue;
            }

            // 已经开始的格子**按完整粒度计算**，不截断到"现在"。
            //
            // 否则当前这一格只有部分时长，各格加起来不到 24 小时
            // （自检里表现为"时间轴未覆盖完整一天，差值 -2552s"）。
            // 数据本身只到"现在"，格子里超出现在的部分自然没有记录，
            // 会被当作"无记录"呈现，符合事实。
            Timeline.Add(BuildBucket(bs, be, spans));
        }
    }

    /// <summary>
    /// 构造时间轴上的一个格子。
    ///
    /// 为什么按格子聚合而不是按原始片段画：原始片段能细到几秒，
    /// 一天下来是几十上百个色块，时间轴看起来像条形码，完全读不出规律。
    /// 按固定粒度（默认 1 小时）分格、每格用**主导应用**的颜色，
    /// 悬停再看该格内每个应用的时长明细——这样既有整体形态又有细节。
    /// </summary>
    private SegmentVm BuildBucket(long bs, long be, List<Span> spans)
    {
        long dur = be - bs;

        // 该格内各状态占用
        long active = 0, idle = 0, locked = 0, off = 0;
        var apps = new Dictionary<string, (double Seconds, string Display)>(StringComparer.OrdinalIgnoreCase);
        var appColor = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);

        foreach (Span s in spans)
        {
            long a = Math.Max(s.StartUtc, bs);
            long b = Math.Min(s.EndUtc, be);
            if (b <= a) continue;
            long d = b - a;

            switch (s.State)
            {
                case UsageState.Active:
                    active += d;
                    string key = s.App.FilePath.Length > 0 ? s.App.FilePath : "~" + s.App.ProcessName;
                    if (apps.TryGetValue(key, out var cur)) apps[key] = (cur.Seconds + d, s.App.DisplayName);
                    else apps[key] = (d, s.App.DisplayName);
                    if (s.App.FilePath.Length > 0 && !appColor.ContainsKey(key))
                        appColor[key] = ColorFor(s.App.FilePath);
                    break;
                case UsageState.Idle: idle += d; break;
                case UsageState.Locked: locked += d; break;
                default: off += d; break;
            }
        }

        // 没有任何记录：按"熄屏/睡眠"的浅灰填充（长时间）或"无记录"（短时间）
        if (active + idle + locked + off == 0)
        {
            return new SegmentVm
            {
                Seconds = dur,
                Fill = Res("Surface3Brush"),
                Tooltip = $"{Hm(bs)} – {Hm(be)}　无记录　{FmtShort(dur)}",
            };
        }

        // 主导状态：谁占比最大就用谁的颜色
        long maxState = Math.Max(Math.Max(active, idle), Math.Max(locked, off));
        Brush fill;
        string label;

        if (active == maxState)
        {
            // 主导应用：该格内时长最长的那个应用，用它的图标主色
            var top = apps.OrderByDescending(kv => kv.Value.Seconds).FirstOrDefault();
            if (top.Key is not null && appColor.TryGetValue(top.Key, out Color c))
            {
                fill = new SolidColorBrush(c);
            }
            else
            {
                fill = new SolidColorBrush(ColorFor(top.Key ?? ""));
            }
            label = top.Value.Display ?? "前台使用";
        }
        else if (idle == maxState) { fill = Brush("StateIdleBrush", Color.FromRgb(0xC3, 0xC8, 0xD1)); label = "空闲"; }
        else if (locked == maxState) { fill = Brush("StateLockedBrush", Color.FromRgb(0x8B, 0x93, 0xA3)); label = "锁屏"; }
        else { fill = Brush("StateOffBrush", Color.FromRgb(0xD7, 0xDA, 0xE0)); label = "熄屏 / 睡眠"; }

        // 悬停明细：起止时间 + 整格时长 + 各状态时长 + 该格内的应用排行
        var sb = new System.Text.StringBuilder();
        sb.Append($"{Hm(bs)} – {Hm(be)}　{FmtShort(dur)}");
        if (active > 0) sb.Append($"\n前台使用　{FmtShort(active)}");
        if (idle > 0) sb.Append($"\n空闲　{FmtShort(idle)}");
        if (locked > 0) sb.Append($"\n锁屏　{FmtShort(locked)}");
        if (off > 0) sb.Append($"\n熄屏 / 睡眠　{FmtShort(off)}");
        if (apps.Count > 0)
        {
            sb.Append("\n── 该时段应用 ──");
            foreach (var kv in apps.OrderByDescending(kv => kv.Value.Seconds).Take(6))
                sb.Append($"\n{kv.Value.Display}　{FmtShort((long)kv.Value.Seconds)}");
            if (apps.Count > 6) sb.Append($"\n其余 {apps.Count - 6} 个应用…");
        }
        else
        {
            sb.Append($"\n（{label}）");
        }

        return new SegmentVm { Seconds = dur, Fill = fill, Tooltip = sb.ToString() };
    }

    /// <summary>
    /// 没有记录覆盖的区间。
    ///
    /// 超过 <see cref="SleepGapMinutes"/> 分钟的空隙按「熄屏 / 睡眠」显示，
    /// 而不是「无记录」——关机、休眠、以及程序没在运行的时段本来就是这个含义，
    /// 标成"无记录"只会让人以为数据丢了。短空隙仍标"无记录"，避免把
    /// 采集抖动也伪装成睡眠。
    /// </summary>
    private SegmentVm GapSegment(long fromUtc, long toUtc)
    {
        long secs = toUtc - fromUtc;
        bool asSleep = secs >= SleepGapMinutes * 60;

        return new SegmentVm
        {
            Seconds = secs,
            Fill = asSleep ? Brush("StateOffBrush", Color.FromRgb(0xD7, 0xDA, 0xE0))
                           : Res("Surface3Brush"),
            Tooltip = asSleep
                ? $"{Hm(fromUtc)} – {Hm(toUtc)}　熄屏 / 睡眠（无记录）　{FmtShort(secs)}"
                : $"{Hm(fromUtc)} – {Hm(toUtc)}　无记录　{FmtShort(secs)}",
        };
    }

    /// <summary>
    /// 多长的无记录空隙算「熄屏 / 睡眠」。
    /// 5 分钟是个折中：比正常采集抖动（最多 2 分钟）长得多，
    /// 又足以覆盖真实的短暂待机。
    /// </summary>
    private const int SleepGapMinutes = 5;

    private static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gainsboro;

    private static Brush BrushFor(Span s)
    {
        // 活跃段用应用自身的颜色（与排行榜一致），其余用状态色
        if (s.State == UsageState.Active && s.App.FilePath.Length > 0)
        {
            return new SolidColorBrush(ColorFor(s.App.FilePath));
        }
        return s.State switch
        {
            UsageState.Idle => Brush("StateIdleBrush", Color.FromRgb(0xC3, 0xC8, 0xD1)),
            UsageState.Locked => Brush("StateLockedBrush", Color.FromRgb(0x8B, 0x93, 0xA3)),
            _ => Brush("StateOffBrush", Color.FromRgb(0xD7, 0xDA, 0xE0)),
        };
    }

    /// <summary>
    /// 应用的图表颜色：**优先取图标主色**，取不到才退回哈希调色板。
    ///
    /// 用图标本色（微信绿、QQ 蓝、网易云红）比哈希配色好得多：
    /// 用户能直接把色块和大脑里的应用形象对应起来；
    /// 哈希出来的颜色虽然稳定却与品牌无关，本质是"随机但一致"。
    /// </summary>
    internal static Color ColorFor(string exePath)
    {
        Color? fromIcon = AppIconCache.GetDominantColor(exePath);
        return fromIcon ?? AppPalette.ColorFor(exePath);
    }

    private static Brush Brush(string key, Color fallback)
    {
        if (Application.Current?.TryFindResource(key) is Brush b) return b;
        return new SolidColorBrush(fallback);
    }

    // ------------------------------------------------------------------
    // 分时柱状图（每根柱子 = 1 小时，共 24 根）
    //
    // 与时间轴用同一个显示窗口：从 ChartStartHour 点开始排 24 根，
    // 因此跨过午夜的后半段属于次日。
    // ------------------------------------------------------------------
    private void BuildHourBars()
    {
        Hours.Clear();

        // 窗口跨两个自然日，两侧数据都要取
        var spans = new List<Span>();
        spans.AddRange(LoadSpans(_store, SelectedDate));
        spans.AddRange(LoadSpans(_store, SelectedDate.AddDays(1)));
        spans.Sort((x, y) => x.StartUtc.CompareTo(y.StartUtc));

        for (int h = 0; h < 24; h++)
        {
            (long hourStart, long hourEnd, _) = HourSlot(h);

            // 标签用真实钟点；跨过午夜后属次日，用一个小标记区分
            int realHour = (ChartStartHour + h) % 24;
            string label = realHour.ToString("00");

            Dictionary<string, (double Seconds, string Display)> agg =
                AggregateApps(spans, hourStart, hourEnd);
            double active = agg.Values.Sum(v => v.Seconds);

            // 只保留前 5 个应用，其余合并为"其他"，否则柱子会被切得太碎
            List<KeyValuePair<string, (double Seconds, string Display)>> sorted =
                agg.OrderByDescending(kv => kv.Value.Seconds).ToList();

            var bar = new HourBarVm
            {
                HourLabel = label,
                BarHeight = Math.Min(active / 3600.0, 1.0) * MaxBarHeight,
                Tooltip = $"{Hm(hourStart)} – {Hm(hourEnd)}\n前台使用 {FmtShort((long)active)}",
            };

            if (active > 120)
            {
                // (显示名, 秒数, 路径)。路径为空表示"其他"聚合项。
                List<(string Name, double Secs, string Path)> show = new();
                if (sorted.Count > 5)
                {
                    double rest = sorted.Skip(5).Sum(x => x.Value.Seconds);
                    show.AddRange(sorted.Take(5).Select(x => (x.Value.Display, x.Value.Seconds, x.Key)));
                    if (rest > 0) show.Add((OtherKey, rest, ""));
                }
                else
                {
                    show.AddRange(sorted.Select(x => (x.Value.Display, x.Value.Seconds, x.Key)));
                }

                foreach ((string name, double secs, string path) in show)
                {
                    bar.Slices.Add(new SliceVm
                    {
                        AppName = name,
                        Seconds = secs,
                        Fill = path.Length > 0
                            ? new SolidColorBrush(ColorFor(path))
                            : Brush("StateLockedBrush", Color.FromRgb(0x8B, 0x93, 0xA3)),
                        Tooltip = $"{name}　{FmtShort((long)secs)}（占本时段 {secs * 100 / Math.Max(1, active):F0}%）",
                    });
                }

                // 柱顶图标已移除（模板层无法可靠渲染，见 MainWindow.xaml 的说明）。
                // Top3 应用信息仍然保留在悬停提示里，不丢信息。
                bar.Tooltip += "\n主要应用：" +
                    string.Join("　", sorted.Take(3).Select(x => $"{x.Value.Display} {FmtShort((long)x.Value.Seconds)}"));
            }

            Hours.Add(bar);
        }
    }

    // ------------------------------------------------------------------
    // 排行榜
    // ------------------------------------------------------------------
    private void BuildRanking()
    {
        Ranking.Clear();

        DayTotals t = SelectedDate.Date == DateTime.Today
            ? _liveTotals()
            : _store.GetDayTotals(SelectedDate);
        long active = t.ActiveSeconds;

        DayTotals y = _store.GetDayTotals(SelectedDate.AddDays(-1));
        List<AppTotal> prev = _store.GetAppTotals(SelectedDate.AddDays(-1), 200);
        var prevMap = prev.ToDictionary(p => p.FilePath, p => p.Seconds, StringComparer.OrdinalIgnoreCase);
        bool hasPrev = y.ActiveSeconds > 0;

        List<AppTotal> list = _store.GetAppTotals(SelectedDate, 12);
        double max = list.Count > 0 ? list[0].Seconds : 1;
        if (max <= 0) max = 1;

        // 归属率自检：活跃时长里有多少能被归到具体应用。
        // 正常应该接近 100%；偏低说明前台窗口解析没生效，用户需要知道，
        // 否则只会看到一个空排行榜而无从判断原因。
        double attributed = list.Sum(a => (double)a.Seconds);
        double ratio = active > 0 ? attributed / active : 1;
        if (active >= 60 && ratio < 0.5)
        {
            ShowAttributionWarn = true;
            AttributionWarnText =
                $"已记录 {Fmt(active)} 的前台使用时间，但其中 {Fmt((long)(active - attributed))} 无法归属到具体应用。" +
                "通常是前台窗口解析未能生效（例如以管理员权限运行、或运行在受限桌面会话中）。";
        }
        else
        {
            ShowAttributionWarn = false;
            AttributionWarnText = "";
        }

        for (int i = 0; i < list.Count; i++)
        {
            AppTotal a = list[i];
            Color color = AppPalette.ColorFor(a.FilePath);

            string deltaText = "";
            Brush deltaBrush = Brushes.Gray;
            if (hasPrev && prevMap.TryGetValue(a.FilePath, out long before))
            {
                long d = a.Seconds - before;
                if (Math.Abs(d) >= 60)
                {
                    deltaText = (d > 0 ? "+" : "−") + FmtShort(Math.Abs(d));
                    deltaBrush = new SolidColorBrush(d > 0
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
                DurationText = FmtShort(a.Seconds),
                ShareText = active > 0 ? $"{a.Seconds * 100.0 / active:F1}%" : "—",
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
    // 数据读取与工具
    // ------------------------------------------------------------------

    /// <summary>把过短的片段向前合并，避免时间轴出现大量 1px 噪声。</summary>
    private static List<Span> MergeShort(List<Span> spans, TimeSpan min)
    {
        var outList = new List<Span>(spans.Count);
        foreach (Span s in spans)
        {
            if (outList.Count > 0)
            {
                Span last = outList[^1];
                bool sameKind = last.State == s.State
                                && string.Equals(last.App.FilePath, s.App.FilePath, StringComparison.OrdinalIgnoreCase);
                if (sameKind && s.DurationSeconds < min.TotalSeconds)
                {
                    outList[^1] = last with { EndUtc = s.EndUtc };
                    continue;
                }
            }
            outList.Add(s);
        }
        return outList;
    }

    private static string Hm(long utc) =>
        DateTimeOffset.FromUnixTimeSeconds(utc).ToLocalTime().ToString("HH:mm");

    private static string WeekdayCn(DateTime d) =>
        "星期" + "日一二三四五六"[(int)d.DayOfWeek];

    private static string StateCn(UsageState s) => s switch
    {
        UsageState.Active => "前台使用",
        UsageState.Idle => "空闲",
        UsageState.Locked => "锁屏",
        _ => "熄屏 / 睡眠",
    };

    /// <summary>
    /// 概览卡用的时长格式。
    ///
    /// 规则：**不足 1 小时才显示秒**。已经到小时级时，秒位变化既挤占空间
    /// 又没什么信息量（"8 小时 14 分"比"8 小时 14 分 52 秒"更好读）。
    /// </summary>
    public static string Fmt(long seconds)
    {
        if (seconds < 0) seconds = 0;
        long h = seconds / 3600, m = seconds % 3600 / 60;
        if (h > 0) return $"{h} 小时 {m} 分";
        if (m > 0) return $"{m} 分 {seconds % 60} 秒";
        return $"{seconds} 秒";
    }

    /// <summary>
    /// 紧凑时长格式，用于时间轴/柱状图/排行榜等空间紧张的地方。
    ///
    /// 规则（**统一用中文单位，与概览卡的「3 小时 46 分」同一套**）：
    ///   · >= 1 小时 → "1时0分" / "3时46分"（不再写成 "1h 0m"，那个混着英文缩写很别扭）
    ///   · >= 1 分钟 → "60分" / "12分"
    ///   · &lt; 1 分钟 → "45秒"
    ///   · 真的为 0  → "0分"
    ///
    /// 「不足 1 分钟」不再显示成 "0m"——那看起来像"没有记录"，
    /// 而实际上该应用确实被用了 45 秒。
    /// </summary>
    public static string FmtShort(long seconds)
    {
        if (seconds < 0) seconds = 0;

        long h = seconds / 3600;
        long m = seconds % 3600 / 60;
        long s = seconds % 60;

        if (h > 0) return $"{h}时{m}分";
        if (m > 0) return $"{m}分";
        if (s > 0) return $"{s}秒";
        return "0分";
    }
}
