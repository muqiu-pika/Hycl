using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 社区资源 API 结果缓存（搜索结果 + 项目详情；参考 PCL-CE 的 compProjectCache 思路并落盘）：
/// 同一请求 6 小时内直接复用磁盘结果，打开分区不再每次全量请求；
/// 图标 URL 随项目数据一起缓存，图片本体由 HyclImage 的磁盘缓存（14 天）承接。
/// 缓存按内容目录无关的全局键存储（搜索词/筛选/页码/来源即键）。
/// </summary>
public static class CompApiCache
{
    private static readonly TimeSpan SearchTtl = TimeSpan.FromHours(6);

    private static readonly TimeSpan DetailTtl = TimeSpan.FromHours(6);

    // 文件列表内存层：详情页来回切换时直接复用反序列化结果。磁盘命中虽然免了网络，
    // 但 CurseForge 的文件列表（pageSize=10000）落盘可达数 MB，若每次点击都在
    // 调用线程（UI 线程）同步读盘 + 反序列化，详情页打开会明显卡顿。
    // 容量按「CompFile 总数」预算而非条数：单条列表可达上万 CompFile（含
    // GameVersions/ModLoaders 子集合，每个约 KB 级），按条数封顶时 8 条热门项目
    // 最坏可到 ~160MB 常驻；按总文件数预算（预算 2 万 ≈ 数十 MB 上限）后，
    // 少数热门大项目与多个小项目都能共存。淘汰逐条移除最旧条目（整体 Clear 会造成 GC 尖峰）。
    private static readonly ConcurrentDictionary<string, (DateTimeOffset CachedAt, IReadOnlyList<CompFile> Files)> FileMemoryCache = new();

    /// <summary>内存层允许驻留的 CompFile 总数预算（约 2 万个 ≈ 数十 MB 上限）。</summary>
    private const int FileMemoryBudget = 20_000;

    private static int FileMemoryCacheCount
        => FileMemoryCache.Aggregate(0, (sum, pair) => sum + pair.Value.Files.Count);

    private static string CacheDirectory(string cacheDirectory) => Path.Combine(cacheDirectory, "comp-api");

    /// <summary>尝试取缓存的搜索结果；未命中或过期返回 null。</summary>
    public static CompSearchResult? TryGetSearch(string cacheDirectory, string requestKey)
    {
        return TryGet<CompSearchResult>(cacheDirectory, $"search-{MakeKey(requestKey)}", SearchTtl);
    }

    /// <summary>写入搜索结果缓存（原子的临时文件 + 改名）。</summary>
    public static void SaveSearch(string cacheDirectory, string requestKey, CompSearchResult result)
        => Save(cacheDirectory, $"search-{MakeKey(requestKey)}", result);

    /// <summary>尝试取缓存的项目详情（按 来源+项目Id）。</summary>
    public static CompProject? TryGetProject(string cacheDirectory, string source, string projectId)
    {
        return TryGet<CompProject>(cacheDirectory, $"project-{MakeKey(source + "|" + projectId)}", DetailTtl);
    }

    public static void SaveProject(string cacheDirectory, string source, string projectId, CompProject project)
        => Save(cacheDirectory, $"project-{MakeKey(source + "|" + projectId)}", project);

    /// <summary>尝试取缓存的项目文件/版本列表（详情页「文件 / 版本列表」数据源）。</summary>
    public static IReadOnlyList<CompFile>? TryGetFiles(string cacheDirectory, string source, string projectId)
    {
        // 文件缓存带 schema 版本：CurseForge 文件的加载器字段曾整段缺失（ModLoaders 为空），
        // 旧缓存不能再用，否则详情页的加载器分组仍会错。改字段结构时同步递增版本号。
        return TryGet<List<CompFile>>(cacheDirectory, $"files2-{MakeKey(source + "|" + projectId)}", DetailTtl);
    }

    /// <summary>
    /// 文件列表的完整读取链：内存层（命中即返回，零反序列化）→ 磁盘层（后台线程读盘解析，
    /// 避免数 MB JSON 卡住调用线程）→ 在线加载（<paramref name="loader"/>，结果写回磁盘）。
    /// 所有结果都会进入内存层，详情页来回切换不再重复解析。
    /// </summary>
    public static async Task<IReadOnlyList<CompFile>> GetOrLoadFilesAsync(
        string cacheDirectory,
        string source,
        string projectId,
        Func<CancellationToken, Task<IReadOnlyList<CompFile>>> loader,
        CancellationToken cancellationToken = default)
    {
        var key = MakeKey(source + "|" + projectId);
        if (FileMemoryCache.TryGetValue(key, out var cached)
            && DateTimeOffset.UtcNow - cached.CachedAt <= DetailTtl)
        {
            return cached.Files;
        }

        IReadOnlyList<CompFile>? files = await Task.Run(
                () => TryGet<List<CompFile>>(cacheDirectory, $"files2-{key}", DetailTtl),
                cancellationToken).ConfigureAwait(false);
        if (files is null)
        {
            files = await loader(cancellationToken).ConfigureAwait(false);
            // 磁盘写入（含整个列表的 JSON 序列化）同样不能在调用线程上做
            _ = Task.Run(() => Save(cacheDirectory, $"files2-{key}", files), CancellationToken.None);
        }

        if (FileMemoryCacheCount + files.Count > FileMemoryBudget)
        {
            // 逐条淘汰最旧条目直到预算内，避免整体 Clear 一次性丢弃全部大列表造成 GC 尖峰
            foreach (var oldest in FileMemoryCache
                         .OrderBy(pair => pair.Value.CachedAt)
                         .ToList())
            {
                if (FileMemoryCacheCount + files.Count <= FileMemoryBudget)
                {
                    break;
                }

                FileMemoryCache.TryRemove(oldest.Key, out _);
            }
        }

        FileMemoryCache[key] = (DateTimeOffset.UtcNow, files);
        return files;
    }

    public static void SaveFiles(string cacheDirectory, string source, string projectId, IReadOnlyList<CompFile> files)
        => Save(cacheDirectory, $"files2-{MakeKey(source + "|" + projectId)}", files);

    private static string MakeKey(string raw)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>通用缓存读取（供分类标签等其它社区元数据复用）。</summary>
    public static T? TryGet<T>(string cacheDirectory, string fileName, TimeSpan ttl)
        where T : class
    {
        try
        {
            var path = Path.Combine(CacheDirectory(cacheDirectory), fileName + ".json");
            if (!File.Exists(path)
                || DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) > ttl)
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>通用缓存写入。</summary>
    public static void Save<T>(string cacheDirectory, string fileName, T value)
    {
        try
        {
            var directory = CacheDirectory(cacheDirectory);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, fileName + ".json");
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是加速层，写失败不影响功能
        }
    }
}
