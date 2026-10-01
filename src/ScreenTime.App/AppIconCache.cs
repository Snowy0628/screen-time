using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

// 本项目同时引用 WPF 与 WinForms，Color 需要固定到 WPF 版本
using Color = System.Windows.Media.Color;

namespace ScreenTime.App;

/// <summary>
/// 应用图标缓存：从 exe 提取图标并缓存为 WPF ImageSource。
///
/// 关键点：WPF 的 BitmapSource 有线程亲和性，必须在 UI 线程创建；
/// 而 SHGetFileInfo 本身开销不大且有缓存，因此直接在 UI 线程按需提取即可。
/// </summary>
internal static class AppIconCache
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000;
    private const uint SHGFI_SMALLICON = 0x000000001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>取指定 exe 的图标；失败或路径为空返回 null（界面会显示占位方块）。</summary>
    public static ImageSource? Get(string? exePath, int size = 32)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (Cache.TryGetValue(exePath, out ImageSource? cached)) return cached;

        ImageSource? result = Extract(exePath, size);
        Cache[exePath] = result;
        return result;
    }

    /// <summary>
    /// 取应用图标的主色调，用于时间轴与柱状图上色。
    ///
    /// 为什么值得做：微信就是绿色、QQ 是蓝色、网易云是红色——
    /// 用图标本身的颜色，图表的可读性远好于"按路径哈希挑一个调色板颜色"。
    /// 哈希方案里同一个应用在不同机器上颜色一致但与品牌无关，
    /// 用户无法一眼把色块和大脑里的应用形象对应起来。
    ///
    /// 取不到图标（或图标是纯灰白）时返回 null，调用方退回调色板。
    /// </summary>
    public static Color? GetDominantColor(string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath)) return null;
        if (ColorCache.TryGetValue(exePath, out Color? cached)) return cached;

        Color? result = null;
        try
        {
            // 用大图标取色：16×16 太小，细节全糊在一起会取偏
            ImageSource? icon = Get(exePath, 32);
            if (icon is BitmapSource bs) result = DominantOf(bs);
        }
        catch
        {
            result = null;
        }

        ColorCache[exePath] = result;
        return result;
    }

    private static readonly ConcurrentDictionary<string, Color?> ColorCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 从位图里找出最有代表性的颜色。
    ///
    /// 做法：把像素按 4 位/通道归并（16×16×16 个桶）投票，然后：
    ///   · 跳过透明像素与近白/近黑像素 —— 多数图标主体之外是白底或透明描边，
    ///     直接取"出现最多的颜色"往往会得到白色；
    ///   · 按 出现次数 × 饱和度 加权，让品牌色胜过大片浅灰；
    ///   · 对胜出桶内的像素取平均，避免结果被量化误差带偏。
    /// </summary>
    private static Color? DominantOf(BitmapSource src)
    {
        var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = conv.PixelWidth, h = conv.PixelHeight;
        if (w <= 0 || h <= 0) return null;

        int stride = w * 4;
        var px = new byte[stride * h];
        conv.CopyPixels(px, stride, 0);

        var buckets = new Dictionary<int, (long Count, long R, long G, long B, double Weight)>();
        for (int i = 0; i + 3 < px.Length; i += 4)
        {
            byte b = px[i], g = px[i + 1], r = px[i + 2], a = px[i + 3];
            if (a < 128) continue;                       // 透明

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));
            int sat = max - min;
            if (max > 240 && sat < 24) continue;         // 近白背景
            if (max < 28) continue;                      // 近黑描边

            // 4 位/通道量化
            int key = (r >> 4) << 8 | (g >> 4) << 4 | (b >> 4);
            // 饱和度越高越可能是品牌色
            double weight = 1.0 + sat / 255.0 * 3.0;

            if (buckets.TryGetValue(key, out var cur))
                buckets[key] = (cur.Count + 1, cur.R + r, cur.G + g, cur.B + b, cur.Weight + weight);
            else
                buckets[key] = (1, r, g, b, weight);
        }

        if (buckets.Count == 0) return null;

        var best = buckets.OrderByDescending(kv => kv.Value.Weight).First().Value;
        if (best.Count == 0) return null;

        byte fr = (byte)(best.R / best.Count);
        byte fg = (byte)(best.G / best.Count);
        byte fb = (byte)(best.B / best.Count);
        return Normalize(Color.FromRgb(fr, fg, fb));
    }

    /// <summary>
    /// 把取到的品牌色调整到适合图表使用的明度。
    ///
    /// 图标里的品牌色往往偏亮（比如一些应用的浅黄、浅青），
    /// 直接铺在白色卡片上会看不清；但只压暗不换色相，
    /// 用户仍能认出"这是那个应用的绿"。
    /// 目标明度控制在 0.42–0.62 之间，与原来的调色板观感接近。
    /// </summary>
    private static Color Normalize(Color c)
    {
        double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        const double lo = 0.42, hi = 0.62;

        if (lum >= lo && lum <= hi) return c;

        double scale = lum < 0.01 ? 1.0 : (lum < lo ? lo / lum : hi / lum);
        byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        return Color.FromRgb(Clamp(c.R * scale), Clamp(c.G * scale), Clamp(c.B * scale));
    }

    private static ImageSource? Extract(string path, int size)
    {
        var info = new SHFILEINFO();
        uint flags = SHGFI_ICON | (size <= 16 ? SHGFI_SMALLICON : SHGFI_LARGEICON);

        try
        {
            IntPtr ret = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), flags);
            if (ret == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;

            try
            {
                BitmapSource src = Imaging.CreateBitmapSourceFromHIcon(
                    info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();   // 冻结后可跨线程安全使用，也省去后续复制
                return src;
            }
            finally
            {
                DestroyIcon(info.hIcon);
            }
        }
        catch
        {
            return null;
        }
    }
}
