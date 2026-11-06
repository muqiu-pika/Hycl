using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// Fabric 系（Fabric / Quilt / LegacyFabric）版本列表。
/// Fabric 与 Quilt 有 BMCLAPI 镜像；LegacyFabric 仅官方源。
/// </summary>
public sealed class FabricLikeProvider(
    LoaderKind kind,
    string metaBaseUrl,
    string? mirrorBaseUrl,
    string cacheKey,
    DownloadClient client,
    LauncherLogger logger,
    LoaderMetadataCache cache) : ILoaderVersionProvider
{
    public LoaderKind Kind { get; } = kind;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var meta = await cache.GetOrLoadAsync(
            $"{cacheKey}:meta",
            async token => await FetchMetaAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        // 该 MC 版本必须在 meta 的 game 数组里（否则 profile json 也不会存在）
        var gameExists = meta.Game?.Any(x => x.Version.Equals(minecraftVersion, StringComparison.OrdinalIgnoreCase)) == true;
        if (!gameExists)
        {
            throw new LoaderUnsupportedException($"{Kind} 不支持 Minecraft {minecraftVersion}");
        }

        return meta.Loader!
            .Select(loader => new LoaderVersionEntry
            {
                Kind = Kind,
                Inherit = minecraftVersion,
                VersionName = loader.Version.Replace("+build", ""),
                IsStable = loader.Stable,
                Payload = loader
            })
            .ToList();
    }

    /// <summary>该 MC 版本下最新的稳定 loader 版本；无稳定版时回退最新版本。</summary>
    public async Task<LoaderVersionEntry?> GetLatestAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsForAsync(minecraftVersion, policy, cancellationToken).ConfigureAwait(false);
        return versions.FirstOrDefault(x => x.IsStable) ?? versions.FirstOrDefault();
    }

    /// <summary>meta 声明支持的 Minecraft 版本（game 数组，从新到旧）。</summary>
    public async Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var meta = await cache.GetOrLoadAsync(
            $"{cacheKey}:meta",
            async token => await FetchMetaAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return meta.Game?.Select(x => x.Version).ToList() ?? [];
    }

    private async Task<FabricMetaVersions> FetchMetaAsync(DownloadPolicy policy, CancellationToken cancellationToken)
    {
        var mirrorUrl = mirrorBaseUrl;
        if (mirrorUrl is null)
        {
            return await FetchFromAsync(metaBaseUrl, cancellationToken).ConfigureAwait(false);
        }

        var preferMirror = SourceRace.PreferMirror(policy);
        var mirror = (CancellationToken token) => FetchFromAsync(mirrorUrl, token);
        var official = (CancellationToken token) => FetchFromAsync(metaBaseUrl, token);
        return preferMirror
            ? await SourceRace.RunAsync(
                logger, Kind.ToString(),
                "BMCLAPI", mirror,
                "官方源", official,
                TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false)
            : await SourceRace.RunAsync(
                logger, Kind.ToString(),
                "官方源", official,
                "BMCLAPI", mirror,
                TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
    }

    private async Task<FabricMetaVersions> FetchFromAsync(string baseUrl, CancellationToken cancellationToken)
    {
        // 单源 15 秒上限：镜像 404/官方超时都能快速回退，避免列表长时间停留在加载状态
        FabricMetaVersions meta;
        try
        {
            meta = await client
                .GetJsonOnceAsync<FabricMetaVersions>(
                    $"{baseUrl}/versions",
                    TimeSpan.FromSeconds(15),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new LoaderMetadataException($"{Kind} 版本列表请求超时：{baseUrl}", ex);
        }

        if (!meta.IsValid)
        {
            throw new LoaderMetadataException($"{Kind} meta 数据不完整（缺少 game/loader/installer）");
        }

        return meta;
    }
}
