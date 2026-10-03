using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
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

    /// <summary>副标题：显示路径尾部，方便区分同名 exe。</summary>
    public string PathHint
    {
        get
        {
            if (FilePath.Length == 0) return "（无路径信息）";
            try
            {
                string? dir = System.IO.Path.GetDirectoryName(FilePath);
                if (string.IsNullOrEmpty(dir)) return FilePath;
                return dir;
            }
            catch
            {
                return FilePath;
            }
        }
    }

    /// <summary>当前分类。改动会触发界面刷新（下拉框回显、饼图重算由外层负责）。</summary>
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
        }
    }

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
        Brush = new SolidColorBrush(color);
        Brush.Freeze();
    }

    public string Name { get; }
    public long Seconds { get; }
    public Color Color { get; }
    public Brush Brush { get; }
    public string TimeText { get; }
    public string PercentText { get; }
}

/// <summary>
/// 应用分类面板的数据。
///
/// ### 面板结构（按用户要求：统计在上、自定义在下）
///
///   1. 圆环图 + 图例      —— 各分类的时长占比
///   2. 新建分类           —— 自己起名字
///   3. 应用列表           —— 逐个改分类，带搜索
///
/// ### 统计口径
///
/// 用的是"今天"的单日数据（`GetAppTotals`）。用户没指定周期，
/// 而单日数据在采集过程中是实时更新的，改完分类立刻能看到饼图变化——
/// 这个即时反馈比"选周期再看"更有用。
/// </summary>
internal sealed class CategoryViewModel : INotifyPropertyChanged
{
    private readonly UsageStore _store;

    public ObservableCollection<CategoryLegendRow> Legend { get; } = new();
    public ObservableCollection<AppCategoryRow> Apps { get; } = new();

    /// <summary>全部分类名，供下拉框与"新建"使用。</summary>
    public ObservableCollection<string> AllCategories { get; } = new();

    private string _search = "";
    private long _totalSeconds;
    private string _totalText = "0分";
    private string _summaryText = "";
    private int _manualCount;

    public CategoryViewModel(UsageStore store)
    {
        _store = store;
    }

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

    /// <summary>图例上方的说明，例如"共 5 小时 34 分 · 15 个应用"。</summary>
    public string SummaryText
    {
        get => _summaryText;
        private set { _summaryText = value; OnPropertyChanged(); }
    }

    /// <summary>手动指定过的应用数量，用于提示"重置"按钮的意义。</summary>
    public int ManualCount
    {
        get => _manualCount;
        private set { _manualCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ManualHintText)); }
    }

    public string ManualHintText => _manualCount > 0
        ? $"已手动指定 {_manualCount} 个应用"
        : "全部按关键字自动识别";

    /// <summary>把当前数据打包给圆环图控件。</summary>
    public List<DonutChart.Slice> BuildSlices()
    {
        var list = new List<DonutChart.Slice>();
        foreach (CategoryLegendRow r in Legend)
            list.Add(new DonutChart.Slice(r.Name, r.Seconds, r.Color));
        return list;
    }

    /// <summary>重新载入分类清单（新建/删除分类后调用）。</summary>
    public void ReloadCategories()
    {
        AllCategories.Clear();
        foreach (string c in Categories.All()) AllCategories.Add(c);
    }

    /// <summary>全量刷新：重算饼图、重建应用列表。</summary>
    public void Refresh()
    {
        ReloadCategories();

        List<AppTotal> totals = _store.GetAppTotals(DateTime.Today, 500);

        // ---- 饼图：按分类聚合 ----
        var byCat = new Dictionary<string, long>(StringComparer.Ordinal);
        long grand = 0;
        foreach (AppTotal a in totals)
        {
            string cat = AppPalette.CategoryOf(a.FilePath);
            byCat.TryGetValue(cat, out long cur);
            byCat[cat] = cur + a.Seconds;
            grand += a.Seconds;
        }

        _totalSeconds = grand;
        TotalText = DurationText.Short(grand);
        SummaryText = grand > 0
            ? $"今天共 {DurationText.Short(grand)} · {totals.Count} 个应用"
            : "今天还没有记录";

        Legend.Clear();
        // 按时长降序，"其他"自然排到最后；时长为 0 的分类不显示，
        // 否则图例里会堆一堆 0% 的项，看不出重点
        foreach (KeyValuePair<string, long> kv in byCat.OrderByDescending(k => k.Value))
        {
            if (kv.Value <= 0) continue;
            Legend.Add(new CategoryLegendRow(kv.Key, kv.Value, grand, Categories.ColorOf(kv.Key)));
        }

        RefreshList();
    }

    /// <summary>只重建应用列表（搜索条件变化、单个应用改分类后调用）。</summary>
    private void RefreshList()
    {
        List<AppTotal> totals = _store.GetAppTotals(DateTime.Today, 500);
        Dictionary<string, string> manual = _store.GetAppCategories();

        string q = _search.Trim();
        Apps.Clear();
        int manualCount = 0;

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
            if (isManual) manualCount++;

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

    /// <summary>只重算饼图与图例（应用列表没变时用，避免列表滚动位置丢失）。</summary>
    private void RefreshLegendOnly()
    {
        var byCat = new Dictionary<string, long>(StringComparer.Ordinal);
        long grand = 0;
        foreach (AppTotal a in _store.GetAppTotals(DateTime.Today, 500))
        {
            string cat = AppPalette.CategoryOf(a.FilePath);
            byCat.TryGetValue(cat, out long cur);
            byCat[cat] = cur + a.Seconds;
            grand += a.Seconds;
        }

        _totalSeconds = grand;
        TotalText = DurationText.Short(grand);

        Legend.Clear();
        foreach (KeyValuePair<string, long> kv in byCat.OrderByDescending(k => k.Value))
        {
            if (kv.Value <= 0) continue;
            Legend.Add(new CategoryLegendRow(kv.Key, kv.Value, grand, Categories.ColorOf(kv.Key)));
        }
    }

    /// <summary>清空全部手动分类。</summary>
    public void ResetAll()
    {
        _store.ClearAppCategories();
        AppPalette.ReloadManualCategories(_store);
        Refresh();
    }

    public long TotalSeconds => _totalSeconds;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
