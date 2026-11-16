using System.Runtime;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 重操作（大批量列表渲染 / 更新检测 / 图标补齐）后的内存回收。
/// .NET 的大对象堆（LOH，>85KB 的对象：图标字节流、API 响应缓冲等）只在 gen2 GC 时回收；
/// 启动器是「短时爆发 + 长期空闲」的进程，爆发后几乎不会再触发 gen2，
/// 任务管理器内存因此长期停在峰值不回落。这里在爆发结束后延迟做一次
/// 激进全收集（含 LOH 压实），代价是一次约百毫秒级的 STW，落在后台窗口期。
/// </summary>
public static class GcReclaimer
{
    private static readonly object Gate = new();

    private static CancellationTokenSource? _pending;

    /// <summary>
    /// 请求一次「尘埃落定」收集：连续调用会互相取消（防抖），最后一次调用后
    /// 静默 <paramref name="quietSeconds"/> 才执行，避免在活跃下载/渲染中途打断。
    /// </summary>
    public static void RequestSettle(string reason, LauncherLogger? logger = null, int quietSeconds = 5)
    {
        lock (Gate)
        {
            _pending?.Cancel();
            var cts = new CancellationTokenSource();
            _pending = cts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(quietSeconds), cts.Token).ConfigureAwait(false);
                    var before = GC.GetTotalMemory(false);
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
                    GC.WaitForPendingFinalizers();
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true);
                    var after = GC.GetTotalMemory(true);
                    logger?.Info($"内存回收（{reason}）：托管堆 {before / 1048576.0:0.#}MB → {after / 1048576.0:0.#}MB");
                }
                catch (OperationCanceledException)
                {
                    // 期间又来了新请求：由新请求重新计时
                }
                catch (Exception)
                {
                    // 回收失败不影响功能
                }
            }, cts.Token);
        }
    }
}
