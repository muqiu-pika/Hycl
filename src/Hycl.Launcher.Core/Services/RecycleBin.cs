using System.Runtime.InteropServices;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 跨平台的可恢复删除（对应 PCL-CE 默认回收站删除）：
/// Windows 用 SHFileOperation(FOF_ALLOWUNDO) 送系统回收站（此前走 Microsoft.VisualBasic.FileIO，
/// 为此要打包 1.19MB 的 Microsoft.VisualBasic.Core，改 P/Invoke 后该程序集可整体剔除）；
/// macOS / Linux 无统一 shell 回收站接口，移入游戏根目录下 .hycl-trash/ 兜底，用户可自行恢复或清空。
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
                if (SendToRecycleBin(directory))
                {
                    logger.Info($"已删除到回收站：{displayName}");
                    return true;
                }

                logger.Error($"删除到回收站失败：{displayName}");
                return false;
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
                if (SendToRecycleBin(file))
                {
                    logger.Info($"已删除到回收站：{displayName}");
                    return true;
                }

                logger.Error($"删除到回收站失败：{displayName}");
                return false;
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

    // FOF_ALLOWUNDO：走回收站可恢复；FOF_NOCONFIRMATION | FOF_SILENT：无确认弹窗；
    // FOF_NOERRORUI：错误改为返回值 false，由调用方记日志。
    private const int FO_DELETE = 3;
    private const ushort FOF_ALLOWUNDO = 0x40;
    private const ushort FOF_NOCONFIRMATION = 0x10;
    private const ushort FOF_SILENT = 0x4;
    private const ushort FOF_NOERRORUI = 0x400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string pTo;
        public ushort fFlags;
        public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT lpFileOp);

    /// <summary>把单个路径送入系统回收站；SHFileOperation 返回 0 且未被用户中止才算成功。</summary>
    private static bool SendToRecycleBin(string path)
    {
        // pFrom 必须以双空字符结尾（String 编组不会自动补第二个 \0）
        var op = new SHFILEOPSTRUCT
        {
            hwnd = IntPtr.Zero,
            wFunc = FO_DELETE,
            pFrom = path + '\0',
            pTo = "\0",
            fFlags = (ushort)(FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI),
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }
}
