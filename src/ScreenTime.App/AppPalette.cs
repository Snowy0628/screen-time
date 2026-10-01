using System.Windows.Media;

// WPF 与 WinForms 都存在 Color，固定用 WPF 的
using Color = System.Windows.Media.Color;

namespace ScreenTime.App;

/// <summary>
/// 应用的稳定配色与分类。
///
/// 配色由 exe 路径哈希决定：同一个应用在任何日子、任何视图里颜色都一致，
/// 这样时间轴、柱状图、排行榜三处的颜色可以互相对照。哈希取值固定在
/// 一个经过挑色的调色板上（明度足够、彼此可区分），而不是随机生成 RGB。
/// </summary>
internal static class AppPalette
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x0A, 0x84, 0xC8), // 蓝
        Color.FromRgb(0xA2, 0x59, 0xFF), // 紫
        Color.FromRgb(0x1D, 0xB9, 0x54), // 绿
        Color.FromRgb(0xE8, 0x72, 0x0C), // 橙
        Color.FromRgb(0x2F, 0x7F, 0xD6), // 中蓝
        Color.FromRgb(0xE0, 0x4F, 0x5F), // 红
        Color.FromRgb(0x00, 0xA6, 0xA6), // 青
        Color.FromRgb(0x9B, 0x7A, 0x2F), // 褐
        Color.FromRgb(0x7C, 0x3A, 0xED), // 深紫
        Color.FromRgb(0x14, 0x73, 0xE6), // 宝蓝
        Color.FromRgb(0xC0, 0x84, 0x57), // 棕
        Color.FromRgb(0x4C, 0xAF, 0x50), // 草绿
        Color.FromRgb(0x62, 0x64, 0xA7), // 靛
        Color.FromRgb(0xD9, 0x53, 0x4F), // 砖红
        Color.FromRgb(0x0F, 0x6C, 0xBD), // 深蓝
        Color.FromRgb(0xF2, 0xB2, 0x1C), // 黄
    };

    private static readonly Color Fallback = Color.FromRgb(0x8B, 0x93, 0xA3);

    /// <summary>按稳定键（exe 路径）取颜色。</summary>
    public static Color ColorFor(string key)
    {
        if (string.IsNullOrEmpty(key)) return Fallback;

        // FNV-1a：稳定、跨进程一致，不需要持久化
        unchecked
        {
            uint hash = 2166136261;
            foreach (char c in key)
            {
                hash ^= char.ToLowerInvariant(c);
                hash *= 16777619;
            }
            return Palette[hash % (uint)Palette.Length];
        }
    }

    /// <summary>粗略分类，用于排行的副标题。关键字匹配，命中即返回。</summary>
    public static string CategoryOf(string exePath)
    {
        string n = System.IO.Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();

        if (Match(n, "code", "devenv", "rider", "pycharm", "idea", "webstorm", "clion",
                       "goland", "studio", "eclipse", "sublime", "notepad", "vim", "emacs",
                       "windowsterminal", "wt", "powershell", "cmd", "conhost", "git"))
            return "开发";
        if (Match(n, "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "iexplore", "360se", "qqbrowser"))
            return "浏览器";
        if (Match(n, "figma", "photoshop", "illustrator", "blender", "procreate", "krita",
                       "gimp", "inkscape", "aftereffects", "premiere", "davinci", "3dsmax", "maya", "zbrush"))
            return "设计";
        if (Match(n, "winword", "excel", "powerpnt", "onenote", "outlook", "notion", "obsidian",
                       "wps", "et", "wpp", "acrobat", "acrord32", "sumatrapdf", "typora", "onenotem"))
            return "办公";
        if (Match(n, "wechat", "weixin", "qq", "tim", "teams", "slack", "discord", "dingtalk",
                       "telegram", "zoom", "wemeet", "feishu", "lark", "skype"))
            return "沟通";
        if (Match(n, "steam", "epicgameslauncher", "battle.net", "ubisoftconnect", "wegame",
                       "genshinimpact", "yuanshen", "spotify", "cloudmusic", "potplayer",
                       "vlc", "mpc-hc", "bilibili", "douyin", "game"))
            return "娱乐";
        if (Match(n, "explorer", "taskmgr", "control", "systemsettings", "calc", "mspaint",
                       "snippingtool", "notepad", "mmc", "regedit"))
            return "系统";
        if (Match(n, "screentime", "screen")) return "自身";

        return "其他";
    }

    private static bool Match(string name, params string[] keys)
    {
        foreach (string k in keys)
        {
            if (name.Contains(k, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}
