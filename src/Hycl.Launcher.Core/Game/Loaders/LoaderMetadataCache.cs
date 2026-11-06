using System.Collections.Concurrent;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// 加载器元数据缓存：内存命中直接返回；源失败后进入负缓存（30 秒），
/// 避免列表页反复卡在同一个坏源上（maven.neoforged.net 在部分网络下会拖满超时）。
/// </summary>
public sealed class LoaderMetadataCache
{
    /// <summary>成功结果缓存时长。</summary>
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);

    /// <summary>失败负缓存时长。</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);

    private sealed record Entry(object? Value, DateTimeOffset ExpiresAt, Exception? Error);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async Task<T> GetOrLoadAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken = default)
    {
        if (_entries.TryGetValue(key, out var entry))
        {
            if (DateTimeOffset.Now < entry.ExpiresAt)
            {
                if (entry.Error is not null)
                {
                    throw new LoaderMetadataException($"加载器数据此前获取失败（稍后自动重试）：{entry.Error.Message}", entry.Error);
                }

                if (entry.Value is T typed)
                {
                    return typed;
                }
            }
            else
            {
                _entries.TryRemove(key, out _);
            }
        }

        try
        {
            var value = await factory(cancellationToken).ConfigureAwait(false);
            _entries[key] = new Entry(value, DateTimeOffset.Now + SuccessTtl, null);
            return value;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not LoaderMetadataException)
        {
            _entries[key] = new Entry(null, DateTimeOffset.Now + FailureTtl, ex);
            throw;
        }
    }

    /// <summary>清空缓存（下载源设置变化或强制刷新时调用）。</summary>
    public void Clear() => _entries.Clear();
}

/// <summary>
/// 源明确声明"该 Minecraft 版本没有可用资源"：不是网络失败，
/// 上层据此展示"暂无对应资源"而不是"配置获取失败"。
/// </summary>
public sealed class LoaderUnsupportedException : LoaderMetadataException
{
    public LoaderUnsupportedException(string message)
        : base(message)
    {
    }
}

/// <summary>加载器元数据获取失败（含负缓存重抛）。</summary>
public class LoaderMetadataException : Exception
{
    public LoaderMetadataException(string message)
        : base(message)
    {
    }

    public LoaderMetadataException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
