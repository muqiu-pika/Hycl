using System.Runtime.InteropServices;

namespace Hycl.Launcher.Core.Platform;

/// <summary>
/// 运行时平台信息。所有平台差异集中在此，UI 层不再出现平台判断。
/// </summary>
public static class PlatformInfo
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    public static bool IsMacOS => OperatingSystem.IsMacOS();

    public static bool IsLinux => OperatingSystem.IsLinux();

    public static string OperatingSystemName
    {
        get
        {
            if (IsWindows)
            {
                return "Windows";
            }

            if (IsMacOS)
            {
                return "macOS";
            }

            if (IsLinux)
            {
                return "Linux";
            }

            return RuntimeInformation.OSDescription;
        }
    }

    public static string ArchitectureName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "arm64",
        Architecture.Arm => "arm",
        var other => other.ToString().ToLowerInvariant()
    };

    /// <summary>Java 可执行文件名（Windows 为 java.exe）。</summary>
    public static string JavaExecutableName => IsWindows ? "java.exe" : "java";

    /// <summary>类路径分隔符。</summary>
    public static string ClassPathSeparator => IsWindows ? ";" : ":";

    /// <summary>启动器配置与日志目录。</summary>
    public static string LauncherDataDirectory => Path.Combine(GetConfigRoot(), "HYCL");

    /// <summary>默认 Minecraft 游戏目录（与原版启动器一致）。</summary>
    public static string DefaultMinecraftDirectory
    {
        get
        {
            if (IsWindows)
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, ".minecraft");
            }

            if (IsMacOS)
            {
                return Path.Combine(GetUserHome(), "Library", "Application Support", "minecraft");
            }

            return Path.Combine(GetUserHome(), ".minecraft");
        }
    }

    public static string GetUserHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) ? Environment.GetFolderPath(Environment.SpecialFolder.Personal) : home;
    }

    private static string GetConfigRoot()
    {
        if (IsWindows)
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }

        if (IsMacOS)
        {
            return Path.Combine(GetUserHome(), "Library", "Application Support");
        }

        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return string.IsNullOrWhiteSpace(xdg) ? Path.Combine(GetUserHome(), ".config") : xdg;
    }

    /// <summary>供 UI 判断是否需要 mica/透明窗口等能力时使用。</summary>
    public static bool SupportsWindowTransparency => IsWindows || IsMacOS;
}
