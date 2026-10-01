namespace ScreenTime.App;

/// <summary>
/// 应用使用的路径。
///
/// 数据目录解析顺序（后者逐级降级，任一失败就尝试下一个）：
///   1. 命令行 --datadir 指定的目录；
///   2. 环境变量 SCREENTIME_DATA_DIR；
///   3. %LOCALAPPDATA%\ScreenTime（标准位置）；
///   4. 可执行文件同级的 data 目录（便携模式）。
///
/// 之所以需要降级：某些受限运行环境会拒绝在工作区之外的固定位置创建目录，
/// 此时退到便携模式，保证采集仍然可用。
/// </summary>
internal static class AppPaths
{
    private static string? _resolved;

    /// <summary>命令行 / 环境变量指定的覆盖目录。</summary>
    public static string? OverrideDirectory { get; set; }

    public static string DataDirectory => _resolved ??= ResolveDirectory();

    public static string DatabasePath => Path.Combine(DataDirectory, "usage.db");

    public static string LogPath => Path.Combine(DataDirectory, "app.log");

    /// <summary>
    /// 接管请求文件。第二个实例写入它来请求已运行实例退出。
    ///
    /// 为什么用文件而不是进程间消息：广播消息、命名管道在受限会话里都可能不可达，
    /// 而"读写同一个数据目录里的文件"是已验证可用的最朴素机制，没有额外依赖。
    /// </summary>
    public static string TakeoverRequestPath => Path.Combine(DataDirectory, "takeover.request");

    /// <summary>候选目录及各自的失败原因，供日志诊断"为什么选了这里"。</summary>
    public static IReadOnlyList<string> ResolutionLog => _resolutionLog;
    private static readonly List<string> _resolutionLog = new();

    /// <summary>是否走了降级（未使用标准位置）。</summary>
    public static bool UsedFallback { get; private set; }

    private static string ResolveDirectory()
    {
        var candidates = new List<(string Dir, string Source)>();

        // 标准位置先算出来单独保存。
        // 曾经写成 `candidates[2]` 这样的硬编码索引，但候选列表长度会随
        // 命令行/环境变量是否存在而变化，正常启动时索引 2 恰好落在"便携模式"上，
        // 于是"标准位置"被误认成便携目录、"是否降级"永远是 false（踩过的坑）。
        string standardDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenTime");

        if (!string.IsNullOrWhiteSpace(OverrideDirectory))
            candidates.Add((OverrideDirectory!, "命令行 --datadir"));

        string? env = Environment.GetEnvironmentVariable("SCREENTIME_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add((env!, "环境变量 SCREENTIME_DATA_DIR"));

        candidates.Add((standardDir, "标准位置 %LOCALAPPDATA%"));

        candidates.Add((Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime"),
            "备选位置 %APPDATA%"));

        // 便携模式：放在 exe 同级的 data 目录
        candidates.Add((Path.Combine(AppContext.BaseDirectory, "data"), "便携模式（exe 同级 data）"));

        string standard = standardDir;

        foreach ((string dir, string source) in candidates)
        {
            try
            {
                Directory.CreateDirectory(dir);
                // 真实写一次，确认不是"能建目录但不能写文件"
                string probe = Path.Combine(dir, ".write-probe");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);

                UsedFallback = !string.Equals(dir, standard, StringComparison.OrdinalIgnoreCase);
                _resolutionLog.Add($"选用 [{source}] {dir}");
                if (UsedFallback)
                {
                    _resolutionLog.Add($"注意：未使用标准位置 {standard}，说明它被系统策略拒绝（受限运行环境）");
                }
                return dir;
            }
            catch (Exception ex)
            {
                _resolutionLog.Add($"失败 [{source}] {dir} -> {ex.GetType().Name}: {ex.Message}");
            }
        }

        throw new IOException(
            "找不到可写的数据目录，已尝试：\n  " +
            string.Join("\n  ", _resolutionLog) +
            "\n请用 --datadir <目录> 指定一个可写位置。");
    }
}
