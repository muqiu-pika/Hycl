using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// OptiFine 版本列表（移植自 PCL-CE Download\OptiFine.cs）：
/// BMCLAPI versionList JSON 与官方 downloads 页面解析双源。
/// </summary>
public sealed partial class OptiFineProvider(
    DownloadClient client,
    LauncherLogger logger,
    LoaderMetadataCache cache) : ILoaderVersionProvider
{
    public LoaderKind Kind => LoaderKind.OptiFine;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await cache.GetOrLoadAsync(
            "optifine:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return all.Where(x => x.Inherit == minecraftVersion).ToList();
    }

    public async Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await cache.GetOrLoadAsync(
            "optifine:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return all.Select(x => x.Inherit).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
        => await cache.GetOrLoadAsync(
            "optifine:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<LoaderVersionEntry>> FetchAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var preferMirror = !(policy.VersionListSource == (int)DownloadSourceMode.OfficialOnly
                             || (policy.VersionListSource == (int)DownloadSourceMode.OfficialAuto
                                 && policy.MojangFast));
        List<OptiFineEntry> entries;
        if (preferMirror)
        {
            try
            {
                entries = await FetchBmclapiAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"OptiFine BMCLAPI 列表失败，改用官方源：{ex.Message}");
                entries = await FetchOfficialAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                entries = await FetchOfficialAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"OptiFine 官方列表失败，改用 BMCLAPI：{ex.Message}");
                entries = await FetchBmclapiAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        return entries.Select(entry => new LoaderVersionEntry
        {
            Kind = LoaderKind.OptiFine,
            Inherit = entry.Inherit ?? "",
            VersionName = entry.NameVersion ?? entry.DisplayName ?? "",
            IsStable = !entry.IsPreview,
            ReleaseTime = entry.ReleaseTime,
            Payload = entry
        }).ToList();
    }

    // ---------- BMCLAPI ----------

    private async Task<List<OptiFineEntry>> FetchBmclapiAsync(CancellationToken cancellationToken)
    {
        var json = await client
            .GetStringAsync("https://bmclapi2.bangbang93.com/optifine/versionList", cancellationToken)
            .ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var entries = new List<OptiFineEntry>();
        foreach (var token in document.RootElement.EnumerateArray())
        {
            var mcVersion = Get(token, "mcversion");
            var type = Get(token, "type");
            var patch = Get(token, "patch");
            var fileName = Get(token, "filename");
            if (string.IsNullOrWhiteSpace(mcVersion) || string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            var forge = Get(token, "forge")?
                .Replace("Forge ", "")
                .Replace("#", "");
            if (forge is not null && forge.Contains("N/A", StringComparison.Ordinal))
            {
                forge = null;
            }

            var inherit = mcVersion.EndsWith(".0", StringComparison.Ordinal) ? mcVersion[..^2] : mcVersion;
            var nameVersion =
                $"{inherit}-OptiFine_{($"{type} {patch}").Replace(".0 ", " ").Replace(" ", "_").Replace($"{inherit}_", "")}";
            entries.Add(new OptiFineEntry
            {
                DisplayName = $"{mcVersion}{(type ?? "").Replace("HD_U", "").Replace("_", " ")} {patch}"
                    .Replace(".0 ", " "),
                IsPreview = patch?.Contains("pre", StringComparison.OrdinalIgnoreCase) == true,
                NameFile = fileName,
                NameVersion = nameVersion,
                RequiredForgeVersion = forge,
                Inherit = inherit
            });
        }

        if (entries.Count == 0)
        {
            throw new LoaderMetadataException("OptiFine BMCLAPI 列表为空");
        }

        return entries;
    }

    // ---------- 官方页面 ----------

    private async Task<List<OptiFineEntry>> FetchOfficialAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://optifine.net/downloads");
        request.Headers.Accept.ParseAdd("application/json, text/javascript, */*; q=0.01");
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        var result = await client.GetStringAsync("https://optifine.net/downloads", cancellationToken)
            .ConfigureAwait(false);
        if (result.Length < 200)
        {
            throw new LoaderMetadataException("OptiFine 官方页面数据异常");
        }

        var forgeList = RegexSearch(result, ForgeRegex());
        var releaseTimeList = RegexSearch(result, DateRegex());
        var nameList = RegexSearch(result, NameRegex());
        if (releaseTimeList.Count != nameList.Count || forgeList.Count != nameList.Count || nameList.Count < 10)
        {
            throw new LoaderMetadataException(
                $"OptiFine 官方页面解析失败（名称 {nameList.Count} / 时间 {releaseTimeList.Count} / Forge {forgeList.Count}）");
        }

        var entries = new List<OptiFineEntry>();
        for (var i = 0; i < nameList.Count; i++)
        {
            var name = nameList[i].Replace("_", " ");
            var inherit = name.Split(' ')[0];
            if (inherit.EndsWith(".0", StringComparison.Ordinal))
            {
                inherit = inherit[..^2];
            }

            DateTimeOffset? releaseTime = null;
            if (DateTime.TryParseExact(
                    releaseTimeList[i],
                    ["d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy", "dd.MM.yyyy"],
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var parsedDate))
            {
                releaseTime = new DateTimeOffset(parsedDate);
            }

            var forge = forgeList[i].Replace("Forge ", "").Replace("#", "");
            if (forge.Contains("N/A", StringComparison.Ordinal))
            {
                forge = null;
            }

            entries.Add(new OptiFineEntry
            {
                DisplayName = name.Replace("HD U ", "").Replace(".0 ", " "),
                IsPreview = name.Contains("pre", StringComparison.OrdinalIgnoreCase),
                Inherit = inherit,
                NameFile = $"{(name.Contains("pre", StringComparison.OrdinalIgnoreCase) ? "preview_" : "")}OptiFine_{name.Replace(" ", "_")}.jar",
                RequiredForgeVersion = forge,
                ReleaseTime = releaseTime,
                NameVersion = $"{inherit}-OptiFine_{name.Replace(" ", "_").Replace($"{inherit}_", "")}"
            });
        }

        return entries;
    }

    private static string? Get(JsonElement token, string name)
        => token.TryGetProperty(name, out var element) ? element.GetString() : null;

    private static List<string> RegexSearch(string input, Regex regex)
        => regex.Matches(input).Select(match => match.Value).ToList();

    [GeneratedRegex("(?<=colForge'>)[^<]*")]
    private static partial Regex ForgeRegex();

    [GeneratedRegex("(?<=colDate'>)[^<]+")]
    private static partial Regex DateRegex();

    [GeneratedRegex("(?<=OptiFine_)[0-9A-Za-z_.]+(?=.jar\\\")")]
    private static partial Regex NameRegex();
}

