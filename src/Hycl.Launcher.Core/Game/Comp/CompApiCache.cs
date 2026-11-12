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
        return TryGet<List<CompFile>>(cacheDirectory, $"files-{MakeKey(source + "|" + projectId)}", DetailTtl);
    }

    public static void SaveFiles(string cacheDirectory, string source, string projectId, IReadOnlyList<CompFile> files)
        => Save(cacheDirectory, $"files-{MakeKey(source + "|" + projectId)}", files);

    private static string MakeKey(string raw)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static T? TryGet<T>(string cacheDirectory, string fileName, TimeSpan ttl)
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

    private static void Save<T>(string cacheDirectory, string fileName, T value)
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
