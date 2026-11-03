using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.Services;

/// <summary>
/// 跨平台路径解析：启动器数据、日志、缓存、默认游戏目录。
/// </summary>
public sealed class LauncherPaths
{
    public LauncherPaths(string executableDirectory, string? dataDirectory = null)
    {
        ExecutableDirectory = executableDirectory;
        DataDirectory = dataDirectory ?? PlatformInfo.LauncherDataDirectory;
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }

    public string ExecutableDirectory { get; }

    public string DataDirectory { get; }

    public string ConfigFile => Path.Combine(DataDirectory, "config.json");

    public string AccountFile => Path.Combine(DataDirectory, "accounts.json");

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public static LauncherPaths CreateDefault()
    {
        var executableDirectory = AppContext.BaseDirectory;
        return new LauncherPaths(executableDirectory);
    }
}
