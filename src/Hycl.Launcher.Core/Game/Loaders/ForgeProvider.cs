using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// Forge 版本列表（移植自 PCL-CE Download\Forge.cs）：
/// BMCLAPI（forge/minecraft JSON）与官方（files.minecraftforge.net 页面解析）双源，
/// 推荐版来自 BMCLAPI promos 缓存（ForgeRecommendedList.json）。
/// </summary>
public sealed partial class ForgeProvider(
    DownloadClient client,
    LauncherLogger logger,
    LoaderMetadataCache cache,
    string? cacheDirectory) : ILoaderVersionProvider
{
    public LoaderKind Kind => LoaderKind.Forge;

    private readonly SemaphoreSlim _promosRefreshLock = new(1, 1);
    private bool _promosRefreshed;

    public async Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var entries = await cache.GetOrLoadAsync(
            $"forge:{minecraftVersion}",
            async token => await FetchEntriesAsync(minecraftVersion, policy, token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
        return entries;
    }

    private async Task<IReadOnlyList<LoaderVersionEntry>> FetchEntriesAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var preferMirror = !(policy.VersionListSource == (int)DownloadSourceMode.OfficialOnly
                             || (policy.VersionListSource == (int)DownloadSourceMode.OfficialAuto
                                 && policy.MojangFast));
        List<ForgeEntry> entries;
        if (preferMirror)
        {
            try
            {
                entries = await FetchBmclapiAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"Forge BMCLAPI 列表失败，改用官方源：{ex.Message}");
                entries = await FetchOfficialAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
            }
        }
        else
        {
            try
            {
                entries = await FetchOfficialAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warn($"Forge 官方列表失败，改用 BMCLAPI：{ex.Message}");
                entries = await FetchBmclapiAsync(minecraftVersion, cancellationToken).ConfigureAwait(false);
            }
        }

        if (entries.Count == 0)
        {
            throw new LoaderMetadataException($"Forge 在 {minecraftVersion} 下没有可用版本");
        }

        entries.Sort((a, b) => b.NumericVersion.CompareTo(a.NumericVersion));
        return entries.Select(entry => new LoaderVersionEntry
        {
            Kind = LoaderKind.Forge,
            Inherit = minecraftVersion,
            VersionName = entry.VersionName,
            IsStable = true,
            IsRecommended = entry.IsRecommended,
            ReleaseTime = entry.ReleaseTime,
            Payload = entry
        }).ToList();
    }

    // ---------- BMCLAPI ----------

    private async Task<List<ForgeEntry>> FetchBmclapiAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        var json = await client
            .GetStringAsync(
                $"https://bmclapi2.bangbang93.com/forge/minecraft/{minecraftVersion.Replace('-', '_')}",
                cancellationToken)
            .ConfigureAwait(false);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var entries = new List<ForgeEntry>();
        foreach (var token in document.RootElement.EnumerateArray())
        {
            var version = token.TryGetProperty("version", out var versionElement) ? versionElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            var branch = token.TryGetProperty("branch", out var branchElement) ? branchElement.GetString() : null;
            var (hash, category) = FindBmclapiArtifact(token);
            DateTimeOffset? modified = null;
            if (token.TryGetProperty("modified", out var modifiedElement)
                && modifiedElement.TryGetDateTimeOffset(out var parsed))
            {
                modified = parsed;
            }

            entries.Add(new ForgeEntry(version, branch, minecraftVersion)
            {
                Hash = hash,
                Category = category,
                ReleaseTime = modified,
                IsRecommended = await IsRecommendedAsync(minecraftVersion, version, cancellationToken).ConfigureAwait(false)
            });
        }

        if (entries.Count == 0)
        {
            throw new LoaderMetadataException($"Forge BMCLAPI 列表为空：{minecraftVersion}");
        }

        return entries;
    }

    private static (string Hash, string Category) FindBmclapiArtifact(System.Text.Json.JsonElement token)
    {
        string? hash = null;
        var category = "unknown";
        var priority = -1;
        if (token.TryGetProperty("files", out var files) && files.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var file in files.EnumerateArray())
            {
                var fileCategory = file.TryGetProperty("category", out var categoryElement) ? categoryElement.GetString() : null;
                var format = file.TryGetProperty("format", out var formatElement) ? formatElement.GetString() : null;
                switch (fileCategory)
                {
                    case "installer" when format == "jar":
                        hash = ReadHash(file) ?? hash;
                        category = "installer";
                        priority = 2;
                        break;
                    case "universal" when priority <= 1 && format == "zip":
                        hash = ReadHash(file) ?? hash;
                        category = "universal";
                        priority = 1;
                        break;
                    case "client" when priority <= 0 && format == "zip":
                        hash = ReadHash(file) ?? hash;
                        category = "client";
                        priority = 0;
                        break;
                }
            }
        }

        return (hash ?? "", category);
    }

    private static string? ReadHash(System.Text.Json.JsonElement file)
        => file.TryGetProperty("hash", out var hashElement) ? hashElement.GetString() : null;

    // ---------- 官方页面 ----------

    private async Task<List<ForgeEntry>> FetchOfficialAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        var page = await client
            .GetStringAsync(
                $"https://files.minecraftforge.net/maven/net/minecraftforge/forge/index_{minecraftVersion.Replace('-', '_')}.html",
                cancellationToken)
            .ConfigureAwait(false);
        if (page.Length < 1000)
        {
            throw new LoaderMetadataException($"Forge 官方页面数据异常：{minecraftVersion}");
        }

        var entries = new List<ForgeEntry>();
        var tableEnd = page.LastIndexOf("</table>", StringComparison.Ordinal);
        var segments = (tableEnd > 0 ? page[..tableEnd] : page)
            .Split("<td class=\"download-version", StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Skip(1))
        {
            var entry = ParseOfficialVersion(segment, minecraftVersion);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private ForgeEntry? ParseOfficialVersion(string segment, string minecraftVersion)
    {
        var name = VersionNameRegex().Match(segment).Value;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var isRecommended = segment.Contains("fa promo-recommended", StringComparison.Ordinal);
        var branchMatch = BranchRegex(name).Match(segment);
        var branch = branchMatch.Success ? branchMatch.Groups[1].Value : null;

        var timeMatch = ReleaseTimeRegex().Match(segment);
        if (!timeMatch.Success)
        {
            return null;
        }

        DateTimeOffset? releaseTime = null;
        if (DateTime.TryParse(timeMatch.Groups[1].Value, null,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedDate))
        {
            releaseTime = new DateTimeOffset(parsedDate);
        }

        var artifact = ParseOfficialArtifact(segment);
        if (artifact is null)
        {
            return null;
        }

        return new ForgeEntry(name, branch, minecraftVersion)
        {
            Category = artifact.Value.Category,
            Hash = artifact.Value.Hash.Trim('\r', '\n'),
            IsRecommended = isRecommended,
            ReleaseTime = releaseTime
        };
    }

    private static (string Hash, string Category)? ParseOfficialArtifact(string segment)
    {
        string marker;
        string category;
        if (segment.Contains("classifier-installer\"", StringComparison.Ordinal))
        {
            marker = "installer.jar";
            category = "installer";
        }
        else if (segment.Contains("classifier-universal\"", StringComparison.Ordinal))
        {
            marker = "universal.zip";
            category = "universal";
        }
        else if (segment.Contains("client.zip", StringComparison.Ordinal))
        {
            marker = "client.zip";
            category = "client";
        }
        else
        {
            return null;
        }

        var artifactIndex = segment.IndexOf(marker, StringComparison.Ordinal);
        if (artifactIndex < 0)
        {
            return null;
        }

        var md5 = Md5Regex().Match(segment[artifactIndex..]);
        return (md5.Success ? md5.Groups[1].Value : "", category);
    }

    // ---------- 推荐版 promos ----------

    private async Task<bool> IsRecommendedAsync(string minecraftVersion, string version, CancellationToken cancellationToken)
    {
        var recommended = await GetRecommendedAsync(cancellationToken).ConfigureAwait(false);
        return recommended is not null
               && recommended.TryGetValue(minecraftVersion, out var build)
               && build == version;
    }

    /// <summary>读取 Forge 推荐版映射；首次调用触发后台刷新（对应 PCL-CE ForgeRecommendedList.json）。</summary>
    private async Task<IReadOnlyDictionary<string, string>?> GetRecommendedAsync(CancellationToken cancellationToken)
    {
        if (cacheDirectory is null)
        {
            return null;
        }

        await RefreshPromosQuietlyAsync(cancellationToken).ConfigureAwait(false);
        var cachePath = Path.Combine(cacheDirectory, "ForgeRecommendedList.json");
        try
        {
            if (!File.Exists(cachePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(cachePath, cancellationToken).ConfigureAwait(false);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.GetString() ?? "";
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task RefreshPromosQuietlyAsync(CancellationToken cancellationToken)
    {
        if (_promosRefreshed || cacheDirectory is null)
        {
            return;
        }

        var entered = false;
        try
        {
            entered = await _promosRefreshLock.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered)
            {
                return;
            }

            var promos = await client.GetStringAsync("https://bmclapi2.bangbang93.com/forge/promos", cancellationToken)
                .ConfigureAwait(false);
            if (promos.Length < 1000)
            {
                throw new LoaderMetadataException($"Forge promos 数据异常（{promos.Length} 字节）");
            }

            var recommended = new List<string>();
            using var document = System.Text.Json.JsonDocument.Parse(promos);
            foreach (var token in document.RootElement.EnumerateArray())
            {
                if (!token.TryGetProperty("name", out var nameElement)
                    || !token.TryGetProperty("build", out var buildElement))
                {
                    continue;
                }

                var name = nameElement.GetString();
                if (string.IsNullOrWhiteSpace(name) || !name.EndsWith("-recommended", StringComparison.Ordinal))
                {
                    continue;
                }

                var buildVersion = buildElement.TryGetProperty("version", out var versionElement)
                    ? versionElement.GetString()
                    : null;
                if (string.IsNullOrWhiteSpace(buildVersion))
                {
                    continue;
                }

                recommended.Add($"\"{name.Replace("-recommended", $"\":\"{buildVersion}\"")}");
            }

            if (recommended.Count < 5)
            {
                throw new LoaderMetadataException($"Forge 推荐版数量异常（{recommended.Count}）");
            }

            Directory.CreateDirectory(cacheDirectory);
            await File.WriteAllTextAsync(
                    Path.Combine(cacheDirectory, "ForgeRecommendedList.json"),
                    $"{{{string.Join(",", recommended)}}}",
                    cancellationToken)
                .ConfigureAwait(false);
            _promosRefreshed = true;
            logger.Info("Forge 推荐版缓存已刷新");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or LoaderMetadataException or InvalidOperationException)
        {
            // promos 只是推荐标记，失败不影响主流程；下次进入页面再试
            logger.Warn($"刷新 Forge 推荐版缓存失败（可忽略）：{ex.Message}");
        }
        finally
        {
            if (entered)
            {
                _promosRefreshLock.Release();
            }
        }
    }

    [GeneratedRegex(@"(?<=[^(0-9)]+)[0-9\.]+")]
    private static partial Regex VersionNameRegex();

    private Regex BranchRegex(string name)
        => new($"(?<=-{Regex.Escape(name)}-)[^-\\\"]+(?=-[a-z]+.[a-z]{{3}})", RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    [GeneratedRegex("(?<=\\\"download-time\\\" title=\\\")[^\\\"]+")]
    private static partial Regex ReleaseTimeRegex();

    [GeneratedRegex("(?<=MD5:</strong> )[^<]+")]
    private static partial Regex Md5Regex();
}
