using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace ScreenTime.App;

/// <summary>一个可选的强调色预设。</summary>
internal sealed record AccentPreset(string Name, string Hex);

/// <summary>
/// 主题配色：预设色 + 自定义色号，运行时替换资源令牌。
///
/// ### 为什么能做得这么轻
///
/// 界面里所有强调色都走 `DynamicResource`（全项目只有 0 处 StaticResource），
/// 集中在 `Themes/Light.xaml` 与 `Themes/Dark.xaml` 的 20 个令牌里，
/// 而真正与"主题色"相关的只有两个：
///
///   `AccentBrush` 纯色      → 日/周/月分段按钮选中态、热力图、图表强调色
///   `HeroBrush`   线性渐变  → 顶栏 Logo 方块、总时长大卡片、一处主按钮
///
/// 所以换色 = 运行时往 `Application.Current.Resources` 里塞两个新画刷，
/// 不需要重启，也不需要改任何界面代码。
///
/// ### 渐变规则（用户明确要求）
///
///   · 默认方案      → 保持原有的蓝紫渐变 #2563EB → #7C3AED
///   · 选了预设/自定义 → **改为纯色**，不做渐变
///
/// ### 不动涨跌色
///
/// `UpBrush` / `DownBrush` 是**语义色**（用时长变多算坏消息，所以涨=红、跌=绿）。
/// 跟着主题色变会让"变红"和"变绿"失去意义，因此恒定不动。
/// </summary>
internal static class ThemeCustomizer
{
    /// <summary>默认方案的名字（蓝紫渐变）。</summary>
    public const string DefaultName = "默认蓝紫";

    /// <summary>
    /// 预设色。第一个是默认方案（蓝紫渐变），其余都是纯色。
    ///
    /// **取色走莫兰迪风格**：低饱和、带灰调，压得住界面不刺眼。
    /// 早先那版用过 #E11D48 / #F97316 这类高饱和色，面板一点开就是一片跳色，
    /// 和界面本身的克制风格不搭。
    ///
    /// 唯一例外是「洛天依蓝 #66CCFF」——用户点名要的，它本来就偏浅通透，
    /// 放在这组灰调里不显突兀。
    ///
    /// 都是中等偏深的取值：再浅一点在白色卡片上就分不出色块边界了。
    /// </summary>
    public static readonly AccentPreset[] Presets =
    {
        new(DefaultName,  "#2563EB"),   // 渐变：这个色 → #7C3AED（保持原有观感）
        new("洛天依蓝",    "#66CCFF"),   // 用户指定
        new("雾霾蓝",      "#7A93AD"),
        new("鼠尾草绿",    "#8AA68F"),
        new("陶土粉",      "#C08A88"),
        new("灰紫",        "#9585AC"),
        new("燕麦棕",      "#B39B7D"),
    };

    /// <summary>默认方案的渐变第二色。</summary>
    private const string DefaultGradientEnd = "#7C3AED";

    /// <summary>解析 #RRGGBB / RRGGBB / #RGB 形式。失败返回 null。</summary>
    public static Color? ParseHex(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string s = text.Trim().TrimStart('#');

        // #RGB 简写补成 #RRGGBB
        if (s.Length == 3)
            s = new string(new[] { s[0], s[0], s[1], s[1], s[2], s[2] });

        if (s.Length != 6) return null;
        if (!int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
            return null;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint u))
            return null;

        _ = v;
        return Color.FromRgb((byte)(u >> 16), (byte)((u >> 8) & 0xFF), (byte)(u & 0xFF));
    }

    /// <summary>把颜色格式化成 #RRGGBB。</summary>
    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>根据设置算出当前该用哪个颜色。返回 null 表示用默认渐变。</summary>
    public static Color? ResolveCustomColor(AppSettings s)
    {
        // 自定义色号优先于预设
        if (ParseHex(s.CustomAccentHex) is { } custom) return custom;

        if (!string.IsNullOrWhiteSpace(s.AccentPresetName))
        {
            foreach (AccentPreset p in Presets)
            {
                if (p.Name == s.AccentPresetName)
                {
                    if (p.Name == DefaultName) return null;   // 默认 = 渐变
                    return ParseHex(p.Hex);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 当前是否为默认方案（蓝紫渐变）。
    /// 设置界面用它来决定高亮哪个预设。
    /// </summary>
    public static bool IsDefault(AppSettings s) => ResolveCustomColor(s) is null;

    /// <summary>
    /// 把配色应用到运行时资源。必须在主题字典合并**之后**调用——
    /// 换主题会重新合并 Themes/*.xaml，那两个令牌会被重置回默认值。
    /// </summary>
    public static void Apply(AppSettings s)
    {
        try
        {
            if (Application.Current is not { } app) return;

            Color? custom = ResolveCustomColor(s);

            if (custom is { } c)
            {
                var accent = new SolidColorBrush(c);
                accent.Freeze();

                // 纯色：HeroBrush 也做成纯色渐变（两个 stop 同色），
                // 这样所有引用 HeroBrush 的地方都不用改模板。
                var hero = new LinearGradientBrush(c, c, new Point(0, 0), new Point(1, 1));
                hero.Freeze();

                app.Resources["AccentBrush"] = accent;
                app.Resources["HeroBrush"] = hero;
                app.Resources["StateActiveBrush"] = accent;
            }
            else
            {
                // 恢复默认：清掉运行时覆盖，让主题字典里的原值生效。
                // 直接 Remove 而不是塞一个副本，这样以后改主题文件能立刻反映出来。
                app.Resources.Remove("AccentBrush");
                app.Resources.Remove("HeroBrush");
                app.Resources.Remove("StateActiveBrush");
            }

            // 应用级资源改了之后，已经渲染的元素要重新解析 DynamicResource。
            RefreshVisualTree();
        }
        catch
        {
            // 换色失败不该影响采集
        }
    }

    /// <summary>
    /// 让现有界面重新解析 DynamicResource。
    ///
    /// WPF 对 `Application.Resources` 的改动**不会**自动通知已经在用的
    /// DynamicResource 引用（只有合并字典被整体替换时才会）。
    /// 走一遍可视树、把根元素的资源引用失效一次是最省事的做法。
    /// </summary>
    private static void RefreshVisualTree()
    {
        try
        {
            foreach (Window w in Application.Current.Windows)
            {
                if (w is MainWindow mw) mw.RefreshThemeColors();
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>默认渐变的起止色，供设置界面预览用。</summary>
    public static (string Start, string End) DefaultGradient =>
        (Presets[0].Hex, DefaultGradientEnd);

    /// <summary>给设置界面列出所有预设（含默认）。</summary>
    public static IReadOnlyList<AccentPreset> All => Presets;
}
