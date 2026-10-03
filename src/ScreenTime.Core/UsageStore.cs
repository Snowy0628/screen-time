using Microsoft.Data.Sqlite;

namespace ScreenTime.Core;

/// <summary>
/// SQLite 存储层。
///
/// 设计要点：
///   * 只存"片段"（span）而不是每秒一行：相邻同应用同状态的采样被 Recorder 合并后写入，
///     每天大约 3k–15k 行，而不是 86,400 行；
///   * 时间戳统一存 UTC 秒，另存本地日键 day_key（yyyy-MM-dd）便于按天聚合，
///     避免每次查询都要做时区换算；
///   * 表名统一用单数 app/session，避免与 SQL 关键字冲突；
///   * 查询按 start_utc 做区间扫描并裁剪首尾，不依赖会话完全不跨天。
/// </summary>
public sealed class UsageStore : IDisposable
{
    private const int SchemaVersion = 2;

    private readonly SqliteConnection _conn;
    private bool _disposed;

    /// <summary>可选诊断输出。</summary>
    public Action<string>? Diagnostics { get; set; }

    public string DatabasePath { get; }

    public UsageStore(string databasePath)
    {
        DatabasePath = databasePath;
        string? dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
        }.ToString());

        _conn.Open();
        InitSchema();
    }

    private void InitSchema()
    {
        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Exec("PRAGMA busy_timeout=5000;");

        Exec("""
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS app (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                file_path  TEXT NOT NULL,
                process    TEXT NOT NULL,
                display    TEXT NOT NULL,
                created_utc INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_app_path ON app(file_path);
            CREATE INDEX IF NOT EXISTS ix_app_display ON app(display);

            CREATE TABLE IF NOT EXISTS session (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                start_utc  INTEGER NOT NULL,
                end_utc    INTEGER NOT NULL,
                day_key    TEXT    NOT NULL,
                state      INTEGER NOT NULL,
                app_id     INTEGER NULL REFERENCES app(id),
                tz_offset  INTEGER NOT NULL,
                CHECK (end_utc > start_utc),
                CHECK (state BETWEEN 0 AND 3)
            );
            CREATE INDEX IF NOT EXISTS ix_session_start ON session(start_utc);
            CREATE INDEX IF NOT EXISTS ix_session_day   ON session(day_key, app_id);

            CREATE TABLE IF NOT EXISTS day_summary (
                day_key   TEXT    NOT NULL,
                app_id    INTEGER NOT NULL,
                seconds   INTEGER NOT NULL,
                PRIMARY KEY (day_key, app_id)
            );

            -- 采集心跳：用于自检"最近一次成功记录是什么时候"，以及判断是否存在采集缺口
            CREATE TABLE IF NOT EXISTS heartbeat (
                ts_utc       INTEGER PRIMARY KEY,
                spans_written INTEGER NOT NULL,
                note         TEXT NOT NULL DEFAULT ''
            );

            -- 用户手动设定的应用分类。
            --
            -- 按**完整路径**做主键，而不是进程名：不同目录下的同名 exe
            -- （比如两个版本的启动器）应当能分别归类。
            --
            -- 只存"用户改过的"，没改过的走 AppPalette 的关键字推断。
            -- 这样以后扩充关键字表时，未手动干预的应用会自动跟着改进，
            -- 不用把推断结果也固化下来。
            CREATE TABLE IF NOT EXISTS app_category (
                file_path TEXT PRIMARY KEY,
                category  TEXT NOT NULL
            );
            """);

        SetMeta("schema_version", SchemaVersion.ToString());
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void SetMeta(string key, string value)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    public string? GetMeta(string key)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key=$k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    /// <summary>取应用 id，不存在则插入。空路径（提权进程）用进程名做唯一键。</summary>
    private int EnsureApp(AppIdentity app)
    {
        string path = app.FilePath.Length > 0 ? app.FilePath : "~" + app.ProcessName;

        using (var sel = _conn.CreateCommand())
        {
            sel.CommandText = "SELECT id FROM app WHERE file_path=$p;";
            sel.Parameters.AddWithValue("$p", path);
            object? existing = sel.ExecuteScalar();
            if (existing is long id) return (int)id;
        }

        using (var ins = _conn.CreateCommand())
        {
            ins.CommandText = """
                INSERT INTO app(file_path, process, display, created_utc)
                VALUES($p,$pr,$d,$t)
                ON CONFLICT(file_path) DO NOTHING;
                SELECT id FROM app WHERE file_path=$p;
                """;
            ins.Parameters.AddWithValue("$p", path);
            ins.Parameters.AddWithValue("$pr", app.ProcessName);
            ins.Parameters.AddWithValue("$d", app.DisplayName);
            ins.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            object? result = ins.ExecuteScalar();
            return result is long v ? (int)v : 0;
        }
    }

    /// <summary>
    /// 批量写入片段，并让首个片段与库中最后一行续接（若状态与应用相同）。
    /// 返回写入后新的"尾行"，供下一次落库续接使用。
    /// </summary>
    public TailRow? WriteSpans(IReadOnlyList<Span> spans, TailRow? tail, int tzOffsetSeconds)
    {
        if (spans.Count == 0) return tail;

        using var tx = _conn.BeginTransaction();

        // 建临时表，逐行判定该"续接"还是"新建"
        using (var tmp = _conn.CreateCommand())
        {
            tmp.Transaction = tx;
            tmp.CommandText = """
                DROP TABLE IF EXISTS temp._inc;
                CREATE TEMP TABLE _inc(seq INTEGER PRIMARY KEY, s INTEGER, e INTEGER, st INTEGER, key TEXT,
                                       akind TEXT, apath TEXT, aproc TEXT, adisp TEXT);
                """;
            tmp.ExecuteNonQuery();
        }

        foreach (Span s in spans)
        {
            if (s.DurationSeconds <= 0) continue;

            string key = s.MergeKey();
            bool hasApp = s.State == UsageState.Active && s.App != AppIdentity.Unknown;

            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO temp._inc(s,e,st,key,akind,apath,aproc,adisp)
                VALUES($s,$e,$st,$k,$ak,$ap,$apr,$ad);
                """;
            cmd.Parameters.AddWithValue("$s", s.StartUtc);
            cmd.Parameters.AddWithValue("$e", s.EndUtc);
            cmd.Parameters.AddWithValue("$st", (int)s.State);
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$ak", hasApp ? "app" : "none");
            cmd.Parameters.AddWithValue("$ap", hasApp ? s.App.FilePath : "");
            cmd.Parameters.AddWithValue("$apr", hasApp ? s.App.ProcessName : "");
            cmd.Parameters.AddWithValue("$ad", hasApp ? s.App.DisplayName : "");
            cmd.ExecuteNonQuery();
        }

        // 1) 第一行若与尾行同状态同应用，且时间连续，则合并进已有行
        using (var merge = _conn.CreateCommand())
        {
            merge.Transaction = tx;
            merge.CommandText = """
                UPDATE session
                SET end_utc = (SELECT MAX(e) FROM temp._inc WHERE seq = (SELECT MIN(seq) FROM temp._inc))
                WHERE id = $tailId
                  AND $tailKey IS NOT NULL
                  AND $tailKey = (SELECT key FROM temp._inc WHERE seq = (SELECT MIN(seq) FROM temp._inc))
                  AND (SELECT MIN(s) FROM temp._inc) <= end_utc;
                """;
            merge.Parameters.AddWithValue("$tailId", (object?)tail?.Id ?? DBNull.Value);
            merge.Parameters.AddWithValue("$tailKey", (object?)tail?.MergeKey ?? DBNull.Value);
            int merged = merge.ExecuteNonQuery();

            if (merged > 0)
            {
                using var del = _conn.CreateCommand();
                del.Transaction = tx;
                del.CommandText = "DELETE FROM temp._inc WHERE seq = (SELECT MIN(seq) FROM temp._inc);";
                del.ExecuteNonQuery();
            }
        }

        // 2) 确保临时表里出现过的应用都有 app 行
        using (var apps = _conn.CreateCommand())
        {
            apps.Transaction = tx;
            apps.CommandText = """
                INSERT INTO app(file_path, process, display, created_utc)
                SELECT DISTINCT apath, aproc, adisp, $now
                FROM temp._inc WHERE akind = 'app' AND apath <> ''
                ON CONFLICT(file_path) DO NOTHING;
                """;
            apps.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            apps.ExecuteNonQuery();
        }

        // 3) 其余行按顺序插入
        string dayKey = DateTimeOffset.FromUnixTimeSeconds(spans[0].StartUtc).ToLocalTime().ToString("yyyy-MM-dd");
        using (var ins = _conn.CreateCommand())
        {
            ins.Transaction = tx;
            ins.CommandText = """
                INSERT INTO session(start_utc, end_utc, day_key, state, app_id, tz_offset)
                SELECT i.s, i.e, $day, i.st,
                       CASE WHEN i.akind = 'app'
                            THEN (SELECT id FROM app WHERE file_path = i.apath)
                            ELSE NULL END,
                       $tz
                FROM temp._inc i ORDER BY i.seq;
                """;
            ins.Parameters.AddWithValue("$day", dayKey);
            ins.Parameters.AddWithValue("$tz", tzOffsetSeconds);
            ins.ExecuteNonQuery();
        }

        // 4) 心跳
        using (var hb = _conn.CreateCommand())
        {
            hb.Transaction = tx;
            hb.CommandText = "INSERT OR REPLACE INTO heartbeat(ts_utc, spans_written, note) VALUES($t,$n,'');";
            hb.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            hb.Parameters.AddWithValue("$n", spans.Count);
            hb.ExecuteNonQuery();
        }

        // 5) 取回新的尾行
        TailRow? newTail = null;
        using (var last = _conn.CreateCommand())
        {
            last.Transaction = tx;
            last.CommandText = TailQuery;
            using SqliteDataReader r = last.ExecuteReader();
            if (r.Read())
            {
                newTail = new TailRow(
                    r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), (UsageState)r.GetInt32(3), r.GetString(4));
            }
        }

        tx.Commit();
        return newTail;
    }

    /// <summary>
    /// 读取当前最后一行片段（进程重启后恢复续接能力）。
    /// 注意 mergeKey 的拼装：state 是整数列，必须显式 CAST 成文本再拼接。
    /// 直接写 `state || '|' || path` 会被 SQLite 当成按位或运算，得到恒定的 'unknown'。
    /// </summary>
    private const string TailQuery = """
        SELECT s2.id, s2.start_utc, s2.end_utc, s2.state,
               CAST(s2.state AS TEXT) || '|' ||
               CASE WHEN a.file_path IS NOT NULL AND a.file_path <> ''
                    THEN a.file_path
                    ELSE '~' || CASE WHEN a.process IS NOT NULL AND a.process <> ''
                                     THEN a.process ELSE 'unknown' END
               END AS merge_key
        FROM session s2
        LEFT JOIN app a ON a.id = s2.app_id
        ORDER BY s2.start_utc DESC, s2.id DESC
        LIMIT 1;
        """;

    /// <summary>读取当前最后一行片段（进程重启后恢复续接能力）。</summary>
    public TailRow? TryGetTail()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = TailQuery;
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read()
            ? new TailRow(r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), (UsageState)r.GetInt32(3), r.GetString(4))
            : null;
    }

    /// <summary>本地某天 [00:00, 24:00) 对应的 UTC 秒区间。</summary>
    public static (long StartUtc, long EndUtc, string DayKey) LocalDayRange(DateTime localDay)
    {
        DateTime start = localDay.Date;
        DateTime end = start.AddDays(1);
        long s = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUnixTimeSeconds();
        long e = new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end)).ToUnixTimeSeconds();
        return (s, e, start.ToString("yyyy-MM-dd"));
    }

    /// <summary>按状态汇总某天的秒数。区间按 UTC 裁剪，因此跨天会话也能正确计数。</summary>
    public Dictionary<UsageState, long> GetStateTotals(DateTime localDay)
    {
        (long s, long e, _) = LocalDayRange(localDay);

        var result = new Dictionary<UsageState, long>
        {
            [UsageState.Active] = 0,
            [UsageState.Idle] = 0,
            [UsageState.Locked] = 0,
            [UsageState.Off] = 0,
        };

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT state,
                   SUM(MIN(end_utc,$e) - MAX(start_utc,$s)) AS secs
            FROM session
            WHERE end_utc > $s AND start_utc < $e
            GROUP BY state;
            """;
        cmd.Parameters.AddWithValue("$s", s);
        cmd.Parameters.AddWithValue("$e", e);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            int state = r.GetInt32(0);
            long secs = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            if (state is >= 0 and <= 3) result[(UsageState)state] = secs;
        }
        return result;
    }

    /// <summary>某天各应用的活跃时长排行。</summary>
    public List<AppTotal> GetAppTotals(DateTime localDay, int limit = 50)
    {
        (long s, long e, _) = LocalDayRange(localDay);
        var list = new List<AppTotal>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.display, a.file_path,
                   SUM(MIN(s2.end_utc,$e) - MAX(s2.start_utc,$s)) AS secs
            FROM session s2
            JOIN app a ON a.id = s2.app_id
            WHERE s2.state = 0 AND s2.end_utc > $s AND s2.start_utc < $e
            GROUP BY a.id
            HAVING secs > 0
            ORDER BY secs DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$s", s);
        cmd.Parameters.AddWithValue("$e", e);
        cmd.Parameters.AddWithValue("$lim", limit);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AppTotal(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
        }
        return list;
    }

    public DayTotals GetDayTotals(DateTime localDay)
    {
        Dictionary<UsageState, long> t = GetStateTotals(localDay);
        (long s, long e, _) = LocalDayRange(localDay);

        int distinct;
        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(DISTINCT app_id) FROM session
                WHERE state = 0 AND app_id IS NOT NULL AND end_utc > $s AND start_utc < $e;
                """;
            cmd.Parameters.AddWithValue("$s", s);
            cmd.Parameters.AddWithValue("$e", e);
            distinct = Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }

        return new DayTotals(
            t[UsageState.Active], t[UsageState.Idle], t[UsageState.Locked], t[UsageState.Off], distinct);
    }

    /// <summary>最近一次心跳时间；无记录返回 null。</summary>
    public DateTimeOffset? LastHeartbeatUtc()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts_utc) FROM heartbeat;";
        object? v = cmd.ExecuteScalar();
        return v is long ts ? DateTimeOffset.FromUnixTimeSeconds(ts) : null;
    }

    /// <summary>
    /// 最近一次心跳的 Unix 秒；无记录返回 null。
    /// 供采集端回填「上次运行结束 → 本次启动」之间的睡眠时间。
    /// </summary>
    public long? LastHeartbeatUnix()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts_utc) FROM heartbeat;";
        object? v = cmd.ExecuteScalar();
        if (v is null || v is DBNull) return null;
        return Convert.ToInt64(v);
    }

    public (long Rows, long Bytes) StoreInfo()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM session;";
        long rows = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        long bytes = File.Exists(DatabasePath) ? new FileInfo(DatabasePath).Length : 0;
        return (rows, bytes);
    }

    /// <summary>维护：把已过去的某天固化到 day_summary（今天不固化，实时计算）。</summary>
    public void RebuildDaySummary(DateTime localDay)
    {
        (_, _, string dayKey) = LocalDayRange(localDay);

        using var tx = _conn.BeginTransaction();

        using (var del = _conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = "DELETE FROM day_summary WHERE day_key=$d;";
            del.Parameters.AddWithValue("$d", dayKey);
            del.ExecuteNonQuery();
        }

        List<AppTotal> totals = GetAppTotals(localDay, 1000);
        foreach (AppTotal t in totals)
        {
            using var ins = _conn.CreateCommand();
            ins.Transaction = tx;
            ins.CommandText = "INSERT OR REPLACE INTO day_summary(day_key, app_id, seconds) VALUES($d,$a,$s);";
            ins.Parameters.AddWithValue("$d", dayKey);
            ins.Parameters.AddWithValue("$a", t.AppId);
            ins.Parameters.AddWithValue("$s", t.Seconds);
            ins.ExecuteNonQuery();
        }

        tx.Commit();
    }

    /// <summary>
    /// 读取与 [startUtc, endUtc) 相交的所有片段（含无归属的活跃片段）。
    /// 交给上层 ViewModel 做裁剪与聚合——时间轴与柱状图共用这一份原始数据，
    /// 保证两处显示不会因为口径不同而互相矛盾。
    /// </summary>
    public List<(long Start, long End, int State, string Path, string Process, string Display)>
        GetRawSpans(long startUtc, long endUtc)
    {
        var list = new List<(long, long, int, string, string, string)>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.start_utc, s.end_utc, s.state,
                   COALESCE(a.file_path, ''), COALESCE(a.process, ''), COALESCE(a.display, '')
            FROM session s
            LEFT JOIN app a ON a.id = s.app_id
            WHERE s.end_utc > $s AND s.start_utc < $e
            ORDER BY s.start_utc;
            """;
        cmd.Parameters.AddWithValue("$s", startUtc);
        cmd.Parameters.AddWithValue("$e", endUtc);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetInt64(0), r.GetInt64(1), r.GetInt32(2),
                      r.GetString(3), r.GetString(4), r.GetString(5)));
        }
        return list;
    }

    /// <summary>
    /// 某个本地日期区间的状态汇总（含首尾）。区间按 UTC 裁剪，
    /// 因此跨天会话能被正确计数，不会因为"片段属于哪一天"而丢时间。
    /// 周/月视图复用这一个方法，避免逐日查询。
    /// </summary>
    public Dictionary<UsageState, long> GetStateTotalsRange(DateTime fromLocalDate, DateTime toLocalDateInclusive)
    {
        DateTime start = fromLocalDate.Date;
        DateTime endExclusive = toLocalDateInclusive.Date.AddDays(1);
        long s = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUnixTimeSeconds();
        long e = new DateTimeOffset(endExclusive, TimeZoneInfo.Local.GetUtcOffset(endExclusive)).ToUnixTimeSeconds();

        var result = new Dictionary<UsageState, long>
        {
            [UsageState.Active] = 0,
            [UsageState.Idle] = 0,
            [UsageState.Locked] = 0,
            [UsageState.Off] = 0,
        };

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT state, SUM(MIN(end_utc,$e) - MAX(start_utc,$s))
            FROM session
            WHERE end_utc > $s AND start_utc < $e
            GROUP BY state;
            """;
        cmd.Parameters.AddWithValue("$s", s);
        cmd.Parameters.AddWithValue("$e", e);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            int state = r.GetInt32(0);
            long secs = r.IsDBNull(1) ? 0 : r.GetInt64(1);
            if (state is >= 0 and <= 3) result[(UsageState)state] = secs;
        }
        return result;
    }

    /// <summary>某个本地日期区间的应用活跃时长排行。</summary>
    public List<AppTotal> GetAppTotalsRange(DateTime fromLocalDate, DateTime toLocalDateInclusive, int limit = 200)
    {
        DateTime start = fromLocalDate.Date;
        DateTime endExclusive = toLocalDateInclusive.Date.AddDays(1);
        long s = new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)).ToUnixTimeSeconds();
        long e = new DateTimeOffset(endExclusive, TimeZoneInfo.Local.GetUtcOffset(endExclusive)).ToUnixTimeSeconds();

        var list = new List<AppTotal>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.display, a.file_path, SUM(MIN(s2.end_utc,$e) - MAX(s2.start_utc,$s)) AS secs
            FROM session s2
            JOIN app a ON a.id = s2.app_id
            WHERE s2.state = 0 AND s2.end_utc > $s AND s2.start_utc < $e
            GROUP BY a.id
            HAVING secs > 0
            ORDER BY secs DESC
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$s", s);
        cmd.Parameters.AddWithValue("$e", e);
        cmd.Parameters.AddWithValue("$lim", limit);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new AppTotal(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt64(3)));
        }
        return list;
    }

    /// <summary>调试用：导出 app 表内容。</summary>
    /// <summary>
    /// 库里记录过的所有应用路径（去重）。供诊断与颜色提取使用。
    /// </summary>
    public List<string> GetAllAppPaths(int limit = 200)
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT DISTINCT file_path FROM app WHERE file_path <> '' " +
                          "ORDER BY id LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", limit);
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }

    // ---- 应用分类（用户手动设定）----

    /// <summary>读全部手动分类：路径 → 分类名。</summary>
    public Dictionary<string, string> GetAppCategories()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT file_path, category FROM app_category;";
            using SqliteDataReader r = cmd.ExecuteReader();
            while (r.Read())
            {
                string p = r.GetString(0);
                if (p.Length > 0) map[p] = r.GetString(1);
            }
        }
        catch
        {
            // 表还不存在（老库首次运行）时返回空表，不影响使用
        }
        return map;
    }

    /// <summary>设定某个应用的分类。传空分类名等于取消手动设定（恢复自动推断）。</summary>
    public void SetAppCategory(string filePath, string category)
    {
        if (string.IsNullOrEmpty(filePath)) return;

        using var cmd = _conn.CreateCommand();
        if (string.IsNullOrWhiteSpace(category))
        {
            cmd.CommandText = "DELETE FROM app_category WHERE file_path = $p;";
            cmd.Parameters.AddWithValue("$p", filePath);
        }
        else
        {
            cmd.CommandText = """
                INSERT INTO app_category (file_path, category) VALUES ($p, $c)
                ON CONFLICT(file_path) DO UPDATE SET category = excluded.category;
                """;
            cmd.Parameters.AddWithValue("$p", filePath);
            cmd.Parameters.AddWithValue("$c", category.Trim());
        }
        cmd.ExecuteNonQuery();
    }

    /// <summary>清空所有手动分类，恢复全部自动推断。</summary>
    public void ClearAppCategories()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_category;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除某个分类名下的全部手动设定，返回受影响的条数。
    ///
    /// 用于"删除自定义分类"：分类名没了之后，还指着它的手动设定就成了孤儿
    /// ——下拉框里选不中，界面会显示成一个不存在的分类。所以删分类必须连带清掉。
    /// </summary>
    public int DeleteCategoriesByName(string category)
    {
        if (string.IsNullOrWhiteSpace(category)) return 0;

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM app_category WHERE category = $c;";
        cmd.Parameters.AddWithValue("$c", category.Trim());
        return cmd.ExecuteNonQuery();
    }

    public List<string> DumpApps()
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, file_path, process, display FROM app ORDER BY id;";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add($"app#{r.GetInt32(0)}  path='{r.GetString(1)}'  proc='{r.GetString(2)}'  display='{r.GetString(3)}'");
        }
        return list;
    }

    /// <summary>调试用：导出 session 表的关键列（含 app_id 是否为空）。</summary>
    public List<string> DumpSessionRaw(int limit = 50)
    {
        var list = new List<string>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, start_utc, end_utc, state, app_id FROM session ORDER BY id LIMIT {limit};
            """;
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            string appId = r.IsDBNull(4) ? "NULL" : r.GetInt64(4).ToString();
            list.Add($"session#{r.GetInt64(0)}  [{r.GetInt64(1)},{r.GetInt64(2)})  state={r.GetInt32(3)}  app_id={appId}");
        }
        return list;
    }

    /// <summary>调试用：直接列出某天的原始片段，用于交叉核对合并与归属是否正确。</summary>
    public List<string> DumpSessions(DateTime localDay, int limit = 100)
    {
        (long s, long e, _) = LocalDayRange(localDay);
        var list = new List<string>();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT s2.start_utc, s2.end_utc, s2.state,
                   COALESCE(a.display, '-'), COALESCE(a.file_path, '-')
            FROM session s2
            LEFT JOIN app a ON a.id = s2.app_id
            WHERE s2.end_utc > $s AND s2.start_utc < $e
            ORDER BY s2.start_utc
            LIMIT $lim;
            """;
        cmd.Parameters.AddWithValue("$s", s);
        cmd.Parameters.AddWithValue("$e", e);
        cmd.Parameters.AddWithValue("$lim", limit);

        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            long st = r.GetInt64(0), en = r.GetInt64(1);
            int state = r.GetInt32(2);
            string name = r.GetString(3);
            string path = r.GetString(4);
            string label = state switch
            {
                0 => "活跃",
                1 => "空闲",
                2 => "锁屏",
                _ => "熄屏",
            };
            DateTimeOffset ls = DateTimeOffset.FromUnixTimeSeconds(st).ToLocalTime();
            DateTimeOffset le = DateTimeOffset.FromUnixTimeSeconds(en).ToLocalTime();
            list.Add($"{ls:HH:mm:ss} → {le:HH:mm:ss}  {en - st,4}s  {label}  {name}" +
                     (path.Length > 60 ? "  " + Path.GetFileName(path) : ""));
        }
        return list;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            Exec("PRAGMA wal_checkpoint(TRUNCATE);");
        }
        catch
        {
            // 关闭时 checkpoint 失败无所谓
        }
        _conn.Dispose();
    }
}
