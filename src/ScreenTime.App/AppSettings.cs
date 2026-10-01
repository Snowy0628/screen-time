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
