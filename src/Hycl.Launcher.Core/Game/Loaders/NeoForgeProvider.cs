using System.Text.Json;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// NeoForge 版本列表（移植自 PCL-CE NeoForgeCleanroom.cs）：
/// 官方 maven.neoforged.net 与 BMCLAPI 双源，同时取 neoforge 与旧 forge（1.20.1）两个包；
/// 47.1.82 为不可下载的黑名单版本。
/// </summary>
public sealed partial class NeoForgeProvider(
    DownloadClient client,
    LauncherLogger logger,
    LoaderMetadataCache cache) : ILoaderVersionProvider
{
    private const string BlacklistedVersion = "47.1.82";

    public LoaderKind Kind => LoaderKind.NeoForge;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await cache.GetOrLoadAsync(
            "neoforge:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return all.Where(x => x.Inherit == minecraftVersion).ToList();
    }

    /// <summary>全部 NeoForge 版本（从新到旧）。</summary>
    public async Task<IReadOnlyList<LoaderVersionEntry>> GetAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
        => await cache.GetOrLoadAsync(
            "neoforge:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(policy, cancellationToken).ConfigureAwait(false);
        return all.Select(x => x.Inherit).Distinct(StringComparer.Ordinal).ToList();
    }

    private async Task<IReadOnlyList<LoaderVersionEntry>> FetchAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var preferMirror = SourceRace.PreferMirror(policy);
        var mirror = (CancellationToken token) => FetchAsync(true, token);
        var official = (CancellationToken token) => FetchAsync(false, token);
        List<NeoForgeEntry> entries = preferMirror
            ? await SourceRace.RunAsync(
                logger, "NeoForge",
                "BMCLAPI", mirror,
                "官方源", official,
                TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false)
            : await SourceRace.RunAsync(
                logger, "NeoForge",
                "官方源", official,
                "BMCLAPI", mirror,
                TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

        return entries
            .OrderByDescending(x => x.NumericVersion)
            .Select(entry => new LoaderVersionEntry
            {
                Kind = LoaderKind.NeoForge,
                Inherit = entry.Inherit,
                VersionName = entry.VersionName,
                IsStable = !entry.IsBeta,
                Payload = entry
            })
            .ToList();
    }

    private async Task<List<NeoForgeEntry>> FetchAsync(bool useMirror, CancellationToken cancellationToken)
    {
        var baseHost = useMirror
            ? "https://bmclapi2.bangbang93.com/neoforge/meta/api/maven/details/releases"
            : "https://maven.neoforged.net/api/maven/versions/releases";
        var latestJson = await SourceRace
            .GetTextAsync(client, $"{baseHost}/net/neoforged/neoforge", 15, cancellationToken)
            .ConfigureAwait(false);
        var legacyJson = await SourceRace
            .GetTextAsync(client, $"{baseHost}/net/neoforged/forge", 15, cancellationToken)
            .ConfigureAwait(false);
        if (latestJson.Length < 100 || legacyJson.Length < 100)
        {
            throw new LoaderMetadataException("NeoForge 版本列表数据异常");
        }

        var names = new List<string>();
        foreach (Match match in NeoForgeVersionRegex().Matches(legacyJson + latestJson))
        {
            names.Add(match.Value);
        }

        var entries = names
            .Where(name => name != BlacklistedVersion)
            .Distinct(StringComparer.Ordinal)
            .Select(name => new NeoForgeEntry(name))
            .ToList();
        if (entries.Count == 0)
        {
            throw new LoaderMetadataException("NeoForge 没有可用版本");
        }

        return entries;
    }

    [GeneratedRegex(@"(?<="")(1\.20\.1-)?\d+\.[^\.]+\.\d+(\.\d+)?(-(beta|alpha)(\.\d+)?)?(\+snapshot-\d+)?(\+pre-\d+)?(?="")")]
    private static partial Regex NeoForgeVersionRegex();
}

/// <summary>
/// Cleanroom 版本列表：GitHub Releases（官方独源）。
/// </summary>
public sealed class CleanroomProvider(
    DownloadClient client,
    LoaderMetadataCache cache) : ILoaderVersionProvider
{
    public LoaderKind Kind => LoaderKind.Cleanroom;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await GetAllAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(x => x.Inherit == minecraftVersion).ToList();
    }

    public Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<string>>(["1.12.2"]);

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetAllAsync(CancellationToken cancellationToken = default)
        => await cache.GetOrLoadAsync(
            "cleanroom:all",
            async token =>
            {
                var entries = new List<CleanroomEntry>();
                try
                {
                    var json = await SourceRace
                        .GetTextAsync(
                            client,
                            "https://api.github.com/repos/CleanroomMC/Cleanroom/releases",
                            15,
                            token)
                        .ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    foreach (var tokenElement in document.RootElement.EnumerateArray())
                    {
                        var tag = tokenElement.TryGetProperty("tag_name", out var tagElement)
                            ? tagElement.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(tag))
                        {
                            entries.Add(new CleanroomEntry(tag));
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is HttpRequestException or IOException or TaskCanceledException or DownloadException or JsonException)
                {
                    // GitHub 在部分网络不可达时，改用 HMCL 使用的 alist 镜像元数据
                    var json = await SourceRace
                        .GetTextAsync(
                            client,
                            "https://alist.8mi.tech/d/mirror/HMCL-Metadata/Auto/cleanroom/index.json",
                            15,
                            token)
                        .ConfigureAwait(false);
                    using var document = JsonDocument.Parse(json);
                    foreach (var tokenElement in document.RootElement.EnumerateArray())
                    {
                        var name = tokenElement.TryGetProperty("name", out var nameElement)
                            ? nameElement.GetString()
                            : null;
                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            entries.Add(new CleanroomEntry(name));
                        }
                    }
                }

                if (entries.Count == 0)
                {
                    throw new LoaderMetadataException("Cleanroom 没有可用版本");
                }

                return entries
                    .OrderByDescending(x => x.NumericVersion)
                    .Select(entry => new LoaderVersionEntry
                    {
                        Kind = LoaderKind.Cleanroom,
                        Inherit = entry.Inherit,
                        VersionName = entry.VersionName,
                        IsStable = !entry.IsBeta,
                        Payload = entry
                    })
                    .ToList();
            },
            cancellationToken).ConfigureAwait(false);
}
