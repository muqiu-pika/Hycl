using System.Text.Json;
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

    /// <summary>
    /// 启动极早期只读取「启动动画」开关：正式配置加载已挪到首帧之后，
    /// 但首帧需要立刻决定是否显示启动层，这里用流式 JSON 只取一个字段，避免为此提前创建整套服务。
    /// 文件缺失/损坏时按默认值（开启动画）处理。
    /// </summary>
    public static bool ReadEnableAnimationsFlag()
    {
        try
        {
            var path = Path.Combine(PlatformInfo.LauncherDataDirectory, "config.json");
            if (!File.Exists(path))
            {
                return true;
            }

            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "enableAnimations", StringComparison.OrdinalIgnoreCase))
                {
                    return property.Value.ValueKind != JsonValueKind.False;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 读不到配置时不影响启动，保持旧行为（播放启动动画）
        }

        return true;
    }
}
