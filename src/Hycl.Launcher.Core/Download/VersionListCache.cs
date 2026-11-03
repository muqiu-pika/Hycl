using System.Text.Json;
using System.Text.Json.Serialization;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// 版本列表磁盘缓存：{数据目录}/Cache/version_manifest.json，命中后可离线展示并后台静默刷新。
/// </summary>
public sealed class VersionListCache
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _filePath;

    public VersionListCache(string cacheDirectory)
    {
        Directory.CreateDirectory(cacheDirectory);
        _filePath = Path.Combine(cacheDirectory, "version_manifest.json");
    }

    /// <summary>UVMC 未收录版本清单的缓存路径（对应 PCL-CE Cache\uvmc-download.json）。</summary>
    public string UnlistedCachePath => Path.Combine(
        Path.GetDirectoryName(_filePath) ?? ".",
        "uvmc-download.json");

    public async Task<(VersionManifest Manifest, DateTimeOffset FetchedAt)?> TryReadAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return null;
            }

            var json = await File.ReadAllTextAsync(_filePath, cancellationToken).ConfigureAwait(false);
            var wrapper = JsonSerializer.Deserialize<CachedManifestWrapper>(json, JsonOptions);
            if (wrapper?.Manifest?.Versions is not { Count: > 0 } || wrapper.FetchedAt == default)
            {
                return null;
            }

            return (wrapper.Manifest, wrapper.FetchedAt);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task WriteAsync(VersionManifest manifest, CancellationToken cancellationToken = default)
    {
        try
        {
            var wrapper = new CachedManifestWrapper
            {
                FetchedAt = DateTimeOffset.Now,
                Manifest = manifest
            };
            var json = JsonSerializer.Serialize(wrapper, JsonOptions);
            await File.WriteAllTextAsync(_filePath, json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写入失败不影响主流程
        }
    }

    public async Task<string?> TryReadTextAsync(string filePath, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            if (DateTimeOffset.Now - File.GetLastWriteTime(filePath) > ttl)
            {
                return null;
            }

            return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task WriteTextAsync(string filePath, string content, CancellationToken cancellationToken = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
            await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 缓存写入失败不影响主流程
        }
    }

    private sealed class CachedManifestWrapper
    {
        [JsonPropertyName("fetchedAt")]
        public DateTimeOffset FetchedAt { get; set; }

        [JsonPropertyName("manifest")]
        public VersionManifest? Manifest { get; set; }
    }
}
