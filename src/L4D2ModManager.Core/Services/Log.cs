using System.Diagnostics;

namespace L4D2ModManager.Core.Services;

/// <summary>极简日志：写入 %AppData%\L4D2ModManager\logs，并对外抛出事件供 UI 显示。</summary>
public static class Log
{
    private const long MaxLogBytes = 5 * 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _logFile;

    public static string? CurrentLogFile
    {
        get
        {
            lock (Gate)
            {
                EnsureFile();
                return _logFile;
            }
        }
    }

    /// <summary>每条日志产生时触发（已在后台线程）。</summary>
    public static event Action<string>? MessageLogged;

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    public static void Debug(string message) => Write("DEBUG", message, null);

    private static void Write(string level, string message, Exception? exception)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        if (exception != null)
            line += Environment.NewLine + exception;

        lock (Gate)
        {
            try
            {
                EnsureFile();
                if (_logFile != null)
                {
                    var info = new FileInfo(_logFile);
                    if (info.Exists && info.Length > MaxLogBytes)
                    {
                        var backup = _logFile + ".1";
                        if (File.Exists(backup)) File.Delete(backup);
                        File.Move(_logFile, backup);
                    }
                    File.AppendAllText(_logFile, line + Environment.NewLine);
                }
            }
            catch
            {
                // 日志失败不能影响主流程
            }
        }

        System.Diagnostics.Debug.WriteLine(line);
        try
        {
            MessageLogged?.Invoke(line);
        }
        catch
        {
            // 忽略订阅方异常
        }
    }

    private static void EnsureFile()
    {
        if (_logFile != null) return;
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            _logFile = Path.Combine(AppPaths.LogDir, $"l4d2mm-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _logFile = null;
        }
    }
}
