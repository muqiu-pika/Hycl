using System.Text.Json;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;
using HyclSecrets = Hycl.Launcher.Core.Config.HyclSecrets;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 社区资源请求器（移植自 PCL-CE GameDownload.DlModRequest 的镜像阶梯）：
/// 按 CommunitySourceSolution 以不同顺序/超时尝试官方源与 mcimirror 镜像源。
/// CurseForge 官方源需要 x-api-key（见 HyclSecrets，默认留空走镜像）。
/// </summary>
public sealed class CompRequester(DownloadClient client, LauncherConfig config, LauncherLogger logger)
{
    /// <summary>当前社区源策略（0 镜像优先 / 1 官方优先 / 2 仅官方），供文件下载 URL 排序复用。</summary>
    public int Solution => config.CommunitySourceSolution;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly TimeSpan ForbiddenRetryDelay = TimeSpan.FromMilliseconds(400);

    /// <summary>CurseForge 官方 API 请求头（Key 留空时官方源必然 403，由镜像兜底——与 PCL-CE 行为一致）。</summary>
    public IReadOnlyDictionary<string, string>? CurseForgeHeaders
        => HyclSecrets.HasCurseForgeApiKey
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-api-key"] = HyclSecrets.CurseForgeApiKey,
                ["Accept"] = "application/json"
            }
            : null;

    /// <summary>构建候选地址与超时阶梯（秒）：对应 PCL-CE DlModRequest 的三种策略。</summary>
    public IReadOnlyList<(string Url, int TimeoutSeconds)> BuildLadder(string officialUrl)
    {
        var mirrorUrl = DownloadSources.GetModApiMirror(officialUrl);
        var hasMirror = !mirrorUrl.Equals(officialUrl, StringComparison.OrdinalIgnoreCase);
        List<(string Url, int TimeoutSeconds)> ladder = config.CommunitySourceSolution switch
        {
            0 => hasMirror
                ? new List<(string, int)> { (mirrorUrl, 5), (mirrorUrl, 10), (officialUrl, 15) }
                : new List<(string, int)> { (officialUrl, 15) },
            2 => new List<(string, int)> { (officialUrl, 5), (officialUrl, 15), (mirrorUrl, 10) },
            _ => hasMirror
                ? new List<(string, int)> { (officialUrl, 5), (mirrorUrl, 5), (officialUrl, 15), (mirrorUrl, 10) }
                : new List<(string, int)> { (officialUrl, 5), (officialUrl, 15) }
        };
        return ladder;
    }

    /// <summary>按镜像阶梯 GET 并解析 JSON；全部失败时抛出含各源错误的异常。</summary>
    public async Task<T> GetJsonAsync<T>(
        string officialUrl,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var ladder = BuildLadder(officialUrl);
        var errors = new List<string>();
        foreach (var (url, timeoutSeconds) in ladder)
        {
            try
            {
                var text = await GetOnceWithForbiddenAsync(url, timeoutSeconds, cancellationToken, headers)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)
                       ?? throw new CompException($"接口返回空内容：{url}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or CompException or CompSourceUnavailableException)
            {
                errors.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new CompException($"社区资源接口请求失败（{string.Join("；", errors)}）");
    }

    /// <summary>按镜像阶梯 POST JSON 并解析（CurseForge 批量文件接口）。</summary>
    public async Task<T> PostJsonAsync<T>(
        string officialUrl,
        string jsonBody,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var mirrorUrl = DownloadSources.GetModApiMirror(officialUrl);
        var hasMirror = !mirrorUrl.Equals(officialUrl, StringComparison.OrdinalIgnoreCase);
        List<(string Url, int TimeoutSeconds)> ladder = config.CommunitySourceSolution switch
        {
            0 when hasMirror => new List<(string, int)> { (mirrorUrl, 5), (mirrorUrl, 10), (officialUrl, 15) },
            2 => new List<(string, int)> { (officialUrl, 5), (officialUrl, 15), (mirrorUrl, 10) },
            _ => hasMirror
                ? new List<(string, int)> { (mirrorUrl, 5), (officialUrl, 5), (officialUrl, 15), (mirrorUrl, 10) }
                : new List<(string, int)> { (officialUrl, 5), (officialUrl, 15) }
        };
        var errors = new List<string>();
        foreach (var (url, timeoutSeconds) in ladder)
        {
            try
            {
                var text = await PostOnceWithForbiddenAsync(url, jsonBody, timeoutSeconds, cancellationToken, headers)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)
                       ?? throw new CompException($"接口返回空内容：{url}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or CompException or CompSourceUnavailableException)
            {
                errors.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new CompException($"社区资源接口请求失败（{string.Join("；", errors)}）");
    }

    /// <summary>
    /// 单次 GET，并按响应体区分 403：
    /// <list type="bullet">
    /// <item>空体 403：判定密钥无效/被拒绝，记一条日志后直接跳到下一个源（通常是镜像）。</item>
    /// <item>非空体 403：边缘节点临时拒绝，退避后重试 1 次；仍失败再回退镜像。</item>
    /// </list>
    /// </summary>
    private async Task<string> GetOnceWithForbiddenAsync(
        string url,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers)
    {
        try
        {
            return await client
                .GetStringOnceAsync(url, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, headers)
                .ConfigureAwait(false);
        }
        catch (ForbiddenResponseException ex) when (ex.IsEmptyBody)
        {
            logger.Warn($"CurseForge 403（空响应体）：判定密钥无效/被拒绝，跳过 {Host(url)} 并回退镜像");
            throw new CompSourceUnavailableException(ex.Message, ex);
        }
        catch (ForbiddenResponseException ex)
        {
            logger.Warn($"边缘节点 403（非空响应体 {ex.BodyLength} 字符）：{Host(url)} 可能为边缘抖动，{ForbiddenRetryDelay.TotalMilliseconds:0}ms 后重试 1 次");
            await Task.Delay(ForbiddenRetryDelay, cancellationToken).ConfigureAwait(false);
            try
            {
                return await client
                    .GetStringOnceAsync(url, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, headers)
                    .ConfigureAwait(false);
            }
            catch (Exception retryException) when (retryException is HttpRequestException or IOException or TaskCanceledException)
            {
                logger.Warn($"边缘 403 重试仍失败，回退镜像：{Host(url)} · {retryException.Message}");
                throw;
            }
        }
    }

    /// <summary>单次 POST，403 处理与 <see cref="GetOnceWithForbiddenAsync"/> 一致。</summary>
    private async Task<string> PostOnceWithForbiddenAsync(
        string url,
        string jsonBody,
        int timeoutSeconds,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? headers)
    {
        try
        {
            return await client
                .PostJsonOnceAsync(url, jsonBody, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, headers)
                .ConfigureAwait(false);
        }
        catch (ForbiddenResponseException ex) when (ex.IsEmptyBody)
        {
            logger.Warn($"CurseForge 403（空响应体）：判定密钥无效/被拒绝，跳过 {Host(url)} 并回退镜像");
            throw new CompSourceUnavailableException(ex.Message, ex);
        }
        catch (ForbiddenResponseException ex)
        {
            logger.Warn($"边缘节点 403（非空响应体 {ex.BodyLength} 字符）：{Host(url)} 可能为边缘抖动，{ForbiddenRetryDelay.TotalMilliseconds:0}ms 后重试 1 次");
            await Task.Delay(ForbiddenRetryDelay, cancellationToken).ConfigureAwait(false);
            try
            {
                return await client
                    .PostJsonOnceAsync(url, jsonBody, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, headers)
                    .ConfigureAwait(false);
            }
            catch (Exception retryException) when (retryException is HttpRequestException or IOException or TaskCanceledException)
            {
                logger.Warn($"边缘 403 重试仍失败，回退镜像：{Host(url)} · {retryException.Message}");
                throw;
            }
        }
    }

    private static string Host(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    public void Log(string message) => logger.Info(message);

    /// <summary>按社区源策略排序后的多源下载（文件 CDN 镜像与 API 镜像共用同一策略）。</summary>
    public async Task<bool> ClientDownloadAsync(
        IReadOnlyList<string> urls,
        string destinationPath,
        long? expectedSize,
        string? hash,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken cancellationToken)
        => await client
            .DownloadFileAsync(urls, destinationPath, expectedSize, hash, progress, cancellationToken)
            .ConfigureAwait(false);
}

/// <summary>社区资源安装进度转发器：把阶段/字节进度聚合为 <see cref="InstallProgress"/> 上报。</summary>
internal sealed class CompProgress : IProgress<InstallProgress>
{
    private readonly IProgress<InstallProgress>? _progress;
    private readonly InstallProgressCounter _counter;

    public CompProgress(IProgress<InstallProgress>? progress)
    {
        _progress = progress;
        _counter = new InstallProgressCounter(progress);
    }

    public void Stage(string stage, int totalFiles = 0) => _counter.Stage(stage, totalFiles);

    public void BeginFile(string name, long expectedBytes) => _counter.BeginFile(name, expectedBytes);

    public void FileBytes(long received, long total) => _counter.FileBytes(received, total);

    public void FileCompleted(long bytes) => _counter.FileCompleted(bytes);

    /// <summary>嵌套安装器直接上报的进度原样转发，避免重复计数。</summary>
    void IProgress<InstallProgress>.Report(InstallProgress value) => _progress?.Report(value);
}

/// <summary>社区资源模块异常。</summary>
public sealed class CompException(string message) : Exception(message);

/// <summary>某个源被判定不可用（如空体 403 = 密钥无效），直接尝试下一个源（镜像）。</summary>
internal sealed class CompSourceUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);
