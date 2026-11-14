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

/// <summary>
/// 一个分区（实例内容目录 + 资源类型）的缓存快照：文件集签名 + 每个文件的洞察结果。
/// 签名只依赖「路径 / 大小 / 修改时间」，不做文件哈希，因此判断是否需要联网的代价很低——
/// 打开实例管理页时先比签名，一致就直接用缓存渲染，不再发任何社区接口请求。
/// </summary>
public sealed record ContentInsightSnapshot
{
    /// <summary>文件集签名（路径 + 大小 + 修改时间的有序哈希）。</summary>
    public string Signature { get; init; } = "";

    public IReadOnlyList<ContentInsightEntry> Entries { get; init; } = [];

    public long SavedAtTicks { get; init; }

    public bool IsEmpty => Entries.Count == 0;
}

/// <summary>按「实例内容目录 + 资源类型」为单位持久化洞察结果（存数据目录 cache，不污染游戏目录）。</summary>
public static class ContentInsightCache
{
    private static readonly TimeSpan FreshTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.General);

    public static bool IsFresh(ContentInsightEntry entry)
        => DateTime.UtcNow.Ticks - entry.CheckedAtTicks < FreshTtl.Ticks;

    public static bool IsFresh(ContentInsightSnapshot snapshot)
        => snapshot.SavedAtTicks > 0 && DateTime.UtcNow.Ticks - snapshot.SavedAtTicks < FreshTtl.Ticks;

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

    /// <summary>
    /// 计算文件集签名：只看路径、大小与修改时间（不读文件内容），
    /// 因此增删改任意一个资源都会让签名变化，而「只是打开页面看看」不会。
    /// </summary>
    public static string ComputeSignature(IEnumerable<(string Path, long Size, long MTimeTicks)> files)
    {
        var builder = new StringBuilder();
        foreach (var (path, size, mtimeTicks) in files
                     .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase))
        {
            builder.Append(path)
                .Append('|')
                .Append(size)
                .Append('|')
                .Append(mtimeTicks)
                .Append('\n');
        }

        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(builder.ToString())))
            .ToLowerInvariant()[..16];
    }

    /// <summary>读取分区快照；无缓存/损坏/旧格式（纯数组）时返回空快照（签名视为不匹配）。</summary>
    public static ContentInsightSnapshot Load(string cacheDirectory, string key)
    {
        var file = Path.Combine(cacheDirectory, "content-insight", key + ".json");
        if (!File.Exists(file))
        {
            return new ContentInsightSnapshot();
        }

        try
        {
            var text = File.ReadAllText(file);
            if (text.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                // 旧版本写入的是纯数组：条目本身可用，但没有签名（下次打开会强制刷新一次并升级格式）
                var legacy = JsonSerializer.Deserialize<List<ContentInsightEntry>>(text, SerializerOptions) ?? [];
                return new ContentInsightSnapshot { Entries = legacy };
            }

            return JsonSerializer.Deserialize<ContentInsightSnapshot>(text, SerializerOptions)
                   ?? new ContentInsightSnapshot();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 缓存损坏按无缓存处理，下一次检查会重建
            return new ContentInsightSnapshot();
        }
    }

    public static void Save(string cacheDirectory, string key, ContentInsightSnapshot snapshot)
    {
        try
        {
            var directory = Path.Combine(cacheDirectory, "content-insight");
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, key + ".json");
            var temporary = file + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(temporary, file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是加速层，写失败不影响功能
        }
    }

    /// <summary>删除某分区的缓存（「重新检测」用）。</summary>
    public static void Clear(string cacheDirectory, string key)
    {
        try
        {
            var file = Path.Combine(cacheDirectory, "content-insight", key + ".json");
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就让它自然过期，不强求
        }
    }
}
