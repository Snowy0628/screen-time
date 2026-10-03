using System.Drawing;
using System.Drawing.Drawing2D;

namespace ScreenTime.App;

/// <summary>
/// GDI+ 绘制的小工具。`Graphics` 本身没有圆角矩形，得自己拼路径。
/// </summary>
internal static class GraphicsExtensions
{
    /// <summary>填充圆角矩形。</summary>
    public static void FillRoundedRectangle(this Graphics g, Brush brush, Rectangle r, int radius)
    {
        using GraphicsPath path = RoundedPath(r, radius);
        g.FillPath(brush, path);
    }

    /// <summary>描边圆角矩形。</summary>
    public static void DrawRoundedRectangle(this Graphics g, Pen pen, Rectangle r, int radius)
    {
        using GraphicsPath path = RoundedPath(r, radius);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();

        // 半径不能超过短边的一半，否则弧会互相穿插画出畸形
        int d = System.Math.Max(1, System.Math.Min(radius, System.Math.Min(r.Width, r.Height) / 2)) * 2;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }
}
