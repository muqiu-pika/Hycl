using System.Diagnostics;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 打开 URL / 目录等系统级操作，按平台分支收敛。
/// </summary>
public sealed class SystemService
{
    private readonly LauncherLogger _logger;

    public SystemService(LauncherLogger logger)
    {
        _logger = logger;
    }

    public bool OpenUrl(string url)
    {
        try
        {
            if (PlatformInfo.IsWindows)
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else
            {
                var command = PlatformInfo.IsMacOS ? "open" : "xdg-open";
                StartDetached(command, url);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"打开链接失败：{url}", ex);
            return false;
        }
    }

    public bool OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }

            if (PlatformInfo.IsWindows)
            {
                StartDetached("explorer.exe", path);
            }
            else
            {
                StartDetached(PlatformInfo.IsMacOS ? "open" : "xdg-open", path);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"打开目录失败：{path}", ex);
            return false;
        }
    }

    private static void StartDetached(string fileName, string argument)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add(argument);
        Process.Start(info);
    }
}
