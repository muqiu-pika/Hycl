using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 本地资源洞察结果的一行缓存：文件指纹 + 已识别的社区项目信息 + 最新兼容版本信息。
/// 未变动的文件（路径/大小/修改时间/SHA-1 一致且检查时间在 TTL 内）在重开页面时直接复用缓存，
/// 不再对每个文件发网络请求；图标 URL 持久缓存，由 HyclImage 自身的磁盘缓存承接加载。
/// </summary>
public sealed record ContentInsightEntry
{
    public string Path { get; init; } = "";

    public long Size { get; init; }

    public long MTimeTicks { get; init; }

    public string Sha1 { get; init; } = "";

    /// <summary>来源标签（Modrinth / CurseForge / 本地）。</summary>
    public string Source { get; init; } = "本地";

    public string ProjectId { get; init; } = "";

    public string IconUrl { get; init; } = "";

    /// <summary>当前安装文件对应的版本 Id（Modrinth version id / CurseForge file id）。</summary>
    public string InstalledVersionId { get; init; } = "";

    public string LatestVersionId { get; init; } = "";

    /// <summary>最新兼容版本展示名（Modrinth version_number / CF displayName）。</summary>
    public string LatestVersionName { get; init; } = "";

    public string LatestFileName { get; init; } = "";

    public string LatestDownloadUrl { get; init; } = "";

    public string LatestSha1 { get; init; } = "";

    public bool IsFromCurseForge { get; init; }

    public long CheckedAtTicks { get; init; }
}

/// <summary>按「实例内容目录 + 资源类型」为单位持久化洞察结果（存数据目录 cache，不污染游戏目录）。</summary>
public static class ContentInsightCache
{
    private static readonly TimeSpan FreshTtl = TimeSpan.FromHours(24);

    public static bool IsFresh(ContentInsightEntry entry)
        => DateTime.UtcNow.Ticks - entry.CheckedAtTicks < FreshTtl.Ticks;

    /// <summary>文件未变动（路径/大小/修改时间/SHA-1 一致）时才复用缓存。</summary>
    public static bool MatchesFile(ContentInsightEntry entry, string path, long size, long mtimeTicks, string sha1)
        => entry.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
           && entry.Size == size
           && entry.MTimeTicks == mtimeTicks
           && entry.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase);

    public static string MakeKey(string contentDirectory, string sectionKey)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(contentDirectory + "|" + sectionKey));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    public static IReadOnlyList<ContentInsightEntry> Load(string cacheDirectory, string key)
    {
        try
        {
            var file = Path.Combine(cacheDirectory, "content-insight", key + ".json");
            if (!File.Exists(file))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<ContentInsightEntry>>(File.ReadAllText(file)) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 缓存损坏按无缓存处理，下一次检查会重建
            return [];
        }
    }

    public static void Save(string cacheDirectory, string key, IReadOnlyList<ContentInsightEntry> entries)
    {
        try
        {
            var directory = Path.Combine(cacheDirectory, "content-insight");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, key + ".json");
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries));
            File.Move(temporary, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是加速层，写失败不影响功能
        }
    }
}
