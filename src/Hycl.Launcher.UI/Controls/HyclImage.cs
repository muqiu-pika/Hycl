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
/// 只有成功解码为位图的数据才会写入磁盘缓存（避免把 CDN 的报错页/JSON 缓存下来后
/// 每次进页都反复解码失败）；被替换的旧位图立即释放，防止整页图标逐次切换时堆积。
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

    /// <summary>解码宽度上限：显示 64px 的图标不必解码 512px 原图（HMCL 的降采样解码经验）。</summary>
    private const int DecodeWidth = 128;

    /// <summary>加载结束（true=已显示网络图片；false=地址为空或加载失败），供图标回退逻辑使用。</summary>
    public event EventHandler<bool>? LoadCompleted;

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
            DisposeSource();
            LoadCompleted?.Invoke(this, false);
            return;
        }

        var url = Url;
        _ = LoadAsync(url);
    }

    private async Task LoadAsync(string url)
    {
        DisposeSource();
        try
        {
            var bytes = await Downloads.GetOrAdd(url, DownloadAndCacheAsync).ConfigureAwait(true);
            if (bytes is null || Url != url)
            {
                if (Url == url)
                {
                    LoadCompleted?.Invoke(this, false);
                }

                return;
            }

            // 降采样解码：源图 512px 也只解出 128px 位图，几十个卡片的内存差一个数量级
            var bitmap = Bitmap.DecodeToWidth(new MemoryStream(bytes), DecodeWidth);
            DisposeSource();
            Source = bitmap;
            LoadCompleted?.Invoke(this, true);
        }
        catch (Exception)
        {
            // 解码失败说明缓存/响应不是有效图片：清掉对应的磁盘缓存文件，
            // 避免同一坏数据在 TTL 内每次进页都重复解码失败。
            TryDeleteCache(url);
            if (Url == url)
            {
                LoadCompleted?.Invoke(this, false);
            }
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

            // 只有确认为有效位图才落盘：CDN 的报错页 / JSON / 截断响应不进缓存
            if (!TryValidateBitmap(data))
            {
                return null;
            }

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

    /// <summary>快速校验字节流是 PNG/JPEG/GIF/WebP 等可解码图片（只看文件头，不做完整解码）。</summary>
    private static bool TryValidateBitmap(byte[] data)
    {
        if (data.Length < 12)
        {
            return false;
        }

        // PNG
        if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
        {
            return true;
        }

        // JPEG
        if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return true;
        }

        // GIF
        if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46)
        {
            return true;
        }

        // WebP（RIFF....WEBP）
        if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50)
        {
            return true;
        }

        // BMP
        if (data[0] == 0x42 && data[1] == 0x4D)
        {
            return true;
        }

        return false;
    }

    /// <summary>删除 URL 对应的磁盘缓存文件（解码失败时清理坏数据）。</summary>
    private static void TryDeleteCache(string url)
    {
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            File.Delete(GetCachePath(url));
        }
        catch (Exception)
        {
            // 文件不存在/被占用等：忽略
        }
    }

    /// <summary>释放当前 Source 位图（每次替换/清空前调用，避免旧位图堆积）。</summary>
    private void DisposeSource()
    {
        if (Source is Bitmap bitmap)
        {
            Source = null;
            bitmap.Dispose();
        }
    }

    private static string GetCachePath(string url)
    {
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(url)));
        return Path.Combine(CacheRoot, hash + ".img");
    }
}
