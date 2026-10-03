using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using ScreenTime.Core;

namespace ScreenTime.App;

/// <summary>分类面板里"一个应用一行"。</summary>
internal sealed class AppCategoryRow : INotifyPropertyChanged
{
    private string _category;

    public AppCategoryRow(string filePath, string display, long seconds, string category,
                          bool isManual, ImageSource? icon)
    {
        FilePath = filePath;
        Display = display;
        Seconds = seconds;
        _category = category;
        IsManual = isManual;
        Icon = icon;
    }

    public string FilePath { get; }
    public string Display { get; }
    public long Seconds { get; }
    public ImageSource? Icon { get; }

    /// <summary>这一项的分类名是用户手动设的还是自动推断的。</summary>
    public bool IsManual { get; private set; }

    public string TimeText => DurationText.Short(Seconds);

    /// <summary>副标题：显示所在目录，方便区分同名 exe。</summary>
    public string PathHint
    {
        get
        {
            if (FilePath.Length == 0) return "（无路径信息）";
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(FilePath);
                return string.IsNullOrEmpty(dir) ? FilePath : dir;
            }
            catch
            {
                return FilePath;
            }
        }
    }

    /// <summary>当前分类。改动会触发界面刷新（下拉框回显）。</summary>
    public string Category
    {
        get => _category;
        set
        {
            if (_category == value) return;
            _category = value;
            IsManual = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ManualHint));
            OnPropertyChanged(nameof(CategoryBrush));
        }
    }

    /// <summary>
    /// 该应用所属分类的颜色，给行尾那个色点用。
    ///
    /// 颜色是**分类**的属性而不是应用的属性——所以同分类的所有应用
    /// 显示同一个色点。改它等于改分类色，饼图里那一块会跟着变。
    /// </summary>
    public Brush CategoryBrush
    {
        get
        {
            var b = new SolidColorBrush(Categories.ColorFor(_category));
            b.Freeze();
            return b;
        }
    }

    /// <summary>分类色被改动后通知色点换色。</summary>
    public void RefreshCategoryBrush() => OnPropertyChanged(nameof(CategoryBrush));

    /// <summary>手动设定的标记文本，让用户一眼看出哪些是自己改过的。</summary>
    public string ManualHint => IsManual ? "已手动指定" : "自动识别";

    /// <summary>把"手动"标记清掉（重置为自动推断时用）。</summary>
    public void MarkAuto(string inferred)
    {
        _category = inferred;
        IsManual = false;
        OnPropertyChanged(nameof(Category));
        OnPropertyChanged(nameof(IsManual));
        OnPropertyChanged(nameof(ManualHint));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>饼图图例的一行。</summary>
internal sealed class CategoryLegendRow
{
    public CategoryLegendRow(string name, long seconds, long total, Color color)
    {
        Name = name;
        Seconds = seconds;
        Color = color;
        TimeText = DurationText.Short(seconds);
        PercentText = DurationText.Percent(seconds, total);
        var b = new SolidColorBrush(color);
        b.Freeze();
        Brush = b;
    }

    public string Name { get; }
    public long Seconds { get; }
    public Color Color { get; }
    public Brush Brush { get; }
    public string TimeText { get; }
    public string PercentText { get; }
}

/// <summary>改色盘里的一个可选颜色。</summary>
internal sealed class CategoryColorOption
{
    public CategoryColorOption(string name, string hex)
    {
        Name = name;
        Hex = hex;
        var b = new SolidColorBrush(ThemeCustomizer.ParseHex(hex) ?? Colors.Gray);
        b.Freeze();
        Brush = b;
    }

    public string Name { get; }
    public string Hex { get; }
    public Brush Brush { get; }
}

/// <summary>
/// 应用分类面板的数据。
///
/// ### 面板结构（按用户要求：统计在上、自定义在下）
///
///   1. 周期切换（天 / 周 / 月）
///   2. 圆环图 + 图例      —— 各分类的时长占比
///   3. 新建分类 + 分类配色 —— 自己起名字、自己定颜色
///   4. 应用列表           —— 逐个改分类，带搜索
///
/// ### 统计周期
///
/// 天/周/月三种口径与主界面**同一套算法**（周从周一开始，月是自然月），
/// 这样面板上的数字和主界面排行的数字能对上——两处口径一旦不一致，
/// 用户会以为其中一个算错了。
///
/// 切换周期时**下方应用列表一起切**：列表里的时长同样按该周期算。
/// </summary>
internal sealed class CategoryViewModel : INotifyPropertyChanged
{
    private readonly UsageStore _store;

    public ObservableCollection<CategoryLegendRow> Legend { get; } = new();
    public ObservableCollection<AppCategoryRow> Apps { get; } = new();

    /// <summary>全部分类名，供下拉框使用。</summary>
    public ObservableCollection<string> AllCategories { get; } = new();

    private string _search = "";
    private string _totalText = "0分";
    private string _summaryText = "";
    private string _totalCaption = "今日合计";
    private int _manualCount;
    private RangeMode _rangeMode = RangeMode.Day;
    private AppCategoryRow? _selectedAppRow;

    public CategoryViewModel(UsageStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 要改颜色的分类名。
    ///
    /// 两个入口共用它：点分类色块（改分类本身的颜色）、点应用行旁边的色点
    /// （改该应用所属分类的颜色）。**两者本质是同一件事**——颜色是属性，
    /// 挂在分类上而不是单个应用上；应用行的色点只是"就近修改所属分类"的快捷方式。
    /// </summary>
    public string? ColorTarget
    {
        get => _selectedAppRow?.Category;
        private set
        {
            _colorTarget = value;
            OnPropertyChanged();
        }
    }
    private string? _colorTarget;


    /// <summary>从应用行点进来（改该应用所属分类的颜色）。</summary>
    public void SelectAppForColor(AppCategoryRow row)
    {
        _selectedAppRow = row;
        ColorTarget = row.Category;
    }

    /// <summary>当前选中分类的颜色（供取色器顶部显示"现在是什么颜色"）。</summary>
    public Color CurrentTargetColor =>
        ColorTarget is { Length: > 0 } name ? Categories.ColorFor(name) : Colors.Gray;


    /// <summary>统计周期，与主界面共用 RangeMode。</summary>
    public RangeMode RangeMode
    {
        get => _rangeMode;
        set
        {
            if (_rangeMode == value) return;
            _rangeMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDay));
            OnPropertyChanged(nameof(IsWeek));
            OnPropertyChanged(nameof(IsMonth));
            Refresh();
        }
    }

    public bool IsDay => _rangeMode == RangeMode.Day;
    public bool IsWeek => _rangeMode == RangeMode.Week;
    public bool IsMonth => _rangeMode == RangeMode.Month;

    /// <summary>取色器顶部的说明，例如「正在改『游戏』的颜色」。</summary>
    public string ColorTargetText => ColorTarget is { Length: > 0 } n
        ? $"正在改「{n}」的颜色"
        : "点一个色点来改颜色";

    public string Search
    {
        get => _search;
        set { if (_search != value) { _search = value; OnPropertyChanged(); RefreshList(); } }
    }

    /// <summary>圆环中间显示的总时长。</summary>
    public string TotalText
    {
        get => _totalText;
        private set { _totalText = value; OnPropertyChanged(); }
    }

    /// <summary>圆环中间那行小字（今日合计 / 本周合计 / 本月合计）。</summary>
    public string TotalCaption
    {
        get => _totalCaption;
        private set { _totalCaption = value; OnPropertyChanged(); }
    }

    /// <summary>图例上方的说明，例如"今天 共 5时34分 · 15 个应用"。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set { _summaryText = value; OnPropertyChanged(); }
    }

    /// <summary>手动指定过的应用数量。</summary>
    public int ManualCount
    {
        get => _manualCount;
        private set { _manualCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManualHintText)); }
    }

    public string ManualHintText => _manualCount > 0
        ? $"已手动指定 {_manualCount} 个应用"
        : "全部按关键字自动识别";

    /// <summary>
    /// 当前周期对应的日期区间。
    ///
    /// 与 RangeViewModel 用**同一套算法**：周从周一开始、月是自然月。
    /// </summary>
    public (DateTime From, DateTime To) CurrentRange()
    {
        DateTime anchor = DateTime.Today;
        return _rangeMode switch
        {
            RangeMode.Week => (WeekStart(anchor), WeekStart(anchor).AddDays(6)),
            RangeMode.Month => (new DateTime(anchor.Year, anchor.Month, 1),
                                new DateTime(anchor.Year, anchor.Month, 1).AddMonths(1).AddDays(-1)),
            _ => (anchor, anchor),
        };
    }

    private static DateTime WeekStart(DateTime d)
        => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>按当前周期查应用汇总。</summary>
    private List<AppTotal> Query()
    {
        (DateTime from, DateTime to) = CurrentRange();
        return from == to
            ? _store.GetAppTotals(from, 500)
            : _store.GetAppTotalsRange(from, to, 500);
    }

    /// <summary>把当前数据打包给圆环图控件。</summary>
    public List<DonutChart.Slice> BuildSlices()
    {
        var list = new List<DonutChart.Slice>();
        foreach (CategoryLegendRow r in Legend)
            list.Add(new DonutChart.Slice(r.Name, r.Seconds, r.Color));
        return list;
    }

    /// <summary>
    /// 重新载入分类清单（新建/删除分类后调用）。
    ///
    /// 只重建下拉框用的名字列表。颜色不在这里维护——改色入口是应用行尾的
    /// 色点，颜色按分类名现取（<see cref="Categories.ColorFor"/>），
    /// 不需要一份"色块对象"来保存状态。
    /// </summary>
    public void ReloadCategories()
    {
        AllCategories.Clear();
        foreach (string c in Categories.All()) AllCategories.Add(c);
    }

    /// <summary>全量刷新：重算饼图、重建应用列表。</summary>
    public void Refresh()
    {
        ReloadCategories();

        List<AppTotal> totals = Query();

        // ---- 按分类聚合 ----
        var byCat = new Dictionary<string, long>(StringComparer.Ordinal);
        long grand = 0;
        foreach (AppTotal a in totals)
        {
            string cat = AppPalette.CategoryOf(a.FilePath);
            byCat.TryGetValue(cat, out long cur);
            byCat[cat] = cur + a.Seconds;
            grand += a.Seconds;
        }

        TotalText = DurationText.Short(grand);
        TotalCaption = _rangeMode switch
        {
            RangeMode.Week => "本周合计",
            RangeMode.Month => "本月合计",
            _ => "今日合计",
        };
        SummaryText = grand > 0
            ? $"{RangeLabel()}共 {DurationText.Short(grand)} · {totals.Count} 个应用"
            : $"{RangeLabel()}还没有记录";

        FillLegend(byCat, grand);
        RefreshList();
    }

    private string RangeLabel() => _rangeMode switch
    {
        RangeMode.Week => "本周 ",
        RangeMode.Month => "本月 ",
        _ => "今天 ",
    };

    /// <summary>按时长降序填图例。时长为 0 的分类不显示，否则会堆一堆 0%。</summary>
    private void FillLegend(Dictionary<string, long> byCat, long grand)
    {
        Legend.Clear();
        foreach (KeyValuePair<string, long> kv in byCat.OrderByDescending(k => k.Value))
        {
            if (kv.Value <= 0) continue;
            Legend.Add(new CategoryLegendRow(kv.Key, kv.Value, grand, Categories.ColorFor(kv.Key)));
        }
    }

    /// <summary>只重建应用列表（搜索条件变化、单个应用改分类后调用）。</summary>
    private void RefreshList()
    {
        List<AppTotal> totals = Query();
        Dictionary<string, string> manual = _store.GetAppCategories();

        string q = _search.Trim();
        Apps.Clear();

        foreach (AppTotal a in totals)
        {
            if (q.Length > 0)
            {
                bool hit = a.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
                        || a.FilePath.Contains(q, StringComparison.OrdinalIgnoreCase);
                if (!hit) continue;
            }

            bool isManual = a.FilePath.Length > 0 && manual.ContainsKey(a.FilePath);
            string cat = isManual ? manual[a.FilePath] : AppPalette.InferCategory(a.FilePath);

            Apps.Add(new AppCategoryRow(
                a.FilePath, a.DisplayName, a.Seconds, cat, isManual,
                AppIconCache.Get(a.FilePath)));
        }

        // 手动数量按**全量**统计，不受搜索过滤影响，否则一搜就变少会让人困惑
        ManualCount = manual.Count;
    }

    /// <summary>改一个应用的分类。传空表示恢复自动推断。</summary>
    public void SetCategory(AppCategoryRow row, string category)
    {
        if (row.FilePath.Length == 0) return;

        if (string.IsNullOrWhiteSpace(category))
        {
            _store.SetAppCategory(row.FilePath, "");
            row.MarkAuto(AppPalette.InferCategory(row.FilePath));
        }
        else
        {
            _store.SetAppCategory(row.FilePath, category);
            row.Category = category;
        }

        // 分类变了，手动表要重新载入，否则排行/图例仍按旧分类显示
        AppPalette.ReloadManualCategories(_store);

        // 饼图跟着变——即时反馈让用户知道改动生效了
        RefreshLegendOnly();
        ManualCount = _store.GetAppCategories().Count;
    }

    /// <summary>给某个分类设定颜色。传空恢复默认色。</summary>
    public void SetCategoryColor(string category, string hex)
    {
        if (string.IsNullOrWhiteSpace(category)) return;

        Categories.SetCustomColor(category, hex);

        // 两处要跟着换色：应用行尾的色点、饼图图例
        RefreshAppRowBrushes(category);
        RefreshLegendOnly();

        OnPropertyChanged(nameof(ColorTargetText));
        OnPropertyChanged(nameof(CurrentTargetColor));
    }

    /// <summary>某个分类的颜色变了，把属于它的应用行的色点刷新一遍。</summary>
    private void RefreshAppRowBrushes(string category)
    {
        foreach (AppCategoryRow row in Apps)
        {
            if (string.Equals(row.Category, category, StringComparison.Ordinal))
                row.RefreshCategoryBrush();
        }
    }

    /// <summary>只重算饼图与图例（应用列表没变时用，避免列表滚动位置丢失）。</summary>
    private void RefreshLegendOnly()
    {
        var byCat = new Dictionary<string, long>(StringComparer.Ordinal);
        long grand = 0;
        foreach (AppTotal a in Query())
        {
            string cat = AppPalette.CategoryOf(a.FilePath);
            byCat.TryGetValue(cat, out long cur);
            byCat[cat] = cur + a.Seconds;
            grand += a.Seconds;
        }

        TotalText = DurationText.Short(grand);
        FillLegend(byCat, grand);
    }

    /// <summary>清空全部手动分类。</summary>
    public void ResetAll()
    {
        _store.ClearAppCategories();
        AppPalette.ReloadManualCategories(_store);
        Refresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
