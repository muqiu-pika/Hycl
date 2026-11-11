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

    /// <summary>启动器配置与日志目录（Windows 上优先 D 盘，避免系统重装丢数据；详见 <see cref="ResolveDataDirectory"/>）。</summary>
    public static string LauncherDataDirectory => _resolvedDataDirectory ??= ResolveDataDirectory();

    private static string? _resolvedDataDirectory;

    /// <summary>
    /// 数据目录解析规则（Windows）：
    /// 1) 环境变量 <c>HYCL_DATA_DIR</c> 显式覆盖（测试/高级用户）；
    /// 2) 存在 D 盘 → 使用 <c>D:\HYCL</c>，旧数据仍在 %APPDATA%\HYCL 时一次性整体迁移过去；
    /// 3) 没有 D 盘 → 回退 %APPDATA%\HYCL（与历史行为一致）。
    /// macOS / Linux 维持平台惯例目录。
    /// </summary>
    private static string ResolveDataDirectory()
    {
        var overrideDir = Environment.GetEnvironmentVariable("HYCL_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideDir));
        }

        if (!IsWindows)
        {
            return Path.Combine(GetConfigRoot(), "HYCL");
        }

        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HYCL");
        try
        {
            if (!Directory.Exists("D:\\"))
            {
                return legacy;
            }

            const string preferred = "D:\\HYCL";
            if (Directory.Exists(preferred))
            {
                return preferred;
            }

            if (Directory.Exists(legacy))
            {
                // 一次性迁移：旧 %APPDATA% 数据整体搬到 D 盘（跨卷需要逐项搬运）
                MoveDirectoryCrossVolume(legacy, preferred);
            }

            return preferred;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // D 盘只读 / 迁移失败：退回原位置，绝不因数据目录问题拦住启动
            return legacy;
        }
    }

    /// <summary>跨卷目录搬迁：逐文件移动（File.Move 支持跨卷），成功后删除源目录。</summary>
    private static void MoveDirectoryCrossVolume(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destination);
            File.Move(file, target, overwrite: false);
        }

        Directory.Delete(source, true);
    }

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
