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
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        await ThrowIfForbiddenAsync(response, url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>POST JSON 并返回文本（社区资源的 CurseForge 批量文件接口需要）。</summary>
    public async Task<string> PostJsonOnceAsync(
        string url,
        string jsonBody,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json")
        };
        if (headers is not null)
        {
            foreach (var (key, value) in headers)
            {
                request.Headers.TryAddWithoutValidation(key, value);
            }
        }

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token)
            .ConfigureAwait(false);
        await ThrowIfForbiddenAsync(response, url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 403 分类：空响应体 = 密钥无效（不可重试），非空响应体 = 边缘节点临时拒绝（可退避重试）。
    /// 必须在 EnsureSuccessStatusCode 之前调用，否则拿不到响应体长度。
    /// </summary>
    private static async Task ThrowIfForbiddenAsync(
        HttpResponseMessage response,
        string url,
        CancellationToken cancellationToken)
    {
        if (response.StatusCode != HttpStatusCode.Forbidden)
        {
            return;
        }

        var bodyLength = 0;
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            bodyLength = body.Length;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            bodyLength = -1;
        }

        throw new ForbiddenResponseException(url, bodyLength);
    }

    /// <summary>按顺序尝试多个 URL 获取文本（整体超时 90 秒，防止响应体停摆挂起）。</summary>
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
                    using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    overall.CancelAfter(TimeSpan.FromSeconds(90));
                    using var response = await SendWithRedirectAsync(url, overall.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    return await response.Content.ReadAsStringAsync(overall.Token).ConfigureAwait(false);
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

        throw new DownloadException(
            $"请求失败：{urls.FirstOrDefault()}（{DescribeLastError(lastError)}）",
            lastError);
    }

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
        => await GetJsonAsync<T>([url], cancellationToken).ConfigureAwait(false);

    /// <summary>单次请求并解析 JSON（不重试、单源超时），用于镜像/官方串行竞速的加载器列表。</summary>
    public async Task<T> GetJsonOnceAsync<T>(
        string url,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var json = await GetStringOnceAsync(url, timeout, cancellationToken).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<T>(json, JsonOptions);
            if (value is null)
            {
                throw new DownloadException($"接口返回空内容：{url}");
            }

            return value;
        }
        catch (JsonException ex)
        {
            throw new DownloadException($"解析接口数据失败：{url}", ex);
        }
    }

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
    /// 多个 URL 按优先级依次尝试；网络中断后保留 .hycl-dl 临时文件供下次续传，
    /// 用户取消时删除该临时文件并关闭文件句柄。
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

            if (!string.IsNullOrWhiteSpace(expectedSha1) && ValidateHash(destinationPath, expectedSha1))
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
        try
        {
            for (var attempt = 0; attempt <= retryCount; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var resumeFrom = GetResumeLength(tempFile);
                foreach (var url in candidateUrls)
                {
                    // 上一次尝试可能在流中断前已把数据追加进临时文件（超过 resumeFrom）：
                    // 先截断回当前续传偏移，否则 Range 续传 + Append 会拼出带空洞的坏文件
                    TruncateTo(tempFile, resumeFrom);
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
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 用户取消：文件流已随 using 关闭，这里删除本次未完成的续传临时文件，避免留下半截文件
            TryDelete(tempFile);
            throw;
        }

        throw new DownloadException(
            $"下载失败：{candidateUrls.FirstOrDefault()}（{DescribeLastError(lastError)}）",
            lastError);
    }

    /// <summary>把续传临时文件截断到指定长度（更长说明上次中断前多写了数据）。</summary>
    private static void TruncateTo(string tempFile, long length)
    {
        try
        {
            if (length >= 0 && File.Exists(tempFile) && new FileInfo(tempFile).Length > length)
            {
                using var stream = new FileStream(tempFile, FileMode.Open, FileAccess.Write, FileShare.None);
                stream.SetLength(length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 截断失败不致命：下一个 URL 会以 416/大小校验兜底
        }
    }

    private static string DescribeLastError(Exception? lastError)
        => lastError is null
            ? "无可用下载源"
            : lastError.GetType() == typeof(IOException) || lastError.GetType() == typeof(UnauthorizedAccessException)
                ? lastError.Message
                : $"{lastError.GetType().Name}: {lastError.Message}";

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
                        ? contentLength + (appending ? resumeFrom : 0)
                        : 0);
        // 服务器不支持 Range 时返回 200 全量：文件已用 FileMode.Create 截断重写，必须从 0 计数
        var received = appending ? resumeFrom : 0;

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

        if (!string.IsNullOrWhiteSpace(expectedSha1) && !ValidateHash(tempFile, expectedSha1))
        {
            TryDelete(tempFile);
            throw new ValidationFailure($"文件校验失败：{url}");
        }

        return true;
    }

    /// <summary>按哈希长度自动识别算法校验（32=MD5、40=SHA-1、64=SHA-256；对应 PCL-CE FileChecker）。</summary>
    private static bool ValidateHash(string filePath, string expectedHash)
    {
        using HashAlgorithm? algorithm = expectedHash.Length switch
        {
            32 => MD5.Create(),
            64 => SHA256.Create(),
            _ => null
        };
        using var stream = File.OpenRead(filePath);
        var actual = algorithm is null
            ? Convert.ToHexString(SHA1.HashData(stream))
            : Convert.ToHexString(algorithm.ComputeHash(stream));
        return actual.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>发送请求；headers 超时后取消（内容流由每次读取超时保护）。</summary>
    private async Task<HttpResponseMessage> GetResponseAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken,
        int headerTimeoutSeconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(headerTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        return await SendWithRedirectAsync(request, linked.Token)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// GET 请求并手动跟随重定向。HttpClient 默认拒绝 HTTPS→HTTP 降级跳转
    /// （dl.liteloader.com / 部分 BMCLAPI 路径会 302 到 http://redirector...），这里统一兜底。
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRedirectAsync(string url, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendWithRedirectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendWithRedirectAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        for (var hops = 0;
             (int)response.StatusCode is 301 or 302 or 307 or 308
             && response.Headers.Location is { } location
             && hops < 5;
             hops++)
        {
            var currentUri = request.RequestUri
                             ?? throw new DownloadException("请求缺少地址");
            var nextUri = location.IsAbsoluteUri ? location : new Uri(currentUri, location);
            _logger.Warn($"跟随重定向：{currentUri} → {nextUri}");
            response.Dispose();
            var rangeHeader = request.Headers.Range;
            request.Dispose();
            request = new HttpRequestMessage(HttpMethod.Get, nextUri);
            if (rangeHeader is not null)
            {
                request.Headers.Range = rangeHeader;
            }

            response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        return response;
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

        // 随机抖动退避（对齐 PCL-CE FileDownloader）：避免并发任务同步重试打爆同一镜像
        var delay = TimeSpan.FromMilliseconds(Math.Min(4000, Random.Shared.Next(300, 500 + (attempt + 1) * 300)));
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

/// <summary>
/// HTTP 403 响应（保留响应体长度）：CurseForge 的“密钥无效”返回空体，
/// 边缘节点临时拒绝返回非空体文本，调用方据此区分“直接回退镜像”与“退避重试”。
/// </summary>
public sealed class ForbiddenResponseException(string url, int bodyLength)
    : HttpRequestException($"403（响应体 {bodyLength} 字符）：{url}")
{
    public string Url { get; } = url;

    public int BodyLength { get; } = bodyLength;

    /// <summary>空响应体（或读取失败）判定为密钥无效。</summary>
    public bool IsEmptyBody => BodyLength <= 0;
}
