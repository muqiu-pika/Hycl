using System.Text.Json;
using System.Text.Json.Serialization;
using Hycl.Launcher.Core.Download;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>按 SHA-1 反查到的 Modrinth 版本文件（导出清单用）。</summary>
public sealed record ModrinthHashMatch(
    string ProjectId,
    string VersionId,
    string FileName,
    long Size,
    string Sha1,
    string? Sha512,
    string DownloadUrl);

/// <summary>按 SHA-1 反查到的「该文件对应的最新兼容版本」（用于本地内容更新检测）。</summary>
public sealed record ModrinthLatestFile(
    string ProjectId,
    string VersionId,
    string VersionNumber,
    string FileName,
    string Sha1,
    string DownloadUrl,
    DateTimeOffset? DatePublished);

/// <summary>
/// Modrinth 客户端：搜索 / 项目详情 / 文件列表（对应 PCL-CE Comp 模块的 Modrinth 分支）。
/// 全部请求经 <see cref="CompRequester"/> 的镜像阶梯（api.modrinth.com ↔ mod.mcimirror.top/modrinth）。
/// </summary>
public sealed class ModrinthClient(CompRequester requester)
{
    public const string ApiBase = "https://api.modrinth.com";

    // ---------- 原始 JSON 模型 ----------

    private sealed class ModrinthSearchHit
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
        [JsonPropertyName("display_categories")] public List<string>? DisplayCategories { get; set; }
        [JsonPropertyName("versions")] public List<string>? Versions { get; set; }
        [JsonPropertyName("author")] public string? Author { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("date_modified")] public DateTimeOffset? DateModified { get; set; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
        [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
    }

    private sealed class ModrinthSearchResponse
    {
        [JsonPropertyName("hits")] public List<ModrinthSearchHit>? Hits { get; set; }
        [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
    }

    private sealed class ModrinthProjectJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("description")] public string? Description { get; set; }
        [JsonPropertyName("categories")] public List<string>? Categories { get; set; }
        [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
        [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; set; }
        [JsonPropertyName("downloads")] public long Downloads { get; set; }
        [JsonPropertyName("date_modified")] public DateTimeOffset? DateModified { get; set; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
        [JsonPropertyName("project_type")] public string? ProjectType { get; set; }
        [JsonPropertyName("issues_url")] public string? IssuesUrl { get; set; }
    }

    private sealed class ModrinthVersionJson
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("version_number")] public string? VersionNumber { get; set; }
        [JsonPropertyName("changelog")] public string? Changelog { get; set; }
        [JsonPropertyName("date_published")] public DateTimeOffset? DatePublished { get; set; }
        [JsonPropertyName("version_type")] public string? VersionType { get; set; }
        [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; set; }
        [JsonPropertyName("loaders")] public List<string>? Loaders { get; set; }
        [JsonPropertyName("files")] public List<ModrinthFileJson>? Files { get; set; }
        [JsonPropertyName("dependencies")] public List<ModrinthDependencyJson>? Dependencies { get; set; }
    }

    private sealed class ModrinthFileJson
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
        [JsonPropertyName("filename")] public string? Filename { get; set; }
        [JsonPropertyName("primary")] public bool Primary { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
        [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; set; }
    }

