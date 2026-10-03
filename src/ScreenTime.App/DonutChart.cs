using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

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
/// ### 画法
///
/// 用 `Path` + `ArcSegment` 拼扇形。**整圆要特别处理**：只有一项占 100% 时，
/// 起止点重合，`ArcSegment` 会退化成画不出东西——这时改画两个半圆。
///
/// 用 `Freeze()` 冻结几何：图表每次刷新都会重建，冻结能让 WPF 跳过
/// 变更通知的开销，数据量不大但白拿的性能没理由不要。
/// </summary>
internal sealed class DonutChart : FrameworkElement
{
    /// <summary>一段数据。Percentage 由控件自己算，调用方只给 Seconds。</summary>
    internal sealed record Slice(string Label, long Seconds, Color Fill);

    private List<Slice> _slices = new();
    private long _total;

    /// <summary>环的厚度占半径的比例。0.34 视觉上比较接近常见图表的观感。</summary>
    private const double ThicknessRatio = 0.34;

    public void SetData(IReadOnlyList<Slice> slices)
    {
        _slices = new List<Slice>(slices);
        _total = 0;
        foreach (Slice s in _slices) _total += s.Seconds;
        InvalidateVisual();
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

        // 没有任何数据：画一个空环，避免整块空白看不出是图表
        if (_total <= 0)
        {
            var emptyPen = new Pen(new SolidColorBrush(Color.FromArgb(0x33, 0x88, 0x88, 0x88)), thickness);
            emptyPen.Freeze();
            dc.DrawEllipse(null, emptyPen, center, midR, midR);
            return;
        }

        // 单项占满时 ArcSegment 画不出来，单独处理
        int nonZero = 0;
        Slice? only = null;
        foreach (Slice s in _slices)
        {
            if (s.Seconds > 0) { nonZero++; only = s; }
        }

        if (nonZero == 1 && only is not null)
        {
            var pen = new Pen(new SolidColorBrush(only.Fill), thickness);
            pen.Freeze();
            dc.DrawEllipse(null, pen, center, midR, midR);
            return;
        }

        double angle = -90.0;   // 从 12 点方向开始，顺时针
        foreach (Slice s in _slices)
        {
            if (s.Seconds <= 0) continue;

            double sweep = 360.0 * s.Seconds / _total;
            if (sweep <= 0) continue;

            DrawArc(dc, center, midR, thickness, angle, sweep, s.Fill);
            angle += sweep;
        }
    }

    /// <summary>画一段圆环。用两个圆弧拼出"有厚度的扇区"，闭合后填充。</summary>
    private static void DrawArc(DrawingContext dc, Point c, double midR, double thickness,
                                double startAngle, double sweep, Color color)
    {
        // 扇区之间留一点缝：同色系相邻时靠这个缝才分得清边界。
        // 缝太大会显得碎，1.2 度是肉眼刚好能看出、又不影响占比观感的程度。
        const double GapDegrees = 1.2;
        double s = startAngle + GapDegrees / 2;
        double e = startAngle + sweep - GapDegrees / 2;
        if (e <= s) { s = startAngle; e = startAngle + sweep; }   // 太窄就不留缝了

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

/// <summary>把秒数格式化成"1时20分"这类短文本，供图例使用。</summary>
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
