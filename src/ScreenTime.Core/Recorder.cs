namespace ScreenTime.Core;

/// <summary>
/// 采集状态机。每约 1 秒调用一次 <see cref="Tick"/>，职责：
///
///   1. 决定当前状态（活跃 / 空闲 / 锁屏 / 熄屏睡眠）并解析前台应用；
///   2. 把连续相同的采样**合并**成一个片段，避免每秒写一行（体积相差 100 倍以上）；
///   3. 按间隔**批量落库**，兼顾崩溃丢失窗口与磁盘写入频率；
///   4. 检测**时间缺口**并回填为"熄屏/睡眠"——这是现代待机（S0ix）下唯一可靠的手段，
///      因为此时计时器被系统节流，电源通知也可能不触发。
/// </summary>
public sealed class Recorder : IDisposable
{
    /// <summary>两次 Tick 实际间隔超过该秒数，判定中间发生了睡眠/休眠（或墙钟被改）。</summary>
    private const int GapThresholdSeconds = 120;

    /// <summary>
    /// 单行片段的最长秒数。超过则强制断开新起一行。
    /// 防止"挂机 8 小时不动"变成一个巨型区间——那样既不利于按天/按小时切片查询，
    /// 也让心跳与崩溃恢复的粒度变得过粗。
    /// </summary>
    private const int MaxSpanSeconds = 6 * 3600;

    private readonly AppResolver _resolver = new();
    private readonly IdleWatcher _idle = new();
    private readonly PowerWatcher _power;
    private readonly UsageStore _store;

    private readonly List<Span> _pending = new();

    private long _lastTickUnix;
    private long _lastFlushUnix;
    private long _coalesceStart = -1;
    private Sample? _lastSample;
    private long _flushesSinceHeartbeat;
    private long _totalSpansWritten;
    private long _totalGapsFilled;
    private TailRow? _tail;
    private bool _disposed;

    /// <summary>落库间隔（秒）。崩溃时最多丢失这么多秒的数据。</summary>
    public int FlushIntervalSeconds { get; set; } = 60;

    /// <summary>
    /// 运行时调整空闲阈值、落库间隔，以及"全屏应用算不算活跃"。
    ///
    /// 单独一个方法而不是直接改属性：空闲阈值归属 <see cref="IdleWatcher"/>，
    /// 落库间隔需要重置计时基准，否则改了间隔要等一个旧周期才生效。
    /// </summary>
    public void Configure(int idleThresholdSeconds, int flushIntervalSeconds,
                          bool fullscreenCountsAsActive = true)
    {
        if (idleThresholdSeconds > 0) _idle.IdleThresholdSeconds = idleThresholdSeconds;
        _idle.TreatFullscreenAsActive = fullscreenCountsAsActive;

        if (flushIntervalSeconds > 0)
        {
            FlushIntervalSeconds = flushIntervalSeconds;
            // 重置基准，让新间隔立即从"现在"开始计算
            _lastFlushUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        }
    }

    /// <summary>每隔多少次落库触发一次心跳回调（默认 60 次 × 60 秒 ≈ 1 小时）。</summary>
    public int HeartbeatEveryFlushes { get; set; } = 60;

    public AppResolver Resolver => _resolver;
    public IdleWatcher Idle => _idle;
    public PowerWatcher Power => _power;
    public UsageStore Store => _store;

    /// <summary>最近一次采样的状态（用于界面显示）。</summary>
    public Sample? CurrentSample { get; private set; }

    public long TotalSpansWritten => _totalSpansWritten;
    public long TotalGapsFilled => _totalGapsFilled;
    public int PendingSpanCount => _pending.Count;

    /// <summary>
    /// 当前内存中尚未落库的片段，加上正在累积的那一段。
    /// 供界面计算"今日实时时长"：数据库每 60 秒才落一次，
    /// 只读数据库会让界面在整分钟内显示 0，用户会以为没在记录。
    /// </summary>
    public IReadOnlyList<Span> PendingSpans()
    {
        var list = new List<Span>(_pending);
        if (_lastSample is { } s && _coalesceStart >= 0 && _lastTickUnix >= _coalesceStart)
        {
            list.Add(new Span(_coalesceStart, _lastTickUnix + 1, s.State, s.App));
        }
        return list;
    }

    /// <summary>可选诊断输出。</summary>
    public Action<string>? Diagnostics { get; set; }

    /// <summary>出现时间缺口（睡眠）时触发，参数为缺口秒数。</summary>
    public event Action<long>? GapDetected;

    /// <summary>到达心跳周期时触发，参数为本次实际写入的片段数。</summary>
    public event Action<int>? Heartbeat;

    /// <summary>状态发生变化时触发，参数为人类可读描述。</summary>
    public event Action<string>? StateChanged;

    public Recorder(UsageStore store)
    {
        _store = store;
        _power = new PowerWatcher();
        _power.StateChanged += msg => StateChanged?.Invoke(msg);
    }

