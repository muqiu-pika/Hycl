using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 文件指纹的落盘缓存：路径 + 大小 + 修改时间 → SHA-1 与 CurseForge murmur2 指纹。
/// 洞察缓存（<see cref="ContentInsightCache"/>）的 TTL 是 24 小时，过期后重开模组页
/// 必须重新核对每个文件的 SHA-1；没有这层缓存时等于把整个 mods 目录（几百 MB）
/// 再完整读两遍——这是进页面卡顿与磁盘 IO 峰值的主因，也是 PCL-CE 用
/// LocalComp 缓存文件指纹、HMCL 用 LocalAddonFile 缓存校验值的同一动机：
/// 文件没动过就没有任何理由再读一遍。
/// </summary>
public static class ModpackHashCache
{
    private sealed record Entry(long Size, long MTimeTicks, string Sha1, uint Murmur2);

    private sealed record Document(int Version, Dictionary<string, Entry> Entries);

    private const int Version = 1;

    /// <summary>单目录条数上限：防止遍历超大整合包时缓存文件无限膨胀。</summary>
    private const int Limit = 8192;

    private static string FilePath(string cacheDirectory) => Path.Combine(cacheDirectory, "hash-cache.json");

    /// <summary>读取一个目录对应的指纹缓存（按目录键隔离，避免不同实例互相污染）。</summary>
    public static Dictionary<string, (string Sha1, uint Murmur2)> Load(
        string cacheDirectory,
        string directory)
    {
        var result = new Dictionary<string, (string, uint)>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = FilePath(cacheDirectory);
            if (!File.Exists(path))
            {
                return result;
            }

            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (document is null || document.Version != Version)
            {
                return result;
            }

            var prefix = KeyPrefix(directory);
            foreach (var (key, entry) in document.Entries)
            {
                if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result[key] = (entry.Sha1, entry.Murmur2);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 缓存损坏按无缓存处理
            return new Dictionary<string, (string, uint)>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// 写入指纹缓存。只更新本目录的键，其它目录的条目原样保留；
    /// 仍存在的文件的 Size/MTime 由调用方保证，未再出现的旧条目一并清掉。
    /// </summary>
    public static void Save(
        string cacheDirectory,
        string directory,
        IReadOnlyDictionary<string, (long Size, long MTimeTicks, string Sha1, uint Murmur2)> entries)
    {
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var path = FilePath(cacheDirectory);
            var merged = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
                    if (existing is { Version: Version })
                    {
                        foreach (var (key, value) in existing.Entries)
                        {
                            merged[key] = value;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    merged.Clear();
                }
            }

            var prefix = KeyPrefix(directory);
            foreach (var key in merged.Keys.Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                merged.Remove(key);
            }

            foreach (var (key, value) in entries.Take(Limit))
            {
                merged[key] = new Entry(value.Size, value.MTimeTicks, value.Sha1, value.Murmur2);
            }

            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(Version, merged)));
            File.Move(temporary, path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存只是加速层
        }
    }

    /// <summary>生成缓存键：目录 + 文件名（同目录内文件名唯一）。</summary>
    public static string MakeKey(string directory, string filePath)
        => KeyPrefix(directory) + Path.GetFileName(filePath);

    private static string KeyPrefix(string directory)
        => ShortHash(directory) + "|";

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
}
