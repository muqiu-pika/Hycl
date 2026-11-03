using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// HTTP 下载封装：并发安全、断点式 .part 写入、重试与 SHA-1 校验。
/// </summary>
public sealed class DownloadClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

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
            Timeout = RequestTimeout
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("HYCL/1.0 (Hyplay Craft Launcher; cross-platform)");
    }

    public int RetryCount { get; set; }

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken = default)
    {
        var retryCount = Math.Clamp(RetryCount, 0, 10);
        Exception? lastError = null;
        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var response = await _httpClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
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
                await DelayBeforeRetryAsync(attempt, retryCount, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new DownloadException($"请求失败：{url}", lastError);
    }

    public async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken = default)
    {
        var json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 下载文件；若目标已存在且大小匹配则跳过。返回是否真正下载。
    /// </summary>
    public async Task<bool> DownloadFileAsync(
        string url,
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
        }

        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Exception? lastError = null;
        var retryCount = Math.Clamp(RetryCount, 0, 10);
        for (var attempt = 0; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partFile = destinationPath + ".part";
            try
            {
                using var response = await _httpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var total = expectedSize ?? response.Content.Headers.ContentLength ?? 0;
                var received = 0L;

                await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var target = new FileStream(partFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                    {
                        await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                        received += read;
                        progress?.Report(new DownloadByteProgress(received, total));
                    }
                }

                if (expectedSize is not null && received != expectedSize)
                {
                    throw new IOException($"文件大小不符（期望 {expectedSize}，实际 {received}）：{url}");
                }

                if (expectedSha1 is not null && !string.IsNullOrWhiteSpace(expectedSha1))
                {
                    var actual = ComputeSha1(partFile);
                    if (!actual.Equals(expectedSha1, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new IOException($"SHA-1 校验失败：{url}");
                    }
                }

                File.Move(partFile, destinationPath, true);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                TryDelete(partFile);
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or UnauthorizedAccessException)
            {
                lastError = ex;
                TryDelete(partFile);
                _logger.Warn($"下载失败（第 {attempt + 1} 次）：{url} · {ex.Message}");
                await DelayBeforeRetryAsync(attempt, retryCount, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new DownloadException($"下载失败：{url}", lastError);
    }

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
