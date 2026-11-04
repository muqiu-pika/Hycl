using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace Hycl.Launcher.UI.Controls;

/// <summary>
/// 带磁盘缓存的网络图片控件（对应 PCL-CE 的 MyImage）：
/// 同一 URL 并发请求共享一次下载；缓存命中秒显（14 天有效期）；
/// 下载期间显示空白（由外层 Border 的灰底承担占位），失败回退过期缓存。
/// </summary>
public class HyclImage : Image
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
    })
    {
        // 部分 CDN 会拒绝空 User-Agent
        DefaultRequestHeaders = { { "User-Agent", "Hycl-Launcher" } },
    };

    /// <summary>下载去重：同一 URL 的并发请求复用同一任务（对应 PCL-CE DownloadTasks）。</summary>
    private static readonly ConcurrentDictionary<string, Task<byte[]?>> Downloads = new();

    private static readonly string CacheRoot = Path.Combine(Path.GetTempPath(), "Hycl", "Cache", "Images");

    private static readonly TimeSpan CacheTtl = TimeSpan.FromDays(14);

    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<HyclImage, string?>(nameof(Url));

    /// <summary>图片下载地址（本地绝对路径亦可用）。</summary>
    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty)
        {
            OnUrlChanged();
        }
    }

    private void OnUrlChanged()
    {
        if (Url is null)
        {
            Source = null;
            return;
        }

        var url = Url;
        _ = LoadAsync(url);
    }

    private async Task LoadAsync(string url)
    {
        Source = null;
        try
        {
            var bytes = await Downloads.GetOrAdd(url, DownloadAndCacheAsync).ConfigureAwait(true);
            if (bytes is null || Url != url)
            {
                return;
            }

            using var stream = new MemoryStream(bytes);
            Source = new Bitmap(stream);
        }
        catch (Exception)
        {
            // 保持占位空白
        }
    }

    private static async Task<byte[]?> DownloadAndCacheAsync(string url)
    {
        try
        {
            // 本地路径直接读取（占位资源等场景）
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return await File.ReadAllBytesAsync(url).ConfigureAwait(true);
            }

            var path = GetCachePath(url);
            if (File.Exists(path) && DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(path) < CacheTtl)
            {
                return await File.ReadAllBytesAsync(path).ConfigureAwait(true);
            }

            Directory.CreateDirectory(CacheRoot);
            var data = await Http.GetByteArrayAsync(url).ConfigureAwait(true);

            // 先写临时文件再原子改名，避免并发写坏缓存
            var temp = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temp, data).ConfigureAwait(true);
            File.Move(temp, path, true);
            return data;
        }
        catch (Exception)
        {
            // 网络失败回退过期磁盘缓存
            try
            {
                var path = GetCachePath(url);
                return File.Exists(path) ? await File.ReadAllBytesAsync(path).ConfigureAwait(true) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        finally
        {
            Downloads.TryRemove(url, out _);
        }
    }

    private static string GetCachePath(string url)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(CacheRoot, hash + ".img");
    }
}
