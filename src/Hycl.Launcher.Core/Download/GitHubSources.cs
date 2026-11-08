using System.Diagnostics;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// GitHub 加速源池：github.com / raw.githubusercontent.com 等链接按「加速镜像（按实测延迟排序）+ GitHub 原站托底」展开。
/// 镜像可用性随时间与地区变化，因此首用触发后台探测（HEAD 探测目标 + 2.5s 超时），
/// 按响应时间重排、失败沉底；每 10 分钟刷新一次，探测失败不阻塞当前下载（沿用上次顺序）。
/// 初始顺序为 2026-11-08 实测（DoH 解析 + 真实文件拉取）：gh-proxy.com → ghfast.top →
/// ghproxy.link → ghproxy.net → mirror.ghproxy.com；GitHub 原站永远最后。
/// </summary>
public static class GitHubSources
{
    private const int ProbeTimeoutMs = 2500;
    private static readonly TimeSpan ProbeRefreshInterval = TimeSpan.FromMinutes(10);
    private const string ProbeTarget = "https://github.com/robots.txt";

    private static readonly string[] InitialMirrorPrefixes =
    [
        "https://gh-proxy.com/",
        "https://ghfast.top/",
        "https://ghproxy.link/",
        "https://ghproxy.net/",
        "https://mirror.ghproxy.com/",
    ];

    private static readonly HttpClient ProbeClient = CreateProbeClient();

    private static readonly object Lock = new();
    private static string[] _orderedPrefixes = InitialMirrorPrefixes;
    private static DateTimeOffset _probedAt = DateTimeOffset.MinValue;
    private static int _probing;

    /// <summary>是否为需要加速的 GitHub 域链接（Release / Raw / Codeload / 对象存储）。</summary>
    public static bool IsGitHubUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return url.Contains("://github.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("://raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("://objects.githubusercontent.com/", StringComparison.OrdinalIgnoreCase)
            || url.Contains("://codeload.github.com/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把单个 GitHub 链接展开为「5 个加速镜像 + 原站」的有序列表。</summary>
    public static IReadOnlyList<string> Order(string url)
    {
        EnsureProbe();
        lock (Lock)
        {
            var result = new List<string>(_orderedPrefixes.Length + 1);
            foreach (var prefix in _orderedPrefixes)
            {
                result.Add(prefix + url);
            }

            result.Add(url);
            return result;
        }
    }

    /// <summary>
    /// URL 列表展开：GitHub 域链接按镜像池展开（原站托底），其他 URL 原样保留、相对次序不变。
    /// 用于插件下载链路的统一换源入口（EdgeOne / 官方镜像等非 GitHub 源不受影响）。
    /// </summary>
    public static IReadOnlyList<string> Expand(IEnumerable<string?> urls)
    {
        var result = new List<string>();
        foreach (var url in urls)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            if (IsGitHubUrl(url))
            {
                result.AddRange(Order(url));
            }
            else
            {
                result.Add(url);
            }
        }

        return result;
    }

    /// <summary>按需后台探测：顺序缓存过期时触发一次（并发去重）；探测结果只影响后续下载。</summary>
    internal static void EnsureProbe()
    {
        if (DateTimeOffset.UtcNow - _probedAt < ProbeRefreshInterval)
        {
            return;
        }

        if (Interlocked.Exchange(ref _probing, 1) == 1)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var timings = new (string Prefix, double? Milliseconds)[InitialMirrorPrefixes.Length];
                var tasks = InitialMirrorPrefixes.Select(async prefix =>
                {
                    double? milliseconds = null;
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(ProbeTimeoutMs));
                        var started = Stopwatch.GetTimestamp();
                        using var request = new HttpRequestMessage(HttpMethod.Head, prefix + ProbeTarget);
                        using var response = await ProbeClient
                            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                            .ConfigureAwait(false);
                        milliseconds = (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;
                        _ = response.StatusCode;
                    }
                    catch (Exception)
                    {
                        // 超时/连接失败：Milliseconds 保持 null，排序沉底
                    }

                    var index = Array.IndexOf(InitialMirrorPrefixes, prefix);
                    timings[index] = (prefix, milliseconds);
                });
                await Task.WhenAll(tasks).ConfigureAwait(false);

                var ordered = timings
                    .Where(item => item.Milliseconds is not null)
                    .OrderBy(item => item.Milliseconds)
                    .Select(item => item.Prefix)
                    .Concat(timings.Where(item => item.Milliseconds is null).Select(item => item.Prefix))
                    .ToArray();
                lock (Lock)
                {
                    _orderedPrefixes = ordered;
                    _probedAt = DateTimeOffset.UtcNow;
                }
            }
            finally
            {
                Interlocked.Exchange(ref _probing, 0);
            }
        });
    }

    private static HttpClient CreateProbeClient()
    {
        return new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromMilliseconds(ProbeTimeoutMs),
            UseProxy = false,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }
}
