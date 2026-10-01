namespace ScreenTime.Core;

/// <summary>记录状态四态。数值会写入数据库，不要随意改动既有取值。</summary>
public enum UsageState
{
    /// <summary>前台活跃：有窗口在前台且用户近期有键鼠输入。</summary>
    Active = 0,

    /// <summary>空闲：无输入但屏幕仍亮、未锁屏。</summary>
    Idle = 1,

    /// <summary>锁屏。</summary>
    Locked = 2,

    /// <summary>熄屏或睡眠（含合盖、待机恢复后的补记）。</summary>
    Off = 3,
}

/// <summary>被跟踪应用的稳定身份。同名进程只产生一个身份。</summary>
public sealed record AppIdentity(string FilePath, string ProcessName, string DisplayName)
{
    /// <summary>无法识别（提权进程、系统壳等）时使用。</summary>
    public static readonly AppIdentity Unknown = new("", "unknown", "未知");
}

/// <summary>一次采样得到的原始状态。</summary>
public readonly record struct Sample(
    UsageState State,
    AppIdentity App,
    bool IsFullscreen)
{
    public static Sample Off() => new(UsageState.Off, AppIdentity.Unknown, false);
}

/// <summary>可合并的时间片段。同一应用、同一状态、时间连续的片段会被合并成一段。</summary>
public sealed record Span(long StartUtc, long EndUtc, UsageState State, AppIdentity App)
{
    public long DurationSeconds => EndUtc - StartUtc;

    /// <summary>
    /// 合并判定用的身份键。有路径用路径；无路径（提权进程）退化为进程名，
    /// 再加状态前缀，避免"空闲"与"活跃"被判成同一个应用。
    /// </summary>
    public string MergeKey()
    {
        string app = App.FilePath.Length > 0 ? App.FilePath : "~" + App.ProcessName;
        return ((int)State).ToString() + "|" + app;
    }
}

/// <summary>某个应用在某一天的汇总。</summary>
public sealed record AppTotal(int AppId, string DisplayName, string FilePath, long Seconds);

/// <summary>
/// 数据库里最后一行片段。用于让下一次落库的片段能与之**续接**，
/// 否则每次落库都会把一段连续使用切成多行（相差可达 100 倍体积）。
/// </summary>
public sealed record TailRow(long Id, long StartUtc, long EndUtc, UsageState State, string MergeKey);

/// <summary>一天的总体统计。</summary>
public sealed record DayTotals(
    long ActiveSeconds,
    long IdleSeconds,
    long LockedSeconds,
    long OffSeconds,
    int DistinctApps)
{
    /// <summary>屏幕亮着的总时长 = 活跃 + 空闲 + 锁屏。</summary>
    public long ScreenOnSeconds => ActiveSeconds + IdleSeconds + LockedSeconds;
}
