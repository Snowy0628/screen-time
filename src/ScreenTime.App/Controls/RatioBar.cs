using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

// 本项目同时启用 WPF 与 WinForms，两套命名空间都有 Brush/Color/Control，
// 因此统一用别名把 WPF 类型固定下来，避免逐处写全名。
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Control = System.Windows.Controls.Control;

namespace ScreenTime.App.Controls;

/// <summary>
/// 按比例填充的进度条。
///
/// 为什么不直接用两个 Border 加宽度绑定：那需要把 0-1 的比例换算成像素，
/// 只能靠 MultiBinding + 转换器把 ActualWidth 传进来，既脆弱又容易在
/// 布局未完成时算出 0 宽度。自绘版本只需要一个比例值，宽度由 WPF 在
/// 渲染时给出，天然正确。
/// </summary>
internal sealed class RatioBar : Control
{
    public static readonly DependencyProperty RatioProperty =
        DependencyProperty.Register(nameof(Ratio), typeof(double), typeof(RatioBar),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty =
        DependencyProperty.Register(nameof(BarBrush), typeof(Brush), typeof(RatioBar),
            new FrameworkPropertyMetadata(Brushes.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>填充比例，0-1（超出会被裁剪）。</summary>
    public double Ratio
    {
        get => (double)GetValue(RatioProperty);
        set => SetValue(RatioProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        double r = Math.Min(h / 2, 3);

        // 轨道
        Brush track = TryFindResource("Surface3Brush") as Brush
                      ?? new SolidColorBrush(Color.FromRgb(0xF0, 0xF1, 0xF4));
        dc.DrawRoundedRectangle(track, null, new Rect(0, 0, w, h), r, r);

        // 填充
        double ratio = Math.Clamp(Ratio, 0, 1);
        double fillW = w * ratio;
        if (fillW < 0.5) return;

        // 极窄时保持圆角不溢出
        if (fillW < h) fillW = Math.Min(h, w);

        dc.DrawRoundedRectangle(BarBrush, null, new Rect(0, 0, fillW, h), r, r);
    }
}
