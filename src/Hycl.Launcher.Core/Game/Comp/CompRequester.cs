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
                var text = await client
                    .GetStringOnceAsync(url, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, headers)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)
                       ?? throw new CompException($"接口返回空内容：{url}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or CompException)
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
                var text = await client
                    .PostJsonOnceAsync(url, jsonBody, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, headers)
                    .ConfigureAwait(false);
                return JsonSerializer.Deserialize<T>(text, JsonOptions)
                       ?? throw new CompException($"接口返回空内容：{url}");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or JsonException or CompException)
            {
                errors.Add($"{new Uri(url).Host}: {ex.Message}");
            }
        }

        throw new CompException($"社区资源接口请求失败（{string.Join("；", errors)}）");
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
internal sealed class CompProgress(IProgress<InstallProgress>? progress) : IProgress<InstallProgress>
{
    private string _stage = "";
    private string _currentItem = "";
    private int _filesDone;
    private int _filesTotal;
    private long _bytesDone;
    private long _bytesTotal;

    public void Stage(string stage, int totalFiles = 0)
    {
        _stage = stage;
        _filesDone = 0;
        _filesTotal = totalFiles;
        _bytesDone = 0;
        _bytesTotal = 0;
        _currentItem = "";
        Report();
    }

    public void BeginFile(string name, long expectedBytes)
    {
        _currentItem = name;
        if (expectedBytes > 0)
        {
            _bytesTotal = expectedBytes;
        }

        Report();
    }

    public void FileBytes(long received, long total)
    {
        _currentItem = total > 0
            ? $"{Math.Round(received / (1024d * 1024d), 1)} MB / {Math.Round(total / (1024d * 1024d), 1)} MB"
            : $"{Math.Round(received / (1024d * 1024d), 1)} MB";
        _bytesDone = received;
        _bytesTotal = total;
        Report();
    }

    public void FileCompleted(long bytes)
    {
        _filesDone++;
        _currentItem = "";
        Report();
    }

    private void Report()
        => progress?.Report(new InstallProgress(_stage, _currentItem, _filesDone, _filesTotal, _bytesDone, _bytesTotal));

    void IProgress<InstallProgress>.Report(InstallProgress value) => progress?.Report(value);
}

/// <summary>社区资源模块异常。</summary>
public sealed class CompException(string message) : Exception(message);
