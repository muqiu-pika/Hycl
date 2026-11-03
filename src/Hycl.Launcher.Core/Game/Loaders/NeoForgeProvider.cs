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

    private async Task<IReadOnlyList<LoaderVersionEntry>> FetchAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var preferMirror = !(policy.VersionListSource == (int)DownloadSourceMode.OfficialOnly
                             || (policy.VersionListSource == (int)DownloadSourceMode.OfficialAuto
                                 && policy.MojangFast));
        List<NeoForgeEntry> entries;
        if (preferMirror)
        {
            try
            {
                entries = await FetchAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"NeoForge BMCLAPI 列表失败，改用官方源：{ex.Message}");
                entries = await FetchAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                entries = await FetchAsync(false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"NeoForge 官方列表失败，改用 BMCLAPI：{ex.Message}");
                entries = await FetchAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }

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
        var latestJson = await client.GetStringAsync($"{baseHost}/net/neoforged/neoforge", cancellationToken)
            .ConfigureAwait(false);
        var legacyJson = await client.GetStringAsync($"{baseHost}/net/neoforged/forge", cancellationToken)
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

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetAllAsync(CancellationToken cancellationToken = default)
        => await cache.GetOrLoadAsync(
            "cleanroom:all",
            async token =>
            {
                var json = await client
                    .GetStringAsync("https://api.github.com/repos/CleanroomMC/Cleanroom/releases", token)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var entries = new List<CleanroomEntry>();
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
