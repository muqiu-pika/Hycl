using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// HTTP 下载封装：多源 URL 按序尝试、并发安全、HTTP Range 断点续传（.hycl-dl 临时文件）、
/// 重试与 SHA-1 校验。URL 列表顺序由 <see cref="DownloadSources"/> 按下载源策略生成。
/// </summary>
public sealed class DownloadClient : IDisposable
{
    /// <summary>断点续传临时文件后缀（参考 HMCL 的 .part 方案）。</summary>
    public const string TempSuffix = ".hycl-dl";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly LauncherLogger _logger;
    private bool _disposed;

    public DownloadClient(LauncherLogger logger, int retryCount = 3)
    {
        _logger = logger;
        RetryCount = Math.Clamp(retryCount, 0, 10);
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(30),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 64
        })
        {
            // 头部超时单独控制；流式读取用每次读取超时，避免大文件被整体超时掐断
            Timeout = Timeout.InfiniteTimeSpan,
            // 统一 HTTP/1.1：部分镜像源（如 meta.legacyfabric.net）对 HTTP/2 握手异常
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            DefaultRequestVersion = HttpVersion.Version11
        };
        // Mozilla 前缀：meta.legacyfabric.net 等源会拒绝非浏览器 UA
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) HYCL/1.0 (Hyplay Craft Launcher)");
    }

    public int RetryCount { get; set; }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
        => await GetStringAsync([url], cancellationToken).ConfigureAwait(false);

    /// <summary>单次请求（不重试），用于列表竞速等需要自行控制超时的场景。</summary>
    public async Task<string> GetStringOnceAsync(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var response = await _httpClient
            .SendAsync(new HttpRequestMessage(HttpMethod.Get, url), HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>按顺序尝试多个 URL 获取文本。</summary>
    public async Task<string> GetStringAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        Exception? lastError = null;
        var retryCount = Math.Clamp(RetryCount, 0, 10);
        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var url in DedupeUrls(urls))
            {
                try
                {
                    using var response = await GetResponseAsync(new HttpRequestMessage(HttpMethod.Get, url), cancellationToken, 30)
                        .ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
                {
                    lastError = ex;
                }
            }

            await DelayBeforeRetryAsync(attempt, retryCount, cancellationToken).ConfigureAwait(false);
        }

        throw new DownloadException($"请求失败：{urls.FirstOrDefault()}", lastError);
    }

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
        => await GetJsonAsync<T>([url], cancellationToken).ConfigureAwait(false);

    public async Task<T> GetJsonAsync<T>(IReadOnlyList<string> urls, CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync(urls, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value is null)
            {
                throw new DownloadException($"接口返回空内容：{urls.FirstOrDefault()}");
            }

            return value;
        }
        catch (JsonException ex)
        {
            throw new DownloadException($"解析接口数据失败：{urls.FirstOrDefault()}", ex);
        }
    }

    /// <summary>
    /// 下载文件；若目标已存在且大小匹配则跳过。返回是否真正下载。
    /// 多个 URL 按优先级依次尝试；中断后保留 .hycl-dl 临时文件，下次经 HTTP Range 续传。
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string url,
        string destinationPath,
        long? expectedSize = null,
        string? expectedSha1 = null,
        IProgress<DownloadByteProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => await DownloadFileAsync([url], destinationPath, expectedSize, expectedSha1, progress, cancellationToken)
            .ConfigureAwait(false);

    public async Task<bool> DownloadFileAsync(
        IReadOnlyList<string> urls,
        string destinationPath,
        long? expectedSize = null,
        string? expectedSha1 = null,
        IProgress<DownloadByteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(destinationPath))
        {
            var length = new FileInfo(destinationPath).Length;
            if (expectedSize is null || length == expectedSize)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedSha1) && ComputeSha1(destinationPath).Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var candidateUrls = DedupeUrls(urls);
        var tempFile = destinationPath + TempSuffix;
        Exception? lastError = null;
        var retryCount = Math.Clamp(RetryCount, 0, 10);
        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resumeFrom = GetResumeLength(tempFile);
            foreach (var url in candidateUrls)
            {
                try
                {
                    var state = new ResumeState(resumeFrom);
                    var completed = await DownloadWithResumeAsync(
                            url,
                            tempFile,
                            state,
                            expectedSize,
                            expectedSha1,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    resumeFrom = state.Offset;
                    if (completed)
                    {
                        File.Move(tempFile, destinationPath, true);
                        return true;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (ValidationFailure)
                {
                    // 校验失败时丢弃临时文件重新下载（续传坏数据没有意义）
                    TryDelete(tempFile);
                    resumeFrom = 0;
                    lastError = new IOException($"校验失败：{url}");
                    _logger.Warn(lastError.Message);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
                {
                    lastError = ex;
                    _logger.Warn($"下载失败（第 {attempt + 1} 次）：{url} · {ex.Message}");
                }
            }

            await DelayBeforeRetryAsync(attempt, retryCount, cancellationToken).ConfigureAwait(false);
        }

        throw new DownloadException($"下载失败：{candidateUrls.FirstOrDefault()}", lastError);
    }

    /// <summary>断点续传状态（async 方法无法使用 ref 参数，用可变对象承载）。</summary>
    private sealed class ResumeState(long offset)
    {
        public long Offset { get; set; } = offset;
    }

    /// <summary>单次下载；返回 true 表示完成并校验通过，false 表示应换下一个 URL。</summary>
    private async Task<bool> DownloadWithResumeAsync(
        string url,
        string tempFile,
        ResumeState state,
        long? expectedSize,
        string? expectedSha1,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resumeFrom = state.Offset;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (resumeFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumeFrom, null);
        }

        using var response = await GetResponseAsync(request, cancellationToken, 45).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 临时文件已超出真实大小：丢弃重下
            TryDelete(tempFile);
            state.Offset = 0;
            return false;
        }

        response.EnsureSuccessStatusCode();
        var appending = response.StatusCode == HttpStatusCode.PartialContent && resumeFrom > 0;
        if (!appending)
        {
            state.Offset = 0;
        }

        var total = expectedSize
                    ?? (response.Content.Headers.ContentLength is { } contentLength
                        ? contentLength + resumeFrom
                        : 0);
        var received = resumeFrom;

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var target = new FileStream(
                         tempFile,
                         appending ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         true))
        {
            var buffer = new byte[81920];
            using var readTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var readLinked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, readTimeout.Token);
            while (true)
            {
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, readLinked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // 60 秒没有读到任何数据：视为连接假死，交给上层换源/重试
                    throw new IOException($"读取超时：{url}");
                }

                if (read <= 0)
                {
                    break;
                }

                readTimeout.CancelAfter(TimeSpan.FromSeconds(60));
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                received += read;
                progress?.Report(new DownloadByteProgress(received, total));
            }
        }

        if (expectedSize is not null && received != expectedSize)
        {
            TryDelete(tempFile);
            throw new ValidationFailure($"文件大小不符（期望 {expectedSize}，实际 {received}）：{url}");
        }

        if (!string.IsNullOrWhiteSpace(expectedSha1))
        {
            var actual = ComputeSha1(tempFile);
            if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(tempFile);
                throw new ValidationFailure($"SHA-1 校验失败：{url}");
            }
        }

        return true;
    }

    /// <summary>发送请求；headers 超时后取消（内容流由每次读取超时保护）。</summary>
    private async Task<HttpResponseMessage> GetResponseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        int headerTimeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(headerTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
    }

    private static long GetResumeLength(string tempFile)
    {
        try
        {
            return File.Exists(tempFile) ? Math.Max(0, new FileInfo(tempFile).Length) : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> DedupeUrls(IReadOnlyList<string> urls)
        => urls.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static string ComputeSha1(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA1.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task DelayBeforeRetryAsync(int attempt, int retryCount, CancellationToken cancellationToken)
    {
        if (attempt >= retryCount)
        {
            return;
        }

        var delay = TimeSpan.FromMilliseconds(Math.Min(3000, 400 * (attempt + 1)));
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 忽略临时文件清理失败
        }
        catch (UnauthorizedAccessException)
        {
            // 忽略占用导致的清理失败
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    /// <summary>内容校验失败（大小/哈希），需要丢弃临时文件重下。</summary>
    private sealed class ValidationFailure : Exception
    {
        public ValidationFailure(string message)
            : base(message)
        {
        }
    }
}

public sealed class DownloadException : Exception
{
    public DownloadException(string message)
        : base(message)
    {
    }

    public DownloadException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
