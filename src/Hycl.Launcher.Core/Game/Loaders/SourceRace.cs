using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// 双源竞速（对应 PCL-CE 的 DlSourceLoader）：首选源立即请求、备选源延迟启动，
/// 先成功者胜出并取消另一方；两源都失败时抛出错误（优先"该版本无资源"）。
/// 单源超时由调用方通过 <see cref="GetTextAsync"/> 等限时方法控制，避免旧版本官方源
/// （大页面/不可达域名）把卡片拖到"获取配置失败"。
/// </summary>
internal static class SourceRace
{
    public static async Task<T> RunAsync<T>(
        LauncherLogger logger,
        string label,
        string preferredName,
        Func<CancellationToken, Task<T>> preferred,
        string backupName,
        Func<CancellationToken, Task<T>> backup,
        TimeSpan backupDelay,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        var preferredTask = preferred(token);
        var backupTask = RunBackupAsync(backup, backupDelay, token);

        var pending = new List<Task<T>>(2) { preferredTask, backupTask };
        LoaderUnsupportedException? unsupported = null;
        Exception? error = null;
        while (pending.Count > 0)
        {
            var finished = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(finished);
            try
            {
                var value = await finished.ConfigureAwait(false);
                logger.Info($"{label}：{DescribeWinner(finished, preferredTask, preferredName, backupName)} 源返回数据");
                linked.Cancel();
                return value;
            }
            catch (Exception ex)
            {
                if (ex is LoaderUnsupportedException unsupportedError)
                {
                    unsupported ??= unsupportedError;
                }
                else if (ex is not OperationCanceledException)
                {
                    error ??= ex;
                }
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        throw unsupported ?? error ?? new LoaderMetadataException($"{label}：两个源均未返回数据");
    }

    private static string DescribeWinner<T>(
        Task<T> finished,
        Task<T> preferredTask,
        string preferredName,
        string backupName)
        => ReferenceEquals(finished, preferredTask) ? preferredName : backupName;

    private static async Task<T> RunBackupAsync<T>(
        Func<CancellationToken, Task<T>> backup,
        TimeSpan delay,
        CancellationToken token)
    {
        await Task.Delay(delay, token).ConfigureAwait(false);
        return await backup(token).ConfigureAwait(false);
    }

    /// <summary>单次 GET 文本并限时（超时转 <see cref="LoaderMetadataException"/>，便于竞速快速回退）。</summary>
    public static async Task<string> GetTextAsync(
        DownloadClient client,
        string url,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client
                .GetStringOnceAsync(url, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LoaderMetadataException($"请求超时（{timeoutSeconds}s）：{url}", ex);
        }
    }

    /// <summary>
    /// 按下载源策略决定哪些源优先：镜像优先 = 官方源先等一等；
    /// 官方优先时镜像延迟 2 秒启动（PCL-CE 旧版本的官方源普遍慢/不可达，镜像通常秒回）。
    /// </summary>
    public static bool PreferMirror(DownloadPolicy policy)
        => !(policy.VersionListSource == (int)DownloadSourceMode.OfficialOnly
             || (policy.VersionListSource == (int)DownloadSourceMode.OfficialAuto
                 && policy.MojangFast));
}