    /// <summary>开始采集：以"熄屏"作为起点，等待第一次真实采样。</summary>
    public void Start(long nowUnix)
    {
        _lastTickUnix = nowUnix;
        _lastFlushUnix = nowUnix;
        _lastSample = null;
        _coalesceStart = nowUnix;
        _pending.Clear();

        // 进程重启后要能接着库里最后一行继续合并，否则每次重启都会多切一行
        try { _tail = _store.TryGetTail(); }
        catch { _tail = null; }

        // 补齐"上次运行结束 → 这次启动"之间的时间
        BackfillGapSinceLastRun(nowUnix);
    }

    /// <summary>
    /// 回填「上次运行结束」到「本次启动」之间的时间。
    ///
    /// 为什么需要：进程内的 <see cref="Tick"/> 缺口检测只能发现"程序一直在跑、
    /// 中间睡了一觉"的情况；而关机、休眠、或用户关掉程序再打开，中间那段
    /// 在内存里根本没有记录，会永远留成"无记录"。
    /// 这里用持久化的心跳时间戳把这段补成「熄屏 / 睡眠」——
    /// 那段时间机器确实没在工作，这是符合事实的推断，而不是编造数据。
    ///
    /// 下限用 <see cref="GapThresholdSeconds"/>：小于它的间隔只是正常重启，
    /// 不值得单独记一行。
    /// </summary>
    private void BackfillGapSinceLastRun(long nowUnix)
    {
        try
        {
            long? lastBeat = _store.LastHeartbeatUnix();
            if (lastBeat is not { } prev || prev <= 0) return;

            // 心跳是"上次落库的时刻"，落库间隔最多再往后一点，
            // 用落库间隔的 2 倍做宽限，避免把正常重启误判成睡眠。
            long grace = Math.Max(FlushIntervalSeconds * 2, GapThresholdSeconds);
            long gapStart = prev + grace;
            long gapEnd = nowUnix;

            if (gapEnd - gapStart < GapThresholdSeconds) return;

            _pending.Add(new Span(gapStart, gapEnd, UsageState.Off, AppIdentity.Unknown));
            _totalGapsFilled++;
            GapDetected?.Invoke(gapEnd - gapStart);
        }
        catch
        {
            // 回填失败不影响本次采集
        }
    }

    /// <summary>周期性调用（约 1 秒一次）。</summary>
    public void Tick(long nowUnix) => Tick(nowUnix, null);

    /// <summary>
    /// 周期性调用（约 1 秒一次）。
    /// <paramref name="forcedSample"/> 非空时跳过真实采样，用于确定性测试。
    /// </summary>
    public void Tick(long nowUnix, Sample? forcedSample)
    {
        if (_lastTickUnix == 0)
        {
            Start(nowUnix);
            return;
        }

        long elapsed = nowUnix - _lastTickUnix;

        // ---- 时间缺口：补记为熄屏/睡眠 ----
        if (elapsed > GapThresholdSeconds)
        {
            long gapStart = _lastTickUnix + 1;
            long gapEnd = nowUnix;
            if (gapEnd > gapStart)
            {
                // 先把缺口前的当前片段收尾，避免缺口被合并进前台使用
                CloseCurrentSpan(gapStart);
                _pending.Add(new Span(gapStart, gapEnd, UsageState.Off, AppIdentity.Unknown));
                _totalGapsFilled++;
                GapDetected?.Invoke(gapEnd - gapStart);
            }
            _coalesceStart = nowUnix;
            _lastSample = null;
        }
        else if (elapsed < 0)
        {
            // 墙钟被回拨：重新对齐，不产生负时长
            StateChanged?.Invoke($"检测到系统时钟回拨 {(-elapsed)} 秒，已重新对齐");
            _coalesceStart = nowUnix;
            _lastSample = null;
        }

        // ---- 采样 ----
        Sample sample = forcedSample ?? SampleNow();
        CurrentSample = sample;

        if (_lastSample is { } prev && prev == sample && _coalesceStart > 0)
        {
            // 与上一秒完全相同：继续延长当前片段，不产生新行
            long currentLen = nowUnix + 1 - _coalesceStart;
            if (currentLen > MaxSpanSeconds)
            {
                // 片段过长：主动断开，避免出现巨型区间
                CloseCurrentSpan(nowUnix + 1);
                _coalesceStart = nowUnix + 1;
                Diagnostics?.Invoke($"tick: 片段达到 {MaxSpanSeconds}s 上限，已断开新起一行");
            }
        }
        else
        {
            // 状态切换：收尾上一片段，开启新片段
            CloseCurrentSpan(nowUnix);
            _coalesceStart = nowUnix;
            _lastSample = sample;
        }

        _lastTickUnix = nowUnix;

        // ---- 节流落库 ----
        if (nowUnix - _lastFlushUnix >= FlushIntervalSeconds)
        {
            Flush();
        }
    }

