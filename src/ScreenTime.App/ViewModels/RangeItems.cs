using System.Collections.ObjectModel;
using System.Windows.Media;

// WPF 与 WinForms 都有 Brush/Color，固定用 WPF 的
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;

namespace ScreenTime.App;

/// <summary>视图范围。</summary>
internal enum RangeMode
{
    Day,
    Week,
    Month,
}

/// <summary>周视图里的一根日柱。</summary>
internal sealed class DayBarVm
{
    public string Caption { get; init; } = "";      // 周一 / 周二 …
    public string AmountText { get; init; } = "";   // 8h 42m
    public double HeightRatio { get; init; }        // 0-1，相对本周最大值
    public bool IsToday { get; init; }
    public string Tooltip { get; init; } = "";
}

/// <summary>月视图热力图的单元格。</summary>
internal sealed class HeatCellVm
{
    public string DayText { get; init; } = "";
    public string HourText { get; init; } = "";
    public Brush Fill { get; init; } = Brushes.Transparent;
    public Brush TextBrush { get; init; } = Brushes.Gray;
    public string Tooltip { get; init; } = "";
    public bool IsBlank { get; init; }
}
