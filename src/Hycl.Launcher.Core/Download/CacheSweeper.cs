using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// 启动时后台清理已过期的磁盘缓存（只删不被读判逻辑使用的死文件）：
/// - comp-api JSON 缓存（TTL 6 小时）：过期的搜索/详情/文件列表文件在读取路径上已被 TTL 判死，落盘只会白白占盘；
/// - HyclImage 图片缓存（TTL 14 天）：过期的图标文件同理；
/// - 残留的 .tmp 临时文件（原子写入中途崩溃遗留，保留 10 分钟缓冲）。
/// 目录不存在时直接跳过；删除失败只计入汇总，不影响启动。
/// </summary>
public static class CacheSweeper
{
    private static readonly TimeSpan CompApiTtl = TimeSpan.FromHours(6);

    private static readonly TimeSpan ImageCacheTtl = TimeSpan.FromDays(14);

    /// <summary>只清理超过该时长未写入的 .tmp 临时文件，避免误删在途原子写入。</summary>
    private static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(10);

    public readonly record struct Result(int Scanned, int Deleted, int Failed);

    /// <summary>可变计数器（record 的 init 属性不能在中途累加）。</summary>
    private sealed class Counter
    {
        public int Scanned;
        public int Deleted;
        public int Failed;
    }

    public static Result Run(string cacheDirectory, LauncherLogger logger)
    {
        var counter = new Counter();
        var compApiDirectory = Path.Combine(cacheDirectory, "comp-api");
        var imageDirectory = Path.Combine(Path.GetTempPath(), "Hycl", "Cache", "Images");

        SweepDirectory(compApiDirectory, "*.json", CompApiTtl, counter);
        SweepDirectory(imageDirectory, "*.img", ImageCacheTtl, counter);
        SweepDirectory(compApiDirectory, "*.tmp", MinimumAge, counter);
        SweepDirectory(imageDirectory, "*.tmp", MinimumAge, counter);

        logger.Info($"缓存清理 comp-api/图片：扫描 {counter.Scanned} 个过期文件，删除 {counter.Deleted}，失败 {counter.Failed}");
        return new Result(counter.Scanned, counter.Deleted, counter.Failed);
    }

    private static void SweepDirectory(string directory, string pattern, TimeSpan ttl, Counter counter)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, pattern))
            {
                counter.Scanned++;
                try
                {
                    if (DateTimeOffset.UtcNow - File.GetLastWriteTimeUtc(file) < ttl)
                    {
                        continue;
                    }

                    File.Delete(file);
                    counter.Deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    counter.Failed++;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            counter.Failed++;
        }
    }
}
