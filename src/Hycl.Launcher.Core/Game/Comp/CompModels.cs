using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>社区资源类型（对应 PCL-CE CompType；Modrinth project_type / CurseForge classId 映射见各客户端）。</summary>
public enum CompResourceType
{
    Mod = 0,
    ModPack = 1,
    ResourcePack = 2,
    Shader = 3,
    DataPack = 4,
    World = 7
}

/// <summary>Mod 加载器筛选（数值与 CurseForge modLoaderType 对齐，0/4/5/6）。</summary>
public enum CompLoaderFilter
{
    Any = 0,
    Forge = 1,
    Fabric = 4,
    Quilt = 5,
    NeoForge = 6
}

/// <summary>搜索排序（对应 PCL-CE CompSortType）。</summary>
public enum CompSortType
{
    Default = 1,
    Relevance = 2,
    Downloads = 3,
    Follows = 4,
    Newest = 5,
    Updated = 6
}

/// <summary>搜索来源（对应 PCL-CE CompSourceType）。</summary>
[Flags]
public enum CompSourceType
{
    CurseForge = 1,
    Modrinth = 2,
    Any = 3
}

/// <summary>文件状态（Release/Beta/Alpha；CurseForge releaseType 与 Modrinth version_type 统一）。</summary>
public enum CompFileStatus
{
    Release = 1,
    Beta = 2,
    Alpha = 3
}

/// <summary>社区资源项目（搜索结果条目 / 详情）。</summary>
public sealed record CompProject
{
    public required string Id { get; init; }

    public string Slug { get; init; } = "";

    public string Title { get; init; } = "";

    public string Description { get; init; } = "";

    public string? IconUrl { get; init; }

    public long DownloadCount { get; init; }

    public DateTimeOffset? LastUpdate { get; init; }

    public CompResourceType Type { get; init; }

    public bool FromCurseForge { get; init; }

    /// <summary>项目支持的加载器（小写：forge/fabric/quilt/neoforge…）。</summary>
    public IReadOnlyList<string> ModLoaders { get; init; } = [];

    /// <summary>项目分类标签（slug，如 optimization / technology）。</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    public string WebsiteUrl { get; init; } = "";
}

/// <summary>项目的某个可下载文件（版本）。</summary>
public sealed record CompDependencyRef(string ProjectId, bool Required);

/// <summary>社区资源文件（对应 PCL-CE CompFile）。</summary>
public sealed record CompFile
{
    public required string ProjectId { get; init; }

    /// <summary>文件条目 Id（CurseForge 数字 id / Modrinth version id）。</summary>
    public required string Id { get; init; }

    /// <summary>展示名（CurseForge displayName / Modrinth version_number）。</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>实际文件名（含扩展名）。</summary>
    public string FileName { get; init; } = "";

    /// <summary>版本号（Modrinth version_number；CurseForge 无则为空）。</summary>
    public string Version { get; init; } = "";

    public DateTimeOffset? ReleaseDate { get; init; }

    public CompFileStatus Status { get; init; } = CompFileStatus.Release;

    public IReadOnlyList<string> GameVersions { get; init; } = [];

    public IReadOnlyList<string> ModLoaders { get; init; } = [];

    /// <summary>校验哈希（Modrinth sha1；CurseForge sha1 或 md5）。</summary>
    public string? Hash { get; init; }

    public long Size { get; init; }

    /// <summary>按社区源策略排序后的候选下载地址。</summary>
    public IReadOnlyList<string> DownloadUrls { get; init; } = [];

    public CompResourceType Type { get; init; } = CompResourceType.Mod;

    public bool FromCurseForge { get; init; }

    /// <summary>CurseForge 文件内的模块名列表（用于判定落盘目录）。</summary>
    public IReadOnlyList<string> ModuleNames { get; init; } = [];

    public IReadOnlyList<CompDependencyRef> Dependencies { get; init; } = [];

    public IReadOnlyList<CompDependencyRef> OptionalDependencies { get; init; } = [];

    public string StatusText => Status switch
    {
        CompFileStatus.Beta => "Beta",
        CompFileStatus.Alpha => "Alpha",
        _ => "Release"
    };
}

/// <summary>搜索请求（对应 PCL-CE CompProjectRequest 的 Hycl 子集）。</summary>
public sealed record CompSearchRequest
{
    public required CompResourceType Type { get; init; }

    public string Query { get; init; } = "";

    /// <summary>null 表示全部游戏版本。</summary>
    public string? GameVersion { get; init; }

    public CompLoaderFilter Loader { get; init; } = CompLoaderFilter.Any;

    /// <summary>分类标签，格式 "curseforgeId/modrinthSlug"，允许任一侧为空。</summary>
    public string? Tag { get; init; }

    public CompSortType Sort { get; init; } = CompSortType.Default;

    public CompSourceType Source { get; init; } = CompSourceType.Any;

    public int Offset { get; init; }

    public int Limit { get; init; } = 40;
}

/// <summary>搜索结果：两源各自的命中总数与合并后的项目列表。</summary>
public sealed record CompSearchResult
{
    public IReadOnlyList<CompProject> Projects { get; init; } = [];

    public int CurseForgeTotal { get; init; } = -1;

    public int ModrinthTotal { get; init; } = -1;
}

/// <summary>社区资源的原始 JSON 条目（保留给 Mrpack/Manifest 等场景复用）。</summary>
public sealed class CompRawFileEntry
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("downloads")]
    public List<string>? Downloads { get; set; }

    [JsonPropertyName("fileSize")]
    public long FileSize { get; set; }

    [JsonPropertyName("hashes")]
    public Dictionary<string, string>? Hashes { get; set; }

    [JsonPropertyName("env")]
    public Dictionary<string, string>? Env { get; set; }
}