/// <summary>
/// LiteLoader 版本列表（移植自 PCL-CE Download\LiteLoader.cs）：
/// dl.liteloader.com 与 BMCLAPI 双源；1.5/1.6 跳过。
/// </summary>
public sealed class LiteLoaderProvider(
    DownloadClient client,
    LauncherLogger logger,
    LoaderMetadataCache cache) : ILoaderVersionProvider
{
    public LoaderKind Kind => LoaderKind.LiteLoader;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await cache.GetOrLoadAsync(
            "liteloader:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return all.Where(x => x.Inherit == minecraftVersion).ToList();
    }

    public async Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var all = await cache.GetOrLoadAsync(
            "liteloader:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return all.Select(x => x.Inherit!).Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
        => await cache.GetOrLoadAsync(
            "liteloader:all",
            async token => await FetchAllAsync(policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<LoaderVersionEntry>> FetchAllAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var preferMirror = !(policy.VersionListSource == (int)DownloadSourceMode.OfficialOnly
                             || (policy.VersionListSource == (int)DownloadSourceMode.OfficialAuto
                                 && policy.MojangFast));
        List<LiteLoaderEntry> entries;
        if (preferMirror)
        {
            try
            {
                entries = await FetchFromAsync(true, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"LiteLoader BMCLAPI 列表失败，改用官方源：{ex.Message}");
                entries = await FetchFromAsync(false, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                entries = await FetchFromAsync(false, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"LiteLoader 官方列表失败，改用 BMCLAPI：{ex.Message}");
                entries = await FetchFromAsync(true, cancellationToken).ConfigureAwait(false);
            }
        }

        return entries.Select(entry => new LoaderVersionEntry
        {
            Kind = LoaderKind.LiteLoader,
            Inherit = entry.Inherit ?? "",
            VersionName = $"LiteLoader {entry.Inherit}",
            IsStable = !entry.IsPreview,
            ReleaseTime = entry.ReleaseTime,
            Payload = entry
        }).ToList();
    }

    private async Task<List<LiteLoaderEntry>> FetchFromAsync(bool useMirror, CancellationToken cancellationToken)
    {
        var url = useMirror
            ? "https://bmclapi2.bangbang93.com/maven/com/mumfrey/liteloader/versions.json"
            : "https://dl.liteloader.com/versions/versions.json";
        var json = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        using var document = JsonDocument.Parse(json);
        var versions = document.RootElement.TryGetProperty("versions", out var versionsElement)
            ? versionsElement
            : throw new LoaderMetadataException("LiteLoader versions.json 缺少 versions 字段");
        var entries = new List<LiteLoaderEntry>();
        foreach (var versionProperty in versions.EnumerateObject())
        {
            var key = versionProperty.Name;
            if (key.StartsWith("1.6", StringComparison.Ordinal) || key.StartsWith("1.5", StringComparison.Ordinal))
            {
                continue;
            }

            if (!versionProperty.Value.TryGetProperty("artefacts", out var artefacts)
                && !versionProperty.Value.TryGetProperty("snapshots", out artefacts))
            {
                continue;
            }

            if (!artefacts.TryGetProperty("com.mumfrey:liteloader", out var liteLoader)
                || !liteLoader.TryGetProperty("latest", out var latest))
            {
                continue;
            }

            var stream = latest.TryGetProperty("stream", out var streamElement) ? streamElement.GetString() : null;
            var md5 = latest.TryGetProperty("md5", out var md5Element) ? md5Element.GetString() : null;
            DateTimeOffset? releaseTime = null;
            if (latest.TryGetProperty("timestamp", out var timestampElement)
                && long.TryParse(timestampElement.ToString(), out var unix))
            {
                releaseTime = DateTimeOffset.FromUnixTimeSeconds(unix);
            }

            entries.Add(new LiteLoaderEntry
            {
                Inherit = key,
                IsLegacy = key.Split('.') is { Length: >= 2 } parts
                           && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var minor)
                           && minor < 8d,
                IsPreview = string.Equals(stream, "snapshot", StringComparison.OrdinalIgnoreCase),
                FileName = $"liteloader-installer-{key}{(key is "1.8" or "1.9" ? ".0" : "")}-00-SNAPSHOT.jar",
                Md5 = md5,
                ReleaseTime = releaseTime,
                RawJson = latest.GetRawText()
            });
        }

        if (entries.Count == 0)
        {
            throw new LoaderMetadataException("LiteLoader 版本列表为空");
        }

        return entries;
    }
}

/// <summary>
/// LabyMod 版本列表（releases.r2.labymod.net manifest，production + snapshot）。
/// manifest 的 minecraftVersions 数组提供每个 MC 版本的 customManifestUrl，用于支持校验。
/// </summary>
public sealed class LabyModProvider(
    DownloadClient client,
    LauncherLogger logger,
    LoaderMetadataCache cache) : ILoaderVersionProvider
{
    public LoaderKind Kind => LoaderKind.LabyMod;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var channels = await cache.GetOrLoadAsync(
            "labymod:channels",
            async token => await FetchChannelsAsync(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var supported = channels.Where(x => x.SupportedVersions.Contains(minecraftVersion, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (supported.Count == 0)
        {
            throw new LoaderMetadataException($"LabyMod 不支持 Minecraft {minecraftVersion}");
        }

        return supported.Select(entry => new LoaderVersionEntry
        {
            Kind = LoaderKind.LabyMod,
            Inherit = minecraftVersion,
            VersionName = $"{entry.LabyModVersion}（{entry.Channel}）",
            IsStable = !entry.IsSnapshot,
            Payload = entry
        }).ToList();
    }

    /// <summary>manifest 声明支持的 Minecraft 版本（production 渠道的 tag/version 并集）。</summary>
    public async Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var channels = await cache.GetOrLoadAsync(
            "labymod:channels",
            async token => await FetchChannelsAsync(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        var production = channels.FirstOrDefault(x => x.Channel == "production") ?? channels.FirstOrDefault();
        return production?.SupportedVersions.ToList() ?? [];
    }

    private async Task<List<LabyModEntry>> FetchChannelsAsync(CancellationToken cancellationToken)
    {
        var entries = new List<LabyModEntry>();
        foreach (var channel in new[] { "production", "snapshot" })
        {
            try
            {
                var json = await client
                    .GetStringAsync($"https://releases.r2.labymod.net/api/v1/manifest/{channel}/latest.json", cancellationToken)
                    .ConfigureAwait(false);
                using var document = JsonDocument.Parse(json);
                var version = document.RootElement.TryGetProperty("labyModVersion", out var versionElement)
                    ? versionElement.GetString()
                    : null;
                var commitRef = document.RootElement.TryGetProperty("commitReference", out var commitElement)
                    ? commitElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(commitRef))
                {
                    throw new LoaderMetadataException($"LabyMod {channel} manifest 缺少版本字段");
                }

                var supportedVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (document.RootElement.TryGetProperty("minecraftVersions", out var mcVersions)
                    && mcVersions.ValueKind == JsonValueKind.Array)
                {
                    foreach (var mcVersion in mcVersions.EnumerateArray())
                    {
                        foreach (var field in new[] { "tag", "version" })
                        {
                            if (mcVersion.TryGetProperty(field, out var fieldElement)
                                && fieldElement.GetString() is { Length: > 0 } value)
                            {
                                supportedVersions.Add(value);
                            }
                        }
                    }
                }

                entries.Add(new LabyModEntry
                {
                    Channel = channel,
                    LabyModVersion = version,
                    CommitRef = commitRef,
                    SupportedVersions = supportedVersions
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"LabyMod {channel} manifest 获取失败：{ex.Message}");
                if (channel == "production")
                {
                    throw;
                }
            }
        }

        return entries;
    }
}
