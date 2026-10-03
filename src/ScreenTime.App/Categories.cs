using System;
using System.Collections.Generic;
using System.Windows.Media;

namespace ScreenTime.App;

/// <summary>
/// 应用分类的模型：默认分类清单、分类配色、自定义分类的增删。
///
/// ### 分类从哪来
///
/// 优先级：**用户手动设定 > 关键字自动推断 > "其他"**
///
/// 手动设定存在数据库的 `app_category` 表里（按完整路径），关键字表在
/// <see cref="AppPalette"/> 里。这样设计的好处：以后扩充关键字表时，
/// 没被手动改过的应用会自动跟着改进，不用把推断结果也固化进数据库。
///
/// ### 自定义分类
///
/// 用户可以自己加分类名，存在 settings.json 里（而不是数据库）。
/// 放设置的考虑：卸载时如果选了"全部删除"，数据库会被清掉，
/// 但用户自己起的分类名属于"配置"而不是"数据"，不该跟着没。
/// </summary>
internal static class Categories
{
    /// <summary>"其他"是兜底分类，任何无法归类的都进这里，不允许删除。</summary>
    public const string Fallback = "其他";

    /// <summary>
    /// 系统自带的默认分类。顺序有意义——饼图的图例和小色块按这个顺序排，
    /// 固定的顺序能让同一份数据每次看起来一致。
    /// </summary>
    public static readonly string[] Defaults =
    {
        "开发", "浏览器", "设计", "办公", "沟通", "游戏", "娱乐", "系统", "自身", Fallback,
    };

    /// <summary>
    /// 分类配色。
    ///
    /// 用与主题色同一套莫兰迪调性（低饱和、带灰），饼图里相邻扇区才不会打架。
    /// 与其让用户配，不如给一套调好的——分类数量固定、语义也固定，
    /// 自动配色的收益远大于让用户逐个挑。
    /// </summary>
    private static readonly Dictionary<string, Color> Palette = new(StringComparer.Ordinal)
    {
        ["开发"]   = Color.FromRgb(0x6E, 0x8B, 0xB8),   // 雾蓝
        ["浏览器"] = Color.FromRgb(0x7A, 0x93, 0xAD),   // 灰蓝
        ["设计"]   = Color.FromRgb(0xB0, 0x8C, 0xA8),   // 藕荷
        ["办公"]   = Color.FromRgb(0x9A, 0xA5, 0x8C),   // 苔绿
        ["沟通"]   = Color.FromRgb(0x8A, 0xA6, 0x8F),   // 鼠尾草
        ["游戏"]   = Color.FromRgb(0xC0, 0x8A, 0x88),   // 陶土
        ["娱乐"]   = Color.FromRgb(0xB3, 0x9B, 0x7D),   // 燕麦
        ["系统"]   = Color.FromRgb(0x9A, 0x9F, 0xA8),   // 中性灰
        ["自身"]   = Color.FromRgb(0x95, 0x85, 0xAC),   // 灰紫
        [Fallback] = Color.FromRgb(0xA8, 0xAC, 0xB4),   // 浅灰
    };

    /// <summary>自定义分类的兜底配色池（按名字哈希取，保证同名同色）。</summary>
    private static readonly Color[] ExtraPalette =
    {
        Color.FromRgb(0x8C, 0x9A, 0xB0),
        Color.FromRgb(0xA8, 0x94, 0x9C),
        Color.FromRgb(0x94, 0xA8, 0x9C),
        Color.FromRgb(0xB0, 0xA0, 0x88),
        Color.FromRgb(0x9C, 0x94, 0xB0),
        Color.FromRgb(0xA0, 0xA8, 0xB4),
    };

    /// <summary>取分类颜色。未知分类按名字哈希从备用池里取，保证稳定。</summary>
    public static Color ColorOf(string category)
    {
        if (Palette.TryGetValue(category, out Color c)) return c;

        unchecked
        {
            uint hash = 2166136261;
            foreach (char ch in category)
            {
                hash ^= ch;
                hash *= 16777619;
            }
            return ExtraPalette[hash % (uint)ExtraPalette.Length];
        }
    }

    /// <summary>当前可用的全部分类：默认 + 用户自定义（去重、保持顺序）。</summary>
    public static List<string> All()
    {
        var list = new List<string>(Defaults);
        foreach (string s in AppSettings.Current.CustomCategories)
        {
            string t = (s ?? "").Trim();
            if (t.Length == 0) continue;
            if (list.Contains(t, StringComparer.Ordinal)) continue;
            list.Add(t);
        }
        return list;
    }

    /// <summary>
    /// 新增一个自定义分类。返回是否成功（重名、空名、与默认重名都算失败）。
    /// </summary>
    public static bool AddCustom(string name)
    {
        string t = (name ?? "").Trim();
        if (t.Length == 0) return false;
        if (t.Length > 12) return false;                      // 太长会撑破下拉框
        if (All().Contains(t, StringComparer.Ordinal)) return false;

        AppSettings s = AppSettings.Current;
        s.CustomCategories.Add(t);
        s.Save();
        return true;
    }

    /// <summary>
    /// 删除一个自定义分类。默认分类不能删。
    /// 返回受影响的路径数，供调用方提示"已把 N 个应用改回自动推断"。
    /// </summary>
    public static int RemoveCustom(string name)
    {
        if (Defaults.Contains(name, StringComparer.Ordinal)) return 0;

        AppSettings s = AppSettings.Current;
        bool removed = false;
        for (int i = s.CustomCategories.Count - 1; i >= 0; i--)
        {
            if (string.Equals(s.CustomCategories[i], name, StringComparison.Ordinal))
            {
                s.CustomCategories.RemoveAt(i);
                removed = true;
            }
        }
        if (removed) s.Save();
        return removed ? 1 : 0;
    }

    /// <summary>
    /// 某分类是不是"系统默认"的（不可删除）。
    /// </summary>
    public static bool IsDefault(string name) => Defaults.Contains(name, StringComparer.Ordinal);
}
