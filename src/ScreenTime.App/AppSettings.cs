using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenTime.App;

/// <summary>点窗口关闭按钮（X）时的行为。</summary>
internal enum CloseAction
{
    /// <summary>每次询问。</summary>
    Ask = 0,

    /// <summary>最小化到托盘，继续记录。</summary>
    MinimizeToTray = 1,

    /// <summary>彻底退出。</summary>
    Exit = 2,
}

/// <summary>用户设置。以 JSON 存在数据目录下，与数据库并列。</summary>
internal sealed class AppSettings
{
    /// <summary>关闭窗口（X）时的行为。</summary>
    public CloseAction CloseAction { get; set; } = CloseAction.Ask;

    /// <summary>空闲判定阈值（秒）。超过这个时长没有键鼠输入就算"空闲"。</summary>
    public int IdleThresholdSeconds { get; set; } = 60;

    /// <summary>落库间隔（秒）。越大写入越少，越小数据越及时。</summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>启动时是否自动打开主界面（关闭则只留托盘）。</summary>
    public bool OpenWindowOnStartup { get; set; } = true;

    /// <summary>是否启用界面动效。低配机器或远程桌面下可关闭。</summary>
    public bool EnableAnimations { get; set; } = true;

    /// <summary>对比基准：true = 与昨天/上一周期对比。</summary>
    public bool CompareWithPrevious { get; set; } = true;

    /// <summary>外观模式：跟随系统 / 浅色 / 深色。</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    /// <summary>是否已配置开机自启（登录后静默启动，只留托盘）。</summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// 全屏应用（视频、游戏）是否视为"正在使用"，即不判空闲。
    /// 默认 true：看视频时人明明在电脑前，只按键鼠空闲判定会把它误记成空闲。
    /// </summary>
    public bool FullscreenCountsAsActive { get; set; } = true;

    /// <summary>
    /// 时间轴与柱状图的**起始小时**（0–23）。默认 0 点。
    ///
    /// 为什么默认 0 点：0 点才是一天的自然边界。若改成 6 点，横轴会覆盖
    /// [当日06:00 → 次日06:00]，凌晨的使用就被算进"前一天"，
    /// "这一天用了多久"的含义会变模糊。起始点做成可配置是为了留出调整余地，
    /// 但默认值必须是 0。
    /// </summary>
    public int ChartStartHour { get; set; }

    /// <summary>时间轴分格粒度（分钟）：30 / 60 / 180。</summary>
    public int TimelineBucketMinutes { get; set; } = 60;

    /// <summary>
    /// 主题配色方案的名字。空字符串 = 默认（蓝紫渐变）。
    ///
    /// 存名字而不是存色号，是为了让"预设色"以后能微调色值而不影响已有设置；
    /// 自定义颜色用 <see cref="CustomAccentHex"/> 单独存。
    /// </summary>
    public string AccentPresetName { get; set; } = "";

    /// <summary>
    /// 用户自定义的强调色，形如 "#66CCFF"。为空表示没设。
    /// 设了它就以它为准（优先于 <see cref="AccentPresetName"/>）。
    /// </summary>
    public string CustomAccentHex { get; set; } = "";

    /// <summary>背景图路径。为空表示不用背景图。</summary>
    public string BackgroundImagePath { get; set; } = "";

    /// <summary>背景图不透明度（0.0–1.0）。默认偏淡，避免盖过内容。</summary>
    public double BackgroundImageOpacity { get; set; } = 0.25;

    /// <summary>
    /// 用户自己新增的应用分类名。
    ///
    /// 存在设置里而不是数据库：卸载时若选了"全部删除"，数据库会被清掉，
    /// 但"我起过哪些分类名"属于配置而不是使用数据，不该跟着没。
    /// 默认分类（开发/浏览器/…）写死在 <see cref="Categories.Defaults"/>，不存这里。
    /// </summary>
    public List<string> CustomCategories { get; set; } = new();

    /// <summary>
    /// 每个分类的自定义颜色：分类名 → "#RRGGBB"。
    ///
    /// 没设过的分类走内置配色 / 名字哈希，所以这里只存用户改过的。
    /// 与 <see cref="CustomCategories"/> 同理放在设置里——
    /// 卸载时数据库可能被清空，但配色属于配置。
    /// </summary>
    public Dictionary<string, string> CategoryColors { get; set; } = new();

    // ---- 读写 ----

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                AppSettings? s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (s is not null) return s;
            }
        }
        catch
        {
            // 配置损坏时退回默认值，不影响采集
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 写不了设置不该影响程序运行
        }
    }
}
