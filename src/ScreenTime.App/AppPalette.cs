using System;
using System.Collections.Generic;
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

    /// <summary>
    /// 用户手动设定的分类缓存：路径 → 分类名。
    ///
    /// 为什么要缓存：分类判定在渲染排行时对每个应用调用一次，而手动分类表
    /// 存在数据库里——每次都查库会成为刷新时的固定开销。缓存由
    /// <see cref="ReloadManualCategories"/> 在启动时与用户改动后刷新。
    /// </summary>
    private static Dictionary<string, string> _manual =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>从数据库重新载入手动分类。启动时与用户在分类界面改动后调用。</summary>
    public static void ReloadManualCategories(Core.UsageStore store)
    {
        try
        {
            _manual = store.GetAppCategories();
        }
        catch
        {
            _manual = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// 分类判定。**优先级：用户手动设定 > 关键字自动推断 > "其他"**。
    ///
    /// 手动优先是这套设计的核心：自动推断只能做到"大致对"，
    /// 而用户对自己装了什么程序最清楚。手动设定按**完整路径**匹配，
    /// 不同目录下的同名 exe 可以分开归类。
    /// </summary>
    public static string CategoryOf(string exePath)
    {
        if (exePath.Length > 0 && _manual.TryGetValue(exePath, out string? manual))
        {
            if (!string.IsNullOrWhiteSpace(manual)) return manual;
        }

        return InferCategory(exePath);
    }

    /// <summary>
    /// 关键字自动推断。用户没手动设定的应用走这条。
    ///
    /// **顺序有意义**：先匹配的那条赢。"游戏"放在"娱乐"之前，
    /// 否则 `steam` 这类两者都沾边的会被娱乐先抢走。
    /// </summary>
    public static string InferCategory(string exePath)
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

        // 游戏。放在"娱乐"之前，因为 steam / 加速器 这类两边都沾。
        //
        // 关键字取自实际统计里出现的进程名——那些名字大多看不出是游戏，
        // 必须逐个补进来，否则永远落在"其他"：
        //   Client-Win64-Shipping  虚幻引擎打包后的游戏主进程（鸣潮等）
        //   launcher_main          国产游戏启动器
        //   KRSDKExternal          游戏内嵌 SDK（鸣潮 third-party 目录）
        //   uu                     UU 加速器
        if (Match(n, "client-win64-shipping", "client-win32-shipping", "launcher_main",
                       "krsdk", "uu", "wegame", "steam", "epicgameslauncher", "battle.net",
                       "ubisoftconnect", "origin", "eaapp", "gog galaxy", "genshinimpact",
                       "yuanshen", "wuthering", "mingchao", "deltaforce", "hoyoplay",
                       "game", "games", "rpg", "fps", "minecraft", "roblox", "valorant",
                       "league", "cs2", "csgo", "dota", "pubg", "apex", "overwatch"))
            return "游戏";

        if (Match(n, "spotify", "cloudmusic", "potplayer", "vlc", "mpc-hc", "mpv",
                       "bilibili", "douyin", "iqiyi", "youku", "qqmusic", "kugou", "foobar2000"))
            return "娱乐";
        if (Match(n, "explorer", "taskmgr", "control", "systemsettings", "calc", "mspaint",
                       "snippingtool", "mmc", "regedit", "shellhost", "systraycomponent",
                       "chxsmartscreen", "gamingcenter", "razer", "msiafterburner", "rtss"))
            return "系统";
        if (Match(n, "screentime", "screen")) return "自身";

        return Categories.Fallback;
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
