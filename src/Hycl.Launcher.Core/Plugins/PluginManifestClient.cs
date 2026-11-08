using System.Text.Json;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Core.Plugins;

/// <summary>
/// Hycl-online 清单拉取：多 URL 兜底 + 5 分钟缓存。
/// 自定义源（Config.OnlineManifestUrl）优先，其次默认 raw 直链 + ghproxy 镜像。
/// </summary>
public sealed class PluginManifestClient
{
    private const int CacheLifetimeSeconds = 300;

    private readonly DownloadClient _downloadClient;
    private readonly LauncherConfig _config;
    private readonly LauncherLogger _logger;

    private HyclOnlineManifest? _cached;
    private DateTimeOffset _cachedAt;

    public PluginManifestClient(DownloadClient downloadClient, LauncherConfig config, LauncherLogger logger)
    {
        _downloadClient = downloadClient;
        _config = config;
        _logger = logger;
    }

    /// <summary>当前生效的清单 URL 列表：自定义源 → EdgeOne 托管（若已部署）→ GitHub raw（经镜像池展开，原站托底）。</summary>
    public IReadOnlyList<string> ManifestUrls
    {
        get
        {
            var custom = _config.OnlineManifestUrl?.Trim();
            var candidates = string.IsNullOrEmpty(custom)
                ? HyclOnlineDefaults.DefaultManifestUrls
                : [custom, ..HyclOnlineDefaults.DefaultManifestUrls];
            return GitHubSources.Expand(candidates);
        }
    }

    public HyclOnlineManifest? Cached => _cached;

    /// <summary>拉取清单；缓存有效期内直接返回缓存。全部源失败时返回 null（不抛出，UI 显示离线态）。</summary>
    public async Task<HyclOnlineManifest?> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is not null
            && DateTimeOffset.UtcNow - _cachedAt < TimeSpan.FromSeconds(CacheLifetimeSeconds))
        {
            return _cached;
        }

        try
        {
            var json = await _downloadClient
                .GetStringAsync(ManifestUrls, cancellationToken)
                .ConfigureAwait(false);
            var manifest = JsonSerializer.Deserialize(json, NetJsonContext.Default.HyclOnlineManifest);
            if (manifest is null || manifest.SchemaVersion != 1)
            {
                _logger.Warn("[插件] 清单 schemaVersion 不受支持，忽略本次结果");
                return _cached;
            }

            _cached = manifest;
            _cachedAt = DateTimeOffset.UtcNow;
            _logger.Info($"[插件] 清单已刷新：{(manifest.Plugins ?? []).Count} 个插件（{ManifestUrls[0]}）");
            return manifest;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warn("[插件] 清单拉取失败：" + LogSanitizer.Sanitize(ex.Message));
            return _cached;
        }
    }

    /// <summary>强制下次刷新（安装/卸载后调用）。</summary>
    public void Invalidate() => _cachedAt = DateTimeOffset.MinValue;
}
