using System.Text;

namespace ScreenTime.App;

/// <summary>极简文件日志。UI 线程低频写入，无需加锁队列。</summary>
internal sealed class Log : IDisposable
{
    private readonly StreamWriter? _writer;
    private bool _disposed;

    public string LogPath { get; }

    public Log(string logPath)
    {
        LogPath = logPath;
        try
        {
            string? dir = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            // 追加模式，UTF-8，自动刷新，便于实时查看
            _writer = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
                new System.Text.UTF8Encoding(false))
            { AutoFlush = true };
        }
        catch
        {
            _writer = null;
        }
    }

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        try { _writer?.WriteLine(line); } catch { /* 忽略日志失败 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _writer?.Dispose(); } catch { /* 忽略 */ }
    }
}
