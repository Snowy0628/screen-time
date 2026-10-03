using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ScreenTime.App;

/// <summary>
/// 圆环图（饼图中间挖空）。
///
/// ### 为什么自己画而不是用现成控件
///
/// 要显示的东西很简单（一组"名称 + 数值 + 颜色"），但有两个硬要求：
///   · 中间要放得下总时长文字 —— 圆环比实心饼图更省地方
///   · 扇区之间要有细微间隙，否则同色系相邻时边界糊在一起
/// 现成控件大多不满足第二点，而且为了这么点东西引一个依赖不值。
///
/// ### 入场动画
///
/// 切换统计周期时，扇区从 12 点方向**顺时针扫出**，像被"画"上去一样。
/// 实现方式不是逐个扇区淡入——那样看不出顺序。而是引入一个"扫出进度"：
///
///   Progress = 0.0  什么都没画
///   Progress = 0.5  画到圆周一半
///   Progress = 1.0  完整圆环
///
/// 绘制时每个扇区按自己的角度区间与进度取交集，超出部分直接不画。
/// 这样无论是一个大扇区还是十几个小扇区，看起来都是连续扫过一整圈。
///
/// 用 <see cref="FrameworkPropertyMetadataOptions.AffectsRender"/> 让属性变化
/// 自动触发重绘，不需要手动调 InvalidateVisual。
/// </summary>
internal sealed class DonutChart : FrameworkElement
{
    /// <summary>一段数据。</summary>
    internal sealed record Slice(string Label, long Seconds, Color Fill);

    private List<Slice> _slices = new();
    private long _total;

    /// <summary>环的厚度占半径的比例。0.34 视觉上比较接近常见图表的观感。</summary>
    private const double ThicknessRatio = 0.34;

    /// <summary>
    /// 扫出进度（0–1）。动画驱动它，用 AffectsRender 自动重绘。
    /// </summary>
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(
            nameof(Progress), typeof(double), typeof(DonutChart),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public void SetData(IReadOnlyList<Slice> slices)
    {
        _slices = new List<Slice>(slices);
        _total = 0;
        foreach (Slice s in _slices) _total += s.Seconds;
        InvalidateVisual();
    }

    /// <summary>
    /// 从当前进度重新扫一遍。
    ///
    /// 动效关闭时直接落到 1.0（不做无谓的动画），符合"低配/远程桌面"的意图。
    /// </summary>
    public void PlaySweep(int milliseconds = 620)
    {
        if (!AppSettings.Current.EnableAnimations)
        {
            BeginAnimation(ProgressProperty, null);
            Progress = 1.0;
            return;
        }

        var anim = new DoubleAnimation
        {
            From = 0.0,
            To = 1.0,
            Duration = TimeSpan.FromMilliseconds(milliseconds),
            // 缓出：起手快、收尾慢，看起来像"扫过去停住"而不是匀速转
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(ProgressProperty, anim);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        var center = new Point(w / 2, h / 2);
        double radius = Math.Min(w, h) / 2 - 2;
        if (radius <= 2) return;
        double thickness = radius * ThicknessRatio;
        double midR = radius - thickness / 2;
        double progress = Math.Clamp(Progress, 0, 1);

        // 没有任何数据：画一个空环，避免整块空白看不出是图表。
        // 空环不参与扫出动画——没有扇区可扫，让它始终可见更合理。
        if (_total <= 0)
        {
            var emptyPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)), thickness);
            emptyPen.Freeze();
            dc.DrawEllipse(null, emptyPen, center, midR, midR);
            return;
        }

        if (progress <= 0) return;

        // 单项占满时 ArcSegment 画不出来（起止点重合），单独处理
        int nonZero = 0;
        Slice? only = null;
        foreach (Slice s in _slices)
        {
            if (s.Seconds > 0) { nonZero++; only = s; }
        }

