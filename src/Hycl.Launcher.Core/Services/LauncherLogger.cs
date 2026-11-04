using System.Text;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 简单的线程安全日志写入器（写入启动器数据目录下的 logs/launcher.log）。
/// 日志超过 2MB 时在启动期裁剪为尾部 512KB，避免长期使用后无限膨胀。
/// </summary>
public sealed class LauncherLogger
{
    private const long TrimThresholdBytes = 2 * 1024 * 1024;
    private const int TrimKeepTailBytes = 512 * 1024;

    private readonly object _gate = new();
    private readonly string _logFile;

    public LauncherLogger(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logFile = Path.Combine(logDirectory, "launcher.log");
        TrimIfNeeded();
    }

    public string LogFilePath => _logFile;

    public void Info(string message) => Write("INFO", message);

    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? exception = null)
    {
        Write("ERROR", exception is null ? message : $"{message}{Environment.NewLine}{exception}");
    }

    /// <summary>日志体积超阈值时裁剪：保留尾部内容并写入分隔标记。</summary>
    private void TrimIfNeeded()
    {
        try
        {
            if (!File.Exists(_logFile) || new FileInfo(_logFile).Length <= TrimThresholdBytes)
            {
                return;
            }

            var allBytes = File.ReadAllBytes(_logFile);
            var tail = allBytes[^TrimKeepTailBytes..];

            // 对齐到下一个换行，避免裁出一行残缺的 UTF-8
            var newline = (byte)'\n';
            var start = 0;
            while (start < tail.Length && tail[start] != newline)
            {
                start++;
            }

            tail = tail[(start + 1)..];
            var marker = Encoding.UTF8.GetBytes(
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff}] [INFO] [HYCL] 旧日志已自动裁剪（原文件超过 2MB，仅保留最近 512KB）{Environment.NewLine}");
            File.WriteAllBytes(_logFile, marker.Concat(tail).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 裁剪失败不影响主流程
        }
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
