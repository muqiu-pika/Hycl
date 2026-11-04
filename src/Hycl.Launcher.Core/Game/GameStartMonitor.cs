using System.Diagnostics;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Game;

/// <summary>游戏启动等待结果。</summary>
public sealed class GameStartResult
{
    public required bool Success { get; init; }

    /// <summary>Windows 下是否真正检测到游戏主窗口。</summary>
    public bool WindowDetected { get; init; }

    /// <summary>是否通过日志特征行判定（macOS / Linux 的降级方案）。</summary>
    public bool LogFallback { get; init; }

    public bool TimedOut { get; init; }

    public bool ProcessExited { get; init; }

    public int ExitCode { get; init; }

    public string Message { get; init; } = "";
}

/// <summary>
/// 游戏实时日志的“启动完成”特征检测：窗口枚举不可用（macOS / Linux）时，
/// 以渲染线程初始化完成的日志行作为等价信号，并配合超时兜底。
/// </summary>
public sealed class GameLogSignal
{
    private static readonly string[] ReadyMarkers =
    [
        "Sound engine started",
        "OpenAL initialized",
        "Created: 1024x1024",
        "Created: 512x512"
    ];

    private volatile bool _ready;

    public bool IsReady => _ready;

    public string? MatchedLine { get; private set; }

    public void Feed(string line)
    {
        if (_ready || string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        foreach (var marker in ReadyMarkers)
        {
            if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                MatchedLine = line.Trim();
                _ready = true;
                return;
            }
        }
    }
}

/// <summary>
/// 启动成功判定：对应 PCL-CE Watcher 的窗口检测（Windows 每 120ms 枚举一次进程窗口）。
/// 不把“进程创建成功”当作启动成功；只有窗口真正可见（或日志判定兜底）才返回成功。
/// </summary>
public static class GameStartMonitor
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    /// <summary>日志兜底至少等待的时间，避免过早用日志误判。</summary>
    private static readonly TimeSpan LogFallbackDelay = TimeSpan.FromSeconds(20);

    private static readonly TimeSpan WindowStageDelay = TimeSpan.FromMilliseconds(800);

    public static async Task<GameStartResult> WaitAsync(
        Process process,
        GameLogSignal signal,
        Action<string>? onStage,
        Action<nint>? onWindowFound,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var loaderStageReported = false;
        var windowStageReported = false;
        onStage?.Invoke("正在启动 JVM…");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                return new GameStartResult
                {
                    Success = false,
                    ProcessExited = true,
                    ExitCode = TryGetExitCode(process),
                    Message = $"游戏进程在窗口出现前退出（退出码 {TryGetExitCode(process)}）"
                };
            }

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            if (elapsed > timeout)
            {
                return new GameStartResult
                {
                    Success = false,
                    TimedOut = true,
                    Message = $"启动超时：{timeout.TotalSeconds:0} 秒内未检测到游戏窗口"
                };
            }

            var window = PlatformInfo.IsWindows ? GameWindowProbe.Probe(process.Id) : default;
            if (window.Kind == GameWindowKind.MainWindow)
            {
                onWindowFound?.Invoke(window.Handle);
                onStage?.Invoke("游戏窗口已出现，正在完成初始化…");
                return new GameStartResult
                {
                    Success = true,
                    WindowDetected = true,
                    Message = $"已检测到游戏窗口「{window.Title}」"
                };
            }

            if (window.Kind == GameWindowKind.LoaderWindow && !loaderStageReported)
            {
                loaderStageReported = true;
                onStage?.Invoke("正在加载 Forge 加载器窗口…");
            }

            if (!windowStageReported && elapsed > WindowStageDelay)
            {
                windowStageReported = true;
                onStage?.Invoke("正在加载游戏窗口…");
            }

            // 窗口枚举不可用（或长时间未命中）时，用日志特征行兜底
            if (signal.IsReady && elapsed > LogFallbackDelay)
            {
                return new GameStartResult
                {
                    Success = true,
                    LogFallback = true,
                    Message = $"日志显示游戏已初始化：{signal.MatchedLine}"
                };
            }

            await Task.Delay(120, cancellationToken).ConfigureAwait(false);
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return -1;
        }
    }
}
