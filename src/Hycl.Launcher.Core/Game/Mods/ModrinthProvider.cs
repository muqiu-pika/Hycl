using System.Text.Json;
using System.Text.Json.Serialization;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Mods;

/// <summary>Modrinth 项目版本条目（v2 API）。</summary>
public sealed class ModrinthVersion
{
    [JsonPropertyName("version_number")]
    public string VersionNumber { get; set; } = "";

    [JsonPropertyName("version_type")]
    public string VersionType { get; set; } = "release";

    [JsonPropertyName("game_versions")]
    public List<string>? GameVersions { get; set; }

    [JsonPropertyName("loaders")]
    public List<string>? Loaders { get; set; }

    [JsonPropertyName("files")]
    public List<ModrinthFile>? Files { get; set; }
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = "";

    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";

    [JsonPropertyName("primary")]
    public bool Primary { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("hashes")]
    public Dictionary<string, string>? Hashes { get; set; }
}

/// <summary>
/// Modrinth 前置 Mod 获取（api.modrinth.com + mcimirror 镜像，仅取主文件 .jar）。
/// 对应 PCL-CE Comp.CompFilesGet 的 Modrinth 路径；CurseForge 需要 API Key（见 <see cref="HyclSecrets"/>，默认留空）。
/// </summary>
public sealed class ModrinthProvider
{
    private readonly DownloadClient _client;
    private readonly LauncherLogger _logger;

    public ModrinthProvider(DownloadClient client, LauncherLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    /// <summary>拉取项目版本列表（镜像优先，官方在后）。</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(
        string slugOrId,
        CancellationToken cancellationToken = default)
    {
        var urls = new List<string>
        {
            $"https://api.modrinth.com/v2/project/{Uri.EscapeDataString(slugOrId)}/version",
            $"https://mod.mcimirror.top/modrinth/v2/project/{Uri.EscapeDataString(slugOrId)}/version"
        };
        Exception? lastError = null;
        foreach (var url in urls)
        {
            try
            {
                var json = await _client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
                var versions = JsonSerializer.Deserialize(json, NetJsonContext.Default.ListModrinthVersion)
                               ?? throw new DownloadException($"Modrinth 版本列表解析失败：{slugOrId}");
                if (versions.Count > 0)
                {
                    return versions;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or DownloadException or JsonException)
            {
                lastError = ex;
                _logger.Warn($"Modrinth 源失败：{url} · {ex.Message}");
            }
        }

        throw new DownloadException($"Modrinth 版本列表获取失败：{slugOrId}", lastError);
    }

    /// <summary>推荐版本：优先匹配游戏版本 + fabric 系加载器 + 稳定版 + 主文件。</summary>
    public ModrinthVersion? Recommend(
        IReadOnlyList<ModrinthVersion> versions,
        string gameVersion,
        string[] loaderNames)
    {
        return versions
            .Where(x => x.VersionType == "release")
            .Where(x => x.GameVersions?.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) == true)
            .Where(x => x.Loaders?.Any(l => loaderNames.Contains(l, StringComparer.OrdinalIgnoreCase)) == true)
            .Where(x => x.Files?.Any(f => f.Primary && f.Url.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) == true)
            .FirstOrDefault()
           ?? versions
               .Where(x => x.GameVersions?.Contains(gameVersion, StringComparer.OrdinalIgnoreCase) == true)
               .Where(x => x.Files?.Any(f => f.Primary && f.Url.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) == true)
               .FirstOrDefault();
    }

    /// <summary>下载版本主文件到目标目录（mcimirror 镜像优先，官方 CDN 兜底）。</summary>
    public async Task<string> DownloadModAsync(
        ModrinthVersion version,
        string modsDirectory,
        IProgress<DownloadByteProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var file = version.Files?.FirstOrDefault(f => f.Primary)
                   ?? version.Files?.FirstOrDefault(f => f.Url.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                   ?? throw new DownloadException($"Modrinth 版本没有可用主文件：{version.VersionNumber}");
        Directory.CreateDirectory(modsDirectory);
        var destination = Path.Combine(modsDirectory, file.Filename);
        var urls = DownloadSources.GetModDownloadUrls(file.Url, 0);
        await _client.DownloadFileAsync(urls, destination, file.Size > 0 ? file.Size : null, null, progress, cancellationToken)
            .ConfigureAwait(false);
        _logger.Info($"前置 Mod 已下载：{file.Filename}");
        return destination;
    }
}
