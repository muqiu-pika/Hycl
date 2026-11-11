using System.Text.Json;
using System.Text.Json.Serialization;
using Hycl.Launcher.Core.Download;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// CurseForge 客户端框架（对应 PCL-CE Comp 模块的 CurseForge 分支）。
/// 官方 API（api.curseforge.com）需要 x-api-key（见 <see cref="HyclSecrets.CurseForgeApiKey"/>，默认留空）；
/// 留空时官方源 403，由 mcimirror 镜像（mod.mcimirror.top/curseforge）兜底——与 PCL-CE 行为一致，
/// 因此 Key 未配置时功能仍可用，只是请求先经历一次官方源失败。
/// </summary>
public sealed partial class CurseForgeClient(CompRequester requester)
{
    public const string ApiBase = "https://api.curseforge.com";

    public const int GameId = 432;

    /// <summary>CompType → CurseForge classId（对应 PCL-CE classId 映射表）。</summary>
    public static int ToClassId(CompResourceType type)
        => type switch
        {
            CompResourceType.ModPack => 4471,
            CompResourceType.DataPack => 6945,
            CompResourceType.Shader => 6552,
            CompResourceType.ResourcePack => 12,
            CompResourceType.World => 17,
            _ => 6
        };

    /// <summary>加载器 → CurseForge modLoaderType（与 CompLoaderFilter 数值一致）。</summary>
    public static int? ToModLoaderType(CompLoaderFilter loader)
        => loader == CompLoaderFilter.Any ? null : (int)loader;

    /// <summary>排序 → CurseForge sortField（PCL-CE：Relevance=4 Downloads=6 Follows=2 Newest=11 Updated=3）。</summary>
    private static int ToSortField(CompSortType sort)
        => sort switch
        {
            CompSortType.Relevance => 4,
            CompSortType.Downloads => 6,
            CompSortType.Follows => 2,
            CompSortType.Newest => 11,
            CompSortType.Updated => 3,
            _ => 2
        };

    // ---------- 原始 JSON 模型 ----------

    private sealed class CurseForgeSearchResponse
    {
        [JsonPropertyName("data")] public List<CurseForgeProjectJson>? Data { get; set; }
        [JsonPropertyName("pagination")] public CurseForgePaginationJson? Pagination { get; set; }
    }

    private sealed class CurseForgePaginationJson
    {
        [JsonPropertyName("totalCount")] public int TotalCount { get; set; }
    }