    private sealed class ModrinthDependencyJson
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
        [JsonPropertyName("dependency_type")] public string? DependencyType { get; set; }
    }

    // ---------- 类型映射 ----------

    /// <summary>CompType → Modrinth project_type（DataPack 在 Modrinth 里作为 mod 类型 + datapack 分类检索）。</summary>
    public static string ToProjectType(CompResourceType type)
        => type switch
        {
            CompResourceType.ModPack => "modpack",
            CompResourceType.ResourcePack => "resourcepack",
            CompResourceType.Shader => "shader",
            _ => "mod"
        };

    private static CompResourceType FromProjectType(string? projectType)
        => projectType switch
        {
            "modpack" => CompResourceType.ModPack,
            "resourcepack" => CompResourceType.ResourcePack,
            "shader" => CompResourceType.Shader,
            _ => CompResourceType.Mod
        };

    private static CompFileStatus FromVersionType(string? versionType)
        => versionType switch
        {
            "beta" => CompFileStatus.Beta,
            "alpha" => CompFileStatus.Alpha,
            _ => CompFileStatus.Release
        };

    // ---------- API ----------

    /// <summary>
    /// 搜索（对应 PCL-CE GetModrinthAddress）：
    /// /v2/search?limit=&index=&query=&offset=&facets=[["project_type:x"],["categories:'loader'"],["versions:'v'"],["categories:'tag'"]]
    /// </summary>
    public async Task<CompSearchResult> SearchAsync(CompSearchRequest request, CancellationToken cancellationToken = default)
    {
        var index = request.Sort switch
        {
            CompSortType.Relevance => "relevance",
            CompSortType.Downloads => "downloads",
            CompSortType.Follows => "follows",
            CompSortType.Newest => "newest",
            CompSortType.Updated => "updated",
            _ => "relevance"
        };
        var facets = new List<IReadOnlyList<string>>
        {
            new[] { $"project_type:{ToProjectType(request.Type)}" }
        };
        if (request.Type == CompResourceType.DataPack)
        {
            // Modrinth 的数据包以 mod 类型 + datapack 分类发布（对应 PCL-CE 的类型识别）
            facets.Add(new[] { "categories:'datapack'" });
        }
        if (request.Loader != CompLoaderFilter.Any)
        {
            facets.Add(new[] { $"categories:'{request.Loader.ToString().ToLowerInvariant()}'" });
        }

        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            facets.Add(new[] { $"versions:'{request.GameVersion}'" });
        }

        var tag = request.Tag?.Split('/', 2)[^1];
        if (!string.IsNullOrWhiteSpace(tag))
        {
            facets.Add(new[] { $"categories:'{tag}'" });
        }

        var facetsJson = JsonSerializer.Serialize(facets);
        var url = $"{ApiBase}/v2/search?limit={request.Limit}&index={index}&offset={request.Offset}&facets={Uri.EscapeDataString(facetsJson)}";
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            url += $"&query={Uri.EscapeDataString(request.Query)}";
        }

        var response = await requester.GetJsonAsync<ModrinthSearchResponse>(url, cancellationToken).ConfigureAwait(false);
        var projects = (response.Hits ?? []).Select(hit => new CompProject
        {
            Id = hit.ProjectId ?? "",
            Slug = hit.Slug ?? "",
            Title = hit.Title ?? "",
            Description = hit.Description ?? "",
            Author = hit.Author ?? "",
            IconUrl = string.IsNullOrWhiteSpace(hit.IconUrl) ? null : hit.IconUrl,
            DownloadCount = hit.Downloads,
            LastUpdate = hit.DateModified,
            Type = request.Type == CompResourceType.DataPack
                ? CompResourceType.DataPack
                : FromProjectType(hit.ProjectType),
            FromCurseForge = false,
            ModLoaders = hit.DisplayCategories?
                .Where(c => c is "forge" or "fabric" or "quilt" or "neoforge")
                .ToList() ?? [],
            Categories = hit.DisplayCategories ?? hit.Categories ?? [],
            GameVersions = hit.Versions ?? []
        }).Where(p => !string.IsNullOrEmpty(p.Id)).ToList();
        return new CompSearchResult { Projects = projects, ModrinthTotal = response.TotalHits };
    }

    /// <summary>项目详情（/v2/project/{idOrSlug}）。</summary>
    public async Task<CompProject> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default)
    {
        var json = await requester.GetJsonAsync<ModrinthProjectJson>(
                $"{ApiBase}/v2/project/{Uri.EscapeDataString(idOrSlug)}", cancellationToken)
            .ConfigureAwait(false);
        return new CompProject
        {
            Id = json.Id ?? "",
            Slug = json.Slug ?? "",
            Title = json.Title ?? "",
            Description = json.Description ?? "",
            IconUrl = string.IsNullOrWhiteSpace(json.IconUrl) ? null : json.IconUrl,
            DownloadCount = json.Downloads,
            LastUpdate = json.DateModified,
            Type = FromProjectType(json.ProjectType),
            FromCurseForge = false,
            ModLoaders = json.Loaders ?? [],
            Categories = json.Categories ?? [],
            GameVersions = json.GameVersions ?? [],
            WebsiteUrl = json.IssuesUrl ?? $"https://modrinth.com/{ToProjectType(FromProjectType(json.ProjectType))}/{json.Slug}"
        };
    }

    /// <summary>文件列表（/v2/project/{id}/version），按发布时间从新到旧。</summary>
    public async Task<IReadOnlyList<CompFile>> GetFilesAsync(
        string projectId,
        CompResourceType type,
        CancellationToken cancellationToken = default)
    {
        var versions = await requester.GetJsonAsync<List<ModrinthVersionJson>>(
                $"{ApiBase}/v2/project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false", cancellationToken)
            .ConfigureAwait(false);
        var files = new List<CompFile>();
        foreach (var version in versions ?? [])
        {
            var file = BuildFile(version, type);
            if (file is not null)
            {
                files.Add(file);
            }
        }


        return files
            .OrderByDescending(f => f.ReleaseDate ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>
    /// 按 SHA-1 批量反查版本文件（POST /v2/version_files，导出整合包时用）。
    /// 返回字典的键是请求里的 sha1；只有响应中能对上 sha1 的文件才会返回。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModrinthHashMatch>> LookupBySha1Async(
        IReadOnlyCollection<string> sha1Hashes,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModrinthHashMatch>(StringComparer.OrdinalIgnoreCase);
        var hashes = sha1Hashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var batch in hashes.Chunk(100))
        {
            var body = JsonSerializer.Serialize(new { hashes = batch, algorithm = "sha1" });
            var response = await requester.PostJsonAsync<Dictionary<string, ModrinthVersionJson>>(
                    $"{ApiBase}/v2/version_files", body, cancellationToken)
                .ConfigureAwait(false);
            foreach (var (hash, version) in response ?? [])
            {
                // 一个版本可能有多个文件，必须选择 sha1 真正对上的那个，避免把附加文件当成主文件
                var file = version.Files?
                    .FirstOrDefault(f => f.Hashes is not null
                                         && f.Hashes.TryGetValue("sha1", out var sha1)
                                         && sha1.Equals(hash, StringComparison.OrdinalIgnoreCase));
                if (file?.Url is null)
                {
                    continue;
                }

                result[hash] = new ModrinthHashMatch(
                    version.ProjectId ?? "",
                    version.Id ?? "",
                    file.Filename ?? "",
                    file.Size,
                    file.Hashes?["sha1"] ?? hash,
                    file.Hashes?.GetValueOrDefault("sha512"),
                    file.Url);
            }
        }

        return result;
    }

    /// <summary>
    /// 按 SHA-1 批量查询「与当前实例版本/加载器兼容的最新版本」（POST /v2/version_files/update）。
    /// 返回字典的键是请求里的 sha1；未在 Modrinth 收录的文件不会出现在结果中。
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModrinthLatestFile>> LookupLatestBySha1Async(
        IReadOnlyCollection<string> sha1Hashes,
        string? gameVersion = null,
        IReadOnlyList<string>? loaders = null,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModrinthLatestFile>(StringComparer.OrdinalIgnoreCase);
        var hashes = sha1Hashes
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .Select(hash => hash.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var batch in hashes.Chunk(100))
        {
            var payload = new Dictionary<string, object>
            {
                ["hashes"] = batch,
                ["algorithm"] = "sha1"
            };
            if (!string.IsNullOrWhiteSpace(gameVersion))
            {
                payload["game_versions"] = new[] { gameVersion };
            }

            if (loaders is { Count: > 0 })
            {
                payload["loaders"] = loaders;
            }

            var response = await requester.PostJsonAsync<Dictionary<string, ModrinthVersionJson>>(
                    $"{ApiBase}/v2/version_files/update", JsonSerializer.Serialize(payload), cancellationToken)
                .ConfigureAwait(false);
            foreach (var (hash, version) in response ?? [])
            {
                var file = version.Files?.FirstOrDefault(candidate => candidate.Primary)
                           ?? version.Files?.FirstOrDefault();
                if (file?.Url is null)
                {
                    continue;
                }

                result[hash] = new ModrinthLatestFile(
                    version.ProjectId ?? "",
                    version.Id ?? "",
                    version.VersionNumber ?? "",
                    file.Filename ?? "",
                    file.Hashes?.GetValueOrDefault("sha1") ?? "",
                    file.Url,
                    version.DatePublished);
            }
        }

        return result;
    }

    private CompFile? BuildFile(ModrinthVersionJson version, CompResourceType type)
    {
        var file = version.Files?.FirstOrDefault(f => f.Primary) ?? version.Files?.FirstOrDefault();
        if (file?.Url is null)
        {
            return null;
        }

        var dependencies = (version.Dependencies ?? [])
            .Where(d => !string.IsNullOrEmpty(d.ProjectId)
                        && d.ProjectId is not "P7dR8mSH"    // Fabric API（PCL-CE 同款排除）
                        && d.ProjectId is not "qvIfYCYJ")   // Quilted Fabric API
            .Select(d => new CompDependencyRef(d.ProjectId!, d.DependencyType == "required"))
            .ToList();
        return new CompFile
        {
            ProjectId = version.ProjectId ?? "",
            Id = version.Id ?? "",
            DisplayName = version.Name ?? version.VersionNumber ?? "",
            FileName = file.Filename ?? "",
            Version = version.VersionNumber ?? "",
            ReleaseDate = version.DatePublished,
            Status = FromVersionType(version.VersionType),
            GameVersions = version.GameVersions ?? [],
            ModLoaders = version.Loaders ?? [],
            Hash = file.Hashes is not null && file.Hashes.TryGetValue("sha1", out var sha1) ? sha1 : null,
            Size = file.Size,
            DownloadUrls = DownloadSources.GetModDownloadUrls(file.Url, requester.Solution),
            Type = type,
            FromCurseForge = false,
            Dependencies = dependencies.Where(d => d.Required).ToList(),
            OptionalDependencies = dependencies.Where(d => !d.Required).ToList()
        };
    }
}