        if (nonZero == 1 && only is not null)
        {
            // 只有一项：随着进度把整圈画出来
            double sweep = 360.0 * progress;
            if (sweep >= 359.99)
            {
                var pen = new Pen(new SolidColorBrush(only.Fill), thickness);
                pen.Freeze();
                dc.DrawEllipse(null, pen, center, midR, midR);
            }
            else
            {
                DrawArc(dc, center, midR, thickness, -90.0, sweep, only.Fill);
            }
            return;
        }

        // 扫出的终止角度：从 12 点方向顺时针走 progress 圈
        double endAngle = -90.0 + 360.0 * progress;

        double angle = -90.0;   // 从 12 点方向开始，顺时针
        foreach (Slice s in _slices)
        {
            if (s.Seconds <= 0) continue;

            double full = 360.0 * s.Seconds / _total;
            double sliceStart = angle;
            double sliceEnd = angle + full;
            angle = sliceEnd;

            // 这一段还没扫到
            if (sliceStart >= endAngle) break;

            // 扫到一半的扇区只画已扫到的部分
            double visibleEnd = Math.Min(sliceEnd, endAngle);
            double visibleSweep = visibleEnd - sliceStart;
            if (visibleSweep <= 0) continue;

            DrawArc(dc, center, midR, thickness, sliceStart, visibleSweep, s.Fill);
        }
    }

    /// <summary>画一段圆环。用两个圆弧拼出"有厚度的扇区"，闭合后填充。</summary>
    private static void DrawArc(DrawingContext dc, Point c, double midR, double thickness,
                                double startAngle, double sweep, Color color)
    {
        if (sweep <= 0) return;

        // 扇区之间留一点缝：同色系相邻时靠这个缝才分得清边界。
        // 缝太大会显得碎，1.2 度是肉眼刚好能看出、又不影响占比观感的程度。
        // 扫出动画进行中不留缝（正在"画"的过程中留缝会看出断口），
        // 只有完整扇区才留。
        const double GapDegrees = 1.2;
        double s = startAngle;
        double e = startAngle + sweep;
        if (sweep < 359.9)
        {
            s = startAngle + GapDegrees / 2;
            e = startAngle + sweep - GapDegrees / 2;
            if (e <= s) { s = startAngle; e = startAngle + sweep; }   // 太窄就不留缝了
        }

        double outer = midR + thickness / 2;
        double inner = midR - thickness / 2;

        Point p1 = PointOnCircle(c, outer, s);
        Point p2 = PointOnCircle(c, outer, e);
        Point p3 = PointOnCircle(c, inner, e);
        Point p4 = PointOnCircle(c, inner, s);

        var geo = new StreamGeometry();
        using (StreamGeometryContext ctx = geo.Open())
        {
            ctx.BeginFigure(p1, isFilled: true, isClosed: true);
            ctx.ArcTo(p2, new Size(outer, outer), 0, e - s > 180, SweepDirection.Clockwise, true, false);
            ctx.LineTo(p3, true, false);
            ctx.ArcTo(p4, new Size(inner, inner), 0, e - s > 180, SweepDirection.Counterclockwise, true, false);
        }
        geo.Freeze();

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        dc.DrawGeometry(brush, null, geo);
    }

    private static Point PointOnCircle(Point c, double r, double angleDegrees)
    {
        double rad = angleDegrees * Math.PI / 180.0;
        return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad));
    }
}

/// <summary>把秒数格式化成"1时20分"这类短文本，供图例与统计使用。</summary>
internal static class DurationText
{
    public static string Short(long seconds)
    {
        if (seconds <= 0) return "0分";
        long h = seconds / 3600;
        long m = seconds % 3600 / 60;

        if (h > 0) return m > 0 ? $"{h}时{m}分" : $"{h}时";
        if (m > 0) return $"{m}分";
        return $"{seconds}秒";
    }

    /// <summary>占比文本，例如 "38.7%"。</summary>
    public static string Percent(long part, long whole)
    {
        if (whole <= 0) return "—";
        double p = 100.0 * part / whole;
        return p.ToString(p >= 10 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + "%";
    }
}
