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

    /// <summary>
    /// 在文件管理器中打开并<b>定位到指定文件</b>（Windows 资源管理器 /select，macOS Finder reveal）。
    /// 平台不支持定位时降级为打开父目录。目录参数直接走 OpenFolder。
    /// </summary>
    public bool RevealFile(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                return OpenFolder(Path.GetDirectoryName(fullPath) ?? fullPath);
            }

            if (PlatformInfo.IsWindows)
            {
                // explorer /select, 必须带未加引号的原样路径：用 ArgumentList 拼 "…" 会失效
                Process.Start(new ProcessStartInfo("explorer.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    Arguments = $"/select,\"{fullPath}\""
                });
                return true;
            }

            if (PlatformInfo.IsMacOS)
            {
                // open -R <file>：Finder 中显示并选中该文件
                var reveal = new ProcessStartInfo("open")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                reveal.ArgumentList.Add("-R");
                reveal.ArgumentList.Add(fullPath);
                Process.Start(reveal);
                return true;
            }

            return OpenFolder(Path.GetDirectoryName(fullPath) ?? fullPath);
        }
        catch (Exception ex)
        {
            _logger.Error($"定位文件失败：{filePath}", ex);
            return OpenFolder(Path.GetDirectoryName(filePath) ?? filePath);
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

    /// <summary>
    /// 释放启动器内存（对应 PCL 正式版的「内存整理」；OpenPCL/CE 源码中未移植该功能）：
    /// 强制完整 GC 后，Windows 上再调用 SetProcessWorkingSetSize 把工作集归还系统。
    /// 返回释放后的工作集（MB）。
    /// </summary>
    public double ReleaseMemory()
    {
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);

        if (PlatformInfo.IsWindows)
        {
            TrySetWorkingSet();
        }

        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return self.WorkingSet64 / 1024d / 1024d;
    }

    /// <summary>当前进程工作集（MB）。</summary>
    public static double GetCurrentWorkingSetMb()
    {
        using var self = Process.GetCurrentProcess();
        self.Refresh();
        return self.WorkingSet64 / 1024d / 1024d;
    }

    [System.Runtime.InteropServices.DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint hProcess);

    private static void TrySetWorkingSet()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            _ = EmptyWorkingSet(self.Handle);
        }
        catch (Exception ex)
        {
            // EmptyWorkingSet 不可用时静默降级（仅 GC 已有效果）
            System.Diagnostics.Debug.WriteLine($"EmptyWorkingSet failed: {ex.Message}");
        }
    }
}
