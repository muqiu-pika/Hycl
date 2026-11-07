using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 跨平台的可恢复删除（对应 PCL-CE 默认回收站删除）：
/// Windows 送系统回收站；macOS / Linux 无统一 shell 回收站接口，
/// 移入游戏根目录下 .hycl-trash/ 兜底，用户可自行恢复或清空。
/// </summary>
public static class RecycleBin
{
    /// <summary>把目录移入回收站（Windows）或 .hycl-trash 兜底目录；返回是否成功。</summary>
    public static bool DeleteDirectory(string directory, string gameDirectory, string displayName, LauncherLogger logger)
    {
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (PlatformInfo.IsWindows)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                    directory,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                logger.Info($"已删除到回收站：{displayName}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                logger.Error($"删除到回收站失败：{displayName}", ex);
                return false;
            }
        }

        try
        {
            var trashDirectory = Path.Combine(gameDirectory, ".hycl-trash");
            Directory.CreateDirectory(trashDirectory);
            var name = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var target = Path.Combine(trashDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}");
            Directory.Move(directory, target);
            logger.Info($"已移入 {RecycleBinFolderName}（可手动恢复）：{displayName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error($"移入兜底回收目录失败：{displayName}", ex);
            return false;
        }
    }

    /// <summary>把文件移入回收站（Windows）或 .hycl-trash 兜底目录；返回是否成功。</summary>
    public static bool DeleteFile(string file, string gameDirectory, string displayName, LauncherLogger logger)
    {
        if (!File.Exists(file))
        {
            return false;
        }

        if (PlatformInfo.IsWindows)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                    file,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                logger.Info($"已删除到回收站：{displayName}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            {
                logger.Error($"删除到回收站失败：{displayName}", ex);
                return false;
            }
        }

        try
        {
            var trashDirectory = Path.Combine(gameDirectory, RecycleBinFolderName);
            Directory.CreateDirectory(trashDirectory);
            var name = Path.GetFileName(file);
            var target = Path.Combine(trashDirectory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{name}");
            File.Move(file, target);
            logger.Info($"已移入 {RecycleBinFolderName}（可手动恢复）：{displayName}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.Error($"移入兜底回收目录失败：{displayName}", ex);
            return false;
        }
    }

    public const string RecycleBinFolderName = ".hycl-trash";
}
