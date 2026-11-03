using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 简单的线程安全日志写入器（写入启动器数据目录下的 logs/launcher.log）。
/// </summary>
public sealed class LauncherLogger
{
    private readonly object _gate = new();
    private readonly string _logFile;

    public LauncherLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFile = Path.Combine(logDirectory, "launcher.log");
    }

    public string LogFilePath => _logFile;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] [HYCL] [{PlatformInfo.OperatingSystemName}] {message}";
        lock (_gate)
        {
            try
            {
                File.AppendAllText(_logFile, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // 日志失败不应影响主流程
            }
            catch (UnauthorizedAccessException)
            {
                // 只读目录下静默忽略
            }
        }
    }
}
