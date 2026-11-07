using System.Diagnostics;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 启动与切页耗时埋点：仅在设置环境变量 <c>HYCL_PERF_TRACE=1</c> 时写入 perf.log，
/// 正常使用不产生任何文件写入，便于性能对比时复核同一组数值。
/// </summary>
public static class PerfTrace
{
    private static readonly bool Enabled = Environment.GetEnvironmentVariable("HYCL_PERF_TRACE") == "1";

    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private static readonly object Gate = new();

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "HYCL",
        "perf.log");

    public static long Now => Clock.ElapsedMilliseconds;

    public static void Mark(string name)
    {
        if (!Enabled)
        {
            return;
        }

        Write($"{Clock.ElapsedMilliseconds}|{name}");
    }

    public static void MarkSince(string name, long start)
    {
        if (!Enabled)
        {
            return;
        }

        Write($"{Clock.ElapsedMilliseconds}|{name}|elapsed={Clock.ElapsedMilliseconds - start}");
    }

    private static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // 埋点失败不影响启动
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
