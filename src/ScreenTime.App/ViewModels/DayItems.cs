using System.Collections.ObjectModel;
using System.Windows.Media;

// WPF 与 WinForms 都有 Brush，固定用 WPF 的
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;

namespace ScreenTime.App;

/// <summary>时间轴上的一个色块。持续时长用 Grid 的星号列宽按比例分配。</summary>
internal sealed class SegmentVm
{
    public double Seconds { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
    public string Tooltip { get; init; } = "";
}

/// <summary>分时柱状图里的一段（某个应用在该小时的占比）。</summary>
internal sealed class SliceVm
{
    public string AppName { get; init; } = "";
    public double Seconds { get; init; }
    public Brush Fill { get; init; } = Brushes.Gray;
    public string Tooltip { get; init; } = "";
}

/// <summary>分时柱状图的一根柱子（= 1 小时）。</summary>
internal sealed class HourBarVm
{
    /// <summary>柱高（像素）。由该小时的前台使用秒数换算。</summary>
    public double BarHeight { get; init; }

    /// <summary>柱高占满格的比例，用于无数据时隐藏。</summary>
    public bool HasData => BarHeight > 0.5;

    /// <summary>小时标签，如 "14"。</summary>
    public string HourLabel { get; init; } = "";

    /// <summary>悬停提示。构建过程中会逐步追加内容，因此不是 init-only。</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>柱内堆叠的各应用占比（顺序与视觉自上而下一致）。</summary>
    public ObservableCollection<SliceVm> Slices { get; } = new();

    /// <summary>
    /// 柱顶展示的 Top3 应用图标。
    /// 当前界面未使用（模板层无法可靠渲染这一行，详见 MainWindow.xaml 的说明），
    /// 字段保留以便日后重做；Top3 信息目前通过悬停提示呈现。
    /// </summary>
    public ObservableCollection<ImageSource> TopIcons { get; } = new();
}

/// <summary>排行榜一行。</summary>
internal sealed class AppRowVm
{
    public int Rank { get; init; }
    public ImageSource? Icon { get; init; }
    public string Name { get; init; } = "";
    public string Category { get; init; } = "";
    public string DurationText { get; init; } = "";
    public string ShareText { get; init; } = "";

    /// <summary>占比条的宽度比例（0-1，相对于第一名的时长）。</summary>
    public double BarRatio { get; init; }
    public Brush BarBrush { get; init; } = Brushes.Gray;

    /// <summary>与昨天的差值文本；无对比数据时为空。</summary>
    public string DeltaText { get; init; } = "";
    public Brush DeltaBrush { get; init; } = Brushes.Gray;

    /// <summary>前三名的名次高亮色。</summary>
    public Brush RankBrush { get; init; } = Brushes.Gray;
}
