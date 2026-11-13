using System.Diagnostics;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 启动器重启：不能在进程内先启动新实例再退出——单实例 Mutex 要等本进程退出才释放，
/// 新实例会撞互斥体。改为派生一个分离的「等待 PID 退出 → 拉起新实例」辅助进程。
/// </summary>
public static class LauncherRestart
{
    /// <summary>派生等待 <paramref name="pid"/> 退出后启动 <paramref name="exePath"/> 的分离进程；失败返回 false（调用方可降级为只提示）。</summary>
    public static bool SpawnRelauncher(int pid, string exePath)
    {
        if (string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        try
        {
            // 等待上限 15s：正常退出在 ShutdownAll（插件 3s grace）之后，远小于该值
            ProcessStartInfo psi;
            if (OperatingSystem.IsWindows())
            {
                psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments =
                        $"-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id {pid} -Timeout 15 -ErrorAction SilentlyContinue; "
                        + "Start-Process -FilePath '" + exePath.Replace("'", "''") + "'\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments =
                        $"-c \"while kill -0 {pid} 2>/dev/null; do sleep 0.3; done; exec '{exePath.Replace("'", "'\\''")}' &\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
            }

            return Process.Start(psi) is not null;
        }
        catch
        {
            return false;
        }
    }
}
