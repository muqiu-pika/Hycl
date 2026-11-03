namespace Hycl.Launcher.Core.Download;

/// <summary>
/// 官方源延迟探测状态（对应 PCL-CE 的 _dlPreferMojang）：
/// 版本列表加载时测量 Mojang 响应耗时，4 秒内应答则视为官方源畅通，
/// "官方优先（自动切换）" 档在有效期内直接优先官方。
/// </summary>
public static class DownloadSourceMonitor
{
    private static DateTimeOffset _mojangFastUntil = DateTimeOffset.MinValue;

    /// <summary>探测结果有效时长。</summary>
    public static TimeSpan ProbeValidity { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>官方延迟阈值：低于该值认为官方源畅通。</summary>
    public static TimeSpan FastThreshold { get; set; } = TimeSpan.FromSeconds(4);

    public static bool MojangFast => DateTimeOffset.UtcNow < _mojangFastUntil;

    /// <summary>记录一次 Mojang 延迟观测。</summary>
    public static void NotifyMojangLatency(TimeSpan elapsed)
    {
        _mojangFastUntil = elapsed <= FastThreshold
            ? DateTimeOffset.UtcNow + ProbeValidity
            : DateTimeOffset.MinValue;
    }

    /// <summary>测试用：清空探测状态。</summary>
    public static void Reset() => _mojangFastUntil = DateTimeOffset.MinValue;
}