    /// <summary>决定当前状态。优先级：睡眠 > 熄屏/合盖/锁屏 > 空闲 > 活跃。</summary>
    private Sample SampleNow()
    {
        if (_power.IsSuspended) return Sample.Off();
        if (_power.IsDisplayOff || _power.IsLidClosed) return Sample.Off();
        if (_power.IsLocked) return new Sample(UsageState.Locked, AppIdentity.Unknown, false);

        if (_idle.IsIdle())
        {
            // 空闲：屏幕亮着但用户没操作。应用仍记录，便于区分"挂着"的应用。
            AppIdentity? app = _resolver.ResolveForeground();
            return new Sample(UsageState.Idle, app ?? AppIdentity.Unknown, false);
        }

        AppIdentity? foreground = _resolver.ResolveForeground();
        // 前台窗口是壳进程（桌面/开始菜单）时不记为任何应用，但也算活跃
        return new Sample(UsageState.Active, foreground ?? AppIdentity.Unknown, false);
    }

    /// <summary>把 [_coalesceStart, end) 收尾成一个片段。</summary>
    private void CloseCurrentSpan(long end)
    {
        if (_coalesceStart < 0 || _lastSample is not { } s) return;
        if (end <= _coalesceStart) return;
        _pending.Add(new Span(_coalesceStart, end, s.State, s.App));
    }

    /// <summary>立即把待写片段落库。返回写入行数。</summary>
    public int Flush()
    {
        // 先把当前正在累积的片段收尾，避免数据滞留在内存。
        // 注意判定是 >=：Tick 只调用一次时 _lastTickUnix 与 _coalesceStart 相等，
        // 用 > 会导致这段已经过去的时间永远落不了库。
        if (_lastSample is not null && _coalesceStart >= 0 && _lastTickUnix >= _coalesceStart)
        {
            CloseCurrentSpan(_lastTickUnix + 1);
            _coalesceStart = _lastTickUnix + 1;
        }

        if (_pending.Count == 0)
        {
            _lastFlushUnix = _lastTickUnix;
            return 0;
        }

        List<Span> merged = Coalesce(_pending);
        _pending.Clear();

        // 首个片段会与库中尾行续接（若状态与应用相同），因此真实的"使用段"不会被落库边界切碎
        int tz = (int)DateTimeOffset.Now.Offset.TotalSeconds;
        string tailBefore = _tail is null ? "null" : $"id={_tail.Id} key='{_tail.MergeKey}' end={_tail.EndUtc}";
        string firstKey = merged[0].MergeKey();
        TailRow? previousTail = _tail;
        _tail = _store.WriteSpans(merged, _tail, tz);
        int written = merged.Count;
        _totalSpansWritten += written;
        _lastFlushUnix = _lastTickUnix;

        // 诊断：暴露尾行推进情况（仅在开启诊断时输出）
        if (Diagnostics != null)
        {
            bool mergedIntoTail = previousTail is not null
                && _tail is not null
                && _tail.Id == previousTail.Id
                && _tail.EndUtc > previousTail.EndUtc;
            Diagnostics($"flush: {merged.Count} 段, 首段key='{firstKey}' 时长={merged[0].DurationSeconds}s, " +
                        $"尾行(前)={tailBefore}, 尾行(后)={(_tail is null ? "null" : $"id={_tail.Id} end={_tail.EndUtc}")}, " +
                        (mergedIntoTail ? "已续接" : "未续接"));
        }

        if (++_flushesSinceHeartbeat >= HeartbeatEveryFlushes)
        {
            _flushesSinceHeartbeat = 0;
            Heartbeat?.Invoke(written);
        }
        return written;
    }

    /// <summary>合并相邻且相同的片段。状态相同 + 应用相同 + 时间连续，三者齐备才合并。</summary>
    internal static List<Span> Coalesce(List<Span> spans)
    {
        var outList = new List<Span>(spans.Count);
        foreach (Span s in spans)
        {
            if (outList.Count > 0)
            {
                Span last = outList[^1];
                // 非活跃状态没有应用归属，必须靠状态区分，否则会把"锁屏"和"熄屏"混成一段
                bool same = last.State == s.State
                            && last.EndUtc == s.StartUtc
                            && string.Equals(last.MergeKey(), s.MergeKey(), StringComparison.OrdinalIgnoreCase);

                if (same)
                {
                    outList[^1] = last with { EndUtc = s.EndUtc };
                    continue;
                }
            }
            outList.Add(s);
        }
        return outList;
    }

    /// <summary>停止采集：收尾并落库。</summary>
    public void Stop()
    {
        if (_lastSample is not null && _coalesceStart >= 0 && _lastTickUnix >= _coalesceStart)
        {
            CloseCurrentSpan(_lastTickUnix + 1);
            _coalesceStart = _lastTickUnix + 1;
        }
        Flush();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Stop(); } catch { /* 退出时忽略 */ }
        _power.Dispose();
    }
}
