using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Download;

/// <summary>
/// 启动时一次性清理上次异常退出遗留的下载临时文件（仅 <c>.hycl-dl</c>）。
/// 只扫描游戏目录下的 assets/objects、libraries、versions 三棵子树；
/// 不跟随目录/文件符号链接，越界目标跳过，删除失败只计入汇总，不影响启动。
/// </summary>
public static class StaleTempCleaner
{
    private static readonly string[] SubDirectories =
    [
        Path.Combine("assets", "objects"),
        "libraries",
        "versions"
    ];

    public readonly record struct Result(int Scanned, int Deleted, int Skipped, int Failed);

    public static Result Run(string gameDirectory, LauncherLogger logger)
    {
        var scanned = 0;
        var deleted = 0;
        var skipped = 0;
        var failed = 0;

        try
        {
            var root = Path.GetFullPath(gameDirectory);
            if (!string.IsNullOrWhiteSpace(gameDirectory) && Directory.Exists(root))
            {
                foreach (var subDirectory in SubDirectories)
                {
                    var start = Path.GetFullPath(Path.Combine(root, subDirectory));
                    if (!IsInside(root, start) || !Directory.Exists(start))
                    {
                        continue;
                    }

                    // 起始目录本身是重解析点（目录联接/符号链接）时不进入
                    if (IsReparsePoint(start))
                    {
                        continue;
                    }

                    var stack = new Stack<string>();
                    stack.Push(start);
                    while (stack.Count > 0)
                    {
                        var directory = stack.Pop();
                        try
                        {
                            foreach (var child in Directory.EnumerateDirectories(directory))
                            {
                                if (IsReparsePoint(child))
                                {
                                    // 不跟随目录符号链接/联接，避免扫描到游戏目录之外
                                    continue;
                                }

                                stack.Push(child);
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failed++;
                        }

                        try
                        {
                            foreach (var file in Directory.EnumerateFiles(directory))
                            {
                                scanned++;
                                if (!file.EndsWith(DownloadClient.TempSuffix, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }

                                if (!IsInside(root, file))
                                {
                                    skipped++;
                                    continue;
                                }

                                if (IsReparsePoint(file))
                                {
                                    // 文件链接：解析最终目标，仍越界则不删除
                                    string? target = null;
                                    try
                                    {
                                        target = File.ResolveLinkTarget(file, true)?.FullName;
                                    }
                                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                    {
                                        failed++;
                                        continue;
                                    }

                                    if (target is null || !IsInside(root, target))
                                    {
                                        skipped++;
                                        continue;
                                    }
                                }

                                try
                                {
                                    File.Delete(file);
                                    deleted++;
                                }
                                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                                {
                                    failed++;
                                }
                            }
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                            failed++;
                        }
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 游戏目录不可用（路径非法/无权限）时静默放弃本次清理
            failed++;
        }

        logger.Info($"启动清理 .hycl-dl：扫描 {scanned} 个文件，删除 {deleted}，跳过 {skipped}，失败 {failed}");
        return new Result(scanned, deleted, skipped, failed);
    }

    private static bool IsInside(string root, string candidate)
    {
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        var normalizedCandidate = Path.TrimEndingDirectorySeparator(candidate);
        if (string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 无法读取属性时按普通路径处理，后续删除失败会再计入 failed
            return false;
        }
    }
}