    private sealed class CurseForgeProjectJson
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("summary")] public string? Summary { get; set; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; set; }
        [JsonPropertyName("dateReleased")] public DateTimeOffset? DateReleased { get; set; }
        [JsonPropertyName("dateModified")] public DateTimeOffset? DateModified { get; set; }
        [JsonPropertyName("logo")] public CurseForgeLogoJson? Logo { get; set; }
        [JsonPropertyName("links")] public CurseForgeLinksJson? Links { get; set; }
        [JsonPropertyName("categories")] public List<CurseForgeCategoryJson>? Categories { get; set; }
        [JsonPropertyName("authors")] public List<CurseForgeAuthorJson>? Authors { get; set; }
        [JsonPropertyName("latestFilesIndexes")] public List<CurseForgeFileIndexJson>? LatestFilesIndexes { get; set; }
    }

    private sealed class CurseForgeAuthorJson
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class CurseForgeLogoJson
    {
        [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; set; }
        [JsonPropertyName("url")] public string? Url { get; set; }
    }

    private sealed class CurseForgeLinksJson
    {
        [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; set; }
    }

    private sealed class CurseForgeCategoryJson
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("slug")] public string? Slug { get; set; }
    }

    private sealed class CurseForgeFileIndexJson
    {
        [JsonPropertyName("gameVersion")] public string? GameVersion { get; set; }
        [JsonPropertyName("fileId")] public long FileId { get; set; }
        [JsonPropertyName("modLoaderType")] public int? ModLoaderType { get; set; }
    }

    private sealed class CurseForgeFilesResponse
    {
        [JsonPropertyName("data")] public List<CurseForgeFileJson>? Data { get; set; }
    }

    private sealed class CurseForgeFileJson
    {
        [JsonPropertyName("id")] public long Id { get; set; }
        [JsonPropertyName("modId")] public long ModId { get; set; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
        [JsonPropertyName("fileName")] public string? FileName { get; set; }
        [JsonPropertyName("fileDate")] public DateTimeOffset? FileDate { get; set; }
        [JsonPropertyName("releaseType")] public int ReleaseType { get; set; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("fileLength")] public long FileLength { get; set; }
        [JsonPropertyName("gameVersions")] public List<string>? GameVersions { get; set; }
        [JsonPropertyName("dependencies")] public List<CurseForgeDependencyJson>? Dependencies { get; set; }
        [JsonPropertyName("hashes")] public List<CurseForgeHashJson>? Hashes { get; set; }
        [JsonPropertyName("modules")] public List<CurseForgeModuleJson>? Modules { get; set; }
    }

    private sealed class CurseForgeDependencyJson
    {
        [JsonPropertyName("modId")] public long ModId { get; set; }
        [JsonPropertyName("relationType")] public int RelationType { get; set; }
    }

    private sealed class CurseForgeHashJson
    {
        [JsonPropertyName("value")] public string? Value { get; set; }
        [JsonPropertyName("algo")] public int Algo { get; set; }
    }

    private sealed class CurseForgeModuleJson
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    private sealed class CurseForgeFileIdsRequest
    {
        [JsonPropertyName("fileIds")] public List<long> FileIds { get; set; } = [];
    }

    private sealed class CurseForgeFingerprintsRequest
    {
        [JsonPropertyName("fingerprints")] public List<uint> Fingerprints { get; set; } = [];
    }

    private sealed class CurseForgeFingerprintResponse
    {
        [JsonPropertyName("data")] public CurseForgeFingerprintData? Data { get; set; }
    }

    private sealed class CurseForgeFingerprintData
    {
        [JsonPropertyName("exactMatches")] public List<CurseForgeFingerprintMatch>? ExactMatches { get; set; }
    }

    private sealed class CurseForgeFingerprintMatch
    {
        [JsonPropertyName("id")] public long Id { get; set; }

        [JsonPropertyName("file")] public CurseForgeFileJson? File { get; set; }

        /// <summary>项目最新文件（任意 MC 版本）：本地资源更新检测据此挑选与当前版本兼容的最新文件。</summary>
        [JsonPropertyName("latestFiles")] public List<CurseForgeFileJson>? LatestFiles { get; set; }
    }

    private sealed class CurseForgeModsRequest
    {
        [JsonPropertyName("modIds")] public List<long> ModIds { get; set; } = [];
    }

    private sealed class CurseForgeModsResponse
    {
        [JsonPropertyName("data")] public List<CurseForgeProjectJson>? Data { get; set; }
    }

    /// <summary>指纹反查结果（含与指定 MC 版本兼容的最新文件，供本地资源更新检测）。</summary>
    public sealed record CurseForgeFingerprintInfo(
        long Fingerprint,
        long ProjectId,
        CompFile File,
        CompFile? LatestCompatible);

    /// <summary>
    /// CurseForge 接口 JSON 的 source-gen 元数据：私有嵌套模型经嵌套 context 收录（嵌套 context 可访问
    /// 同类的私有类型），替代反射序列化以支持裁剪。所有模型字段均带 [JsonPropertyName]，命名策略不改变输出。
    /// </summary>
    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(CurseForgeSearchResponse))]
    [JsonSerializable(typeof(CurseForgeProjectResponse))]
    [JsonSerializable(typeof(CurseForgeFilesResponse))]
    [JsonSerializable(typeof(CurseForgeFingerprintResponse))]
    [JsonSerializable(typeof(CurseForgeFileIdsRequest))]
    [JsonSerializable(typeof(CurseForgeFingerprintsRequest))]
    [JsonSerializable(typeof(CurseForgeModsRequest))]
    [JsonSerializable(typeof(CurseForgeModsResponse))]
    private sealed partial class CurseForgeJsonContext : JsonSerializerContext;

    // ---------- API ----------

    /// <summary>
    /// 搜索（对应 PCL-CE GetCurseForgeAddress）：
    /// /v1/mods/search?gameId=432&classId=&categoryId=&modLoaderType=&gameVersion=&searchFilter=&index=&sortField=
    /// </summary>
    public async Task<CompSearchResult> SearchAsync(CompSearchRequest request, CancellationToken cancellationToken = default)
    {
        var url = $"{ApiBase}/v1/mods/search?gameId={GameId}&sortOrder=desc&pageSize={request.Limit}&classId={ToClassId(request.Type)}";
        var tag = request.Tag?.Split('/', 2)[0];
        if (!string.IsNullOrWhiteSpace(tag))
        {
            url += $"&categoryId={Uri.EscapeDataString(tag)}";
        }

        var loader = ToModLoaderType(request.Loader);
        if (loader is not null)
        {
            url += $"&modLoaderType={loader}";
        }

        if (!string.IsNullOrWhiteSpace(request.GameVersion))
        {
            url += $"&gameVersion={Uri.EscapeDataString(request.GameVersion)}";
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            url += $"&searchFilter={Uri.EscapeDataString(request.Query)}";
        }

        if (request.Offset > 0)
        {
            url += $"&index={request.Offset}";
        }

        url += $"&sortField={ToSortField(request.Sort)}";
        var response = await requester
            .GetJsonAsync(url, CurseForgeJsonContext.Default.CurseForgeSearchResponse, cancellationToken, requester.CurseForgeHeaders)
            .ConfigureAwait(false);
        var projects = (response.Data ?? []).Select(MapProject).ToList();
        return new CompSearchResult { Projects = projects, CurseForgeTotal = response.Pagination?.TotalCount ?? -1 };
    }

    /// <summary>项目详情（/v1/mods/{id}）。</summary>
    public async Task<CompProject> GetProjectAsync(string id, CancellationToken cancellationToken = default)
    {
        var response = await requester
            .GetJsonAsync(
                $"{ApiBase}/v1/mods/{Uri.EscapeDataString(id)}", CurseForgeJsonContext.Default.CurseForgeProjectResponse,
                cancellationToken, requester.CurseForgeHeaders)
            .ConfigureAwait(false);
        return MapProject(response.Data ?? throw new CompException($"CurseForge 项目不存在：{id}"));
    }

    private sealed class CurseForgeProjectResponse
    {
        [JsonPropertyName("data")] public CurseForgeProjectJson? Data { get; set; }
    }

    /// <summary>项目文件列表（/v1/mods/{id}/files）。</summary>
    public async Task<IReadOnlyList<CompFile>> GetFilesAsync(
        string projectId,
        CompResourceType type,
        CancellationToken cancellationToken = default)
    {
        var response = await requester.GetJsonAsync(
                $"{ApiBase}/v1/mods/{Uri.EscapeDataString(projectId)}/files?pageSize=10000",
                CurseForgeJsonContext.Default.CurseForgeFilesResponse,
                cancellationToken,
                requester.CurseForgeHeaders)
            .ConfigureAwait(false);
        return (response.Data ?? [])
            .Select(file => BuildFile(file, type))
            .Where(f => f is not null)
            .Cast<CompFile>()
            .OrderByDescending(f => f.ReleaseDate ?? DateTimeOffset.MinValue)
            .ToList();
    }

    /// <summary>按文件 Id 批量取文件元数据（整合包 manifest 用；POST /v1/mods/files）。</summary>
    public async Task<IReadOnlyList<CompFile>> GetFilesByIdsAsync(
        IEnumerable<long> fileIds,
        CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(
            new CurseForgeFileIdsRequest { FileIds = fileIds.ToList() },
            CurseForgeJsonContext.Default.CurseForgeFileIdsRequest);
        var response = await requester
            .PostJsonAsync(
                $"{ApiBase}/v1/mods/files", body, CurseForgeJsonContext.Default.CurseForgeFilesResponse,
                cancellationToken, requester.CurseForgeHeaders)
            .ConfigureAwait(false);
        return (response.Data ?? [])
            .Select(file => BuildFile(file, CompResourceType.Mod))
            .Where(f => f is not null)
            .Cast<CompFile>()
            .ToList();
    }

    /// <summary>
    /// 按 CurseForge 指纹（MurmurHash2）批量反查文件（POST /v1/fingerprints/{gameId}，导出整合包时用）。
    /// 响应不直接回传指纹，因此返回文件列表，由调用方按 sha1 对齐（BuildFile 的 Hash 即 sha1）。
    /// </summary>
    public async Task<IReadOnlyList<CompFile>> LookupFingerprintsAsync(
        IReadOnlyCollection<uint> fingerprints,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CompFile>();
        var distinct = fingerprints.Distinct().ToList();
        foreach (var batch in distinct.Chunk(100))
        {
            var body = JsonSerializer.Serialize(
                new CurseForgeFingerprintsRequest { Fingerprints = batch.ToList() },
                CurseForgeJsonContext.Default.CurseForgeFingerprintsRequest);
            var response = await requester
                .PostJsonAsync(
                    $"{ApiBase}/v1/fingerprints/{GameId}", body, CurseForgeJsonContext.Default.CurseForgeFingerprintResponse,
                    cancellationToken, requester.CurseForgeHeaders)
                .ConfigureAwait(false);
            foreach (var match in response.Data?.ExactMatches ?? [])
            {
                if (match.File is null)
                {
                    continue;
                }

                var file = BuildFile(match.File, CompResourceType.Mod);
                if (file is not null)
                {
                    results.Add(file);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 指纹反查详情（本地资源更新检测）：除命中的文件外，还从项目的 latestFiles 里挑出
    /// 与指定 MC 版本兼容的最新文件（按发布时间从新到旧），供「是否可更新」与一键更新使用。
    /// </summary>
    public async Task<IReadOnlyList<CurseForgeFingerprintInfo>> LookupFingerprintInfosAsync(
        IReadOnlyCollection<uint> fingerprints,
        string? gameVersion,
        CancellationToken cancellationToken = default)
    {
        var results = new List<CurseForgeFingerprintInfo>();
        var distinct = fingerprints.Distinct().ToList();
        foreach (var batch in distinct.Chunk(100))
        {
            var body = JsonSerializer.Serialize(
                new CurseForgeFingerprintsRequest { Fingerprints = batch.ToList() },
                CurseForgeJsonContext.Default.CurseForgeFingerprintsRequest);
            var response = await requester
                .PostJsonAsync(
                    $"{ApiBase}/v1/fingerprints/{GameId}", body, CurseForgeJsonContext.Default.CurseForgeFingerprintResponse,
                    cancellationToken, requester.CurseForgeHeaders)
                .ConfigureAwait(false);
            foreach (var match in response.Data?.ExactMatches ?? [])
            {
                if (match.File is null)
                {
                    continue;
                }

                var file = BuildFile(match.File, CompResourceType.Mod);
                if (file is null)
                {
                    continue;
                }

                CompFile? latest = null;
                if (match.LatestFiles is { Count: > 0 })
                {
                    latest = match.LatestFiles
                        .Where(candidate => string.IsNullOrWhiteSpace(gameVersion)
                                            || candidate.GameVersions?.Any(v =>
                                                v.Equals(gameVersion, StringComparison.OrdinalIgnoreCase)) == true)
                        .OrderByDescending(candidate => candidate.FileDate ?? DateTimeOffset.MinValue)
                        .Select(candidate => BuildFile(candidate, CompResourceType.Mod))
                        .FirstOrDefault(candidate => candidate is not null);
                }

                results.Add(new CurseForgeFingerprintInfo(
                    match.Id,
                    match.File.ModId,
                    file,
                    latest));
            }
        }

        return results;
    }

    /// <summary>批量取项目图标 URL（POST /v1/mods）：projectId → logo 缩略图，本地资源更新检测的图标列。</summary>
    public async Task<IReadOnlyDictionary<long, string>> GetModIconUrlsAsync(
        IReadOnlyCollection<long> projectIds,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<long, string>();
        var ids = projectIds.Where(id => id > 0).Distinct().ToList();
        foreach (var batch in ids.Chunk(100))
        {
            var body = JsonSerializer.Serialize(
                new CurseForgeModsRequest { ModIds = batch.ToList() },
                CurseForgeJsonContext.Default.CurseForgeModsRequest);
            var response = await requester.PostJsonAsync(
                    $"{ApiBase}/v1/mods", body, CurseForgeJsonContext.Default.CurseForgeModsResponse,
                    cancellationToken, requester.CurseForgeHeaders)
                .ConfigureAwait(false);
            foreach (var project in response.Data ?? [])
            {
                var icon = project.Logo?.ThumbnailUrl ?? project.Logo?.Url;
                if (!string.IsNullOrWhiteSpace(icon))
                {
                    result[project.Id] = icon;
                }
            }
        }

        return result;
    }

    // ---------- 映射 ----------

    private static CompProject MapProject(CurseForgeProjectJson json)
    {
        var website = json.Links?.WebsiteUrl ?? "";
        // PCL-CE：CurseForge 的类型以网站链接段落为准
        var type = website.Contains("/mc-mods/", StringComparison.OrdinalIgnoreCase) ? CompResourceType.Mod
            : website.Contains("/modpacks/", StringComparison.OrdinalIgnoreCase) ? CompResourceType.ModPack
            : website.Contains("/texture-packs/", StringComparison.OrdinalIgnoreCase) ? CompResourceType.ResourcePack
            : website.Contains("/shaders/", StringComparison.OrdinalIgnoreCase) ? CompResourceType.Shader
            : website.Contains("/worlds/", StringComparison.OrdinalIgnoreCase) ? CompResourceType.Mod
            : CompResourceType.Mod;
        var loaders = (json.LatestFilesIndexes ?? [])
            .Select(i => i.ModLoaderType)
            .Where(t => t is not null)
            .Distinct()
            .Select(t => ((CompLoaderFilter)t!).ToString().ToLowerInvariant())
            .ToList();
        return new CompProject
        {
            Id = json.Id.ToString(),
            Slug = json.Slug ?? "",
            Title = json.Name ?? "",
            Description = json.Summary ?? "",
            Author = (json.Authors ?? [])
                .Select(a => a.Name ?? "")
                .FirstOrDefault(n => n.Length > 0) ?? "",
            IconUrl = json.Logo?.ThumbnailUrl ?? json.Logo?.Url,
            DownloadCount = json.DownloadCount,
            LastUpdate = json.DateModified ?? json.DateReleased,
            Type = type,
            FromCurseForge = true,
            ModLoaders = loaders,
            Categories = (json.Categories ?? []).Select(c => c.Slug ?? "").Where(s => s.Length > 0).ToList(),
            CategoryIds = (json.Categories ?? []).Select(c => c.Id).ToList(),
            GameVersions = (json.LatestFilesIndexes ?? [])
                .Select(i => i.GameVersion ?? "")
                .Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            WebsiteUrl = website
        };
    }

    /// <summary>构建文件（对应 PCL-CE _BuildFromCurseForge；下载地址经 forgecdn 规整 + 社区源策略排序）。</summary>
    private CompFile? BuildFile(CurseForgeFileJson json, CompResourceType type)
    {
        var idText = json.Id.ToString();
        var url = json.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            // 官方 CDN 兜底路径：https://edge.forgecdn.net/files/{前4位}/{余下}/{文件名}
            url = $"https://edge.forgecdn.net/files/{idText[..4]}/{idText[4..]}/{Uri.EscapeDataString(json.FileName ?? "")}";
        }

        url = url
            .Replace("-service.overwolf.wtf", ".forgecdn.net", StringComparison.Ordinal)
            .Replace("://media.", "://edge.", StringComparison.Ordinal)
            .Replace("://mediafilez.", "://edge.", StringComparison.Ordinal);

        var hash = json.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value
                   ?? json.Hashes?.FirstOrDefault(h => h.Algo == 2)?.Value;
        var dependencies = (json.Dependencies ?? [])
            .Where(d => d.ModId is not 306612 and not 634179)   // Fabric API / Embeddium（PCL-CE 同款排除）
            .Select(d => new CompDependencyRef(d.ModId.ToString(), d.RelationType == 3))
            .ToList();
        return new CompFile
        {
            ProjectId = json.ModId.ToString(),
            Id = idText,
            DisplayName = (json.DisplayName ?? json.FileName ?? "").Trim(),
            FileName = json.FileName ?? "",
            Version = "",
            ReleaseDate = json.FileDate,
            Status = (CompFileStatus)Math.Clamp(json.ReleaseType, 1, 3),
            GameVersions = json.GameVersions ?? [],
            ModLoaders = [],
            Hash = string.IsNullOrEmpty(hash) ? null : hash,
            Size = json.FileLength,
            DownloadUrls = DownloadSources.GetModDownloadUrls(url, requester.Solution),
            Type = type,
            FromCurseForge = true,
            ModuleNames = (json.Modules ?? []).Select(m => m.Name ?? "").Where(n => n.Length > 0).ToList(),
            Dependencies = dependencies.Where(d => d.Required).ToList(),
            OptionalDependencies = dependencies.Where(d => !d.Required).ToList()
        };
    }
}
