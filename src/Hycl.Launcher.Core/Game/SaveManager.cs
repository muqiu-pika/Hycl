using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>本地存档条目（对应 PCL-CE PageInstanceSavesViewModel BuildEntry）。</summary>
public sealed record LocalSaveInfo(string Directory, string Name, string Info, DateTime Created, DateTime Modified);

/// <summary>
/// 本地存档管理：扫描 saves 目录、复制存档、删除到回收站、打开目录。
/// 目录本身由实例设置（版本隔离 / 独立存档目录）决定，这里不猜路径。
/// </summary>
public static class SaveManager
{
    public static IReadOnlyList<LocalSaveInfo> Scan(string root, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var results = new List<LocalSaveInfo>();
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var created = Directory.GetCreationTime(directory);
                var modified = Directory.GetLastWriteTime(directory);
                var name = Path.GetFileName(directory);
                results.Add(new LocalSaveInfo(
                    directory,
                    name,
                    $"创建 {created:yyyy-MM-dd} · 修改 {modified:yyyy-MM-dd}",
                    created,
                    modified));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个损坏/无权限的目录不影响其余存档
            }
        }

        return results
            .OrderByDescending(item => item.Modified)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>在同目录复制一份存档，名称为「原名 - 副本」（重名时追加序号）。</summary>
    public static (bool Success, string Message) Duplicate(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            return (false, "存档目录不存在");
        }

        try
        {
            var parent = Path.GetDirectoryName(sourceDirectory);
            if (parent is null)
            {
                return (false, "无法确定存档所在目录");
            }

            var baseName = Path.GetFileName(sourceDirectory);
            var targetName = $"{baseName} - 副本";
            var target = Path.Combine(parent, targetName);
            var index = 2;
            while (Directory.Exists(target))
            {
                targetName = $"{baseName} - 副本 ({index})";
                target = Path.Combine(parent, targetName);
                index++;
            }

            CopyDirectory(sourceDirectory, target);
            return (true, $"已复制存档：{targetName}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, $"复制存档失败：{ex.Message}");
        }
    }

    /// <summary>删除到回收站（与 PCL-CE 一致，可从回收站恢复）。</summary>
    public static bool DeleteToRecycleBin(string directory, LauncherLogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        try
        {
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                directory,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            logger.Error($"删除存档失败：{directory}", ex);
            return false;
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            File.Copy(file, destination, true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));
            CopyDirectory(directory, destination);
        }
    }
}
