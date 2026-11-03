using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Config;

/// <summary>下载源。</summary>
public enum DownloadSourceKind
{
    /// <summary>官方（Mojang）。</summary>
    Official = 0,

    /// <summary>BMCLAPI 国内镜像。</summary>
    Bmclapi = 1
}

/// <summary>
/// 启动器全局配置（对应 PCL-CE 的 PCL.ini 中启动相关项）。
/// </summary>
public sealed class LauncherConfig
{
    public string UserName { get; set; } = "Player";

    public string SelectedVersion { get; set; } = "";

    /// <summary>空字符串表示使用平台默认的 .minecraft 目录。</summary>
    public string GameDirectory { get; set; } = "";

    /// <summary>空字符串表示自动匹配 Java。</summary>
    public string JavaPath { get; set; } = "";

    /// <summary>0 表示自动（物理内存的一半，限制在 512–8192 MB）。</summary>
    public int MemoryMegabytes { get; set; } = 0;

    /// <summary>0 默认；1 全屏；2 与启动器相同；3 自定义；4 最大化。</summary>
    public int WindowType { get; set; }

    public int WindowWidth { get; set; } = 854;

    public int WindowHeight { get; set; } = 480;

    public string ExtraJvmArguments { get; set; } = "";

    public string ExtraGameArguments { get; set; } = "";

    public DownloadSourceKind DownloadSource { get; set; } = DownloadSourceKind.Bmclapi;

    public int DownloadThreads { get; set; } = 16;

    public int RetryCount { get; set; } = 3;

    public bool CloseLauncherAfterLaunch { get; set; }

    public bool ShowGameOutput { get; set; } = true;

    public bool EnableAnimations { get; set; } = true;

    /// <summary>light / dark。</summary>
    public string Theme { get; set; } = "light";

    [JsonIgnore]
    public string EffectiveGameDirectory =>
        string.IsNullOrWhiteSpace(GameDirectory)
            ? Core.Platform.PlatformInfo.DefaultMinecraftDirectory
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(GameDirectory));

    public static LauncherConfig CreateDefault()
    {
        var config = new LauncherConfig();
        config.Normalize();
        return config;
    }

    /// <summary>把用户输入/旧配置文件里的非法值收敛到可用范围。</summary>
    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(UserName))
        {
            UserName = "Player";
        }

        MemoryMegabytes = Math.Clamp(MemoryMegabytes, 0, 65536);
        WindowType = Math.Clamp(WindowType, 0, 4);
        WindowWidth = Math.Clamp(WindowWidth, 320, 7680);
        WindowHeight = Math.Clamp(WindowHeight, 240, 4320);
        DownloadThreads = Math.Clamp(DownloadThreads, 1, 128);
        RetryCount = Math.Clamp(RetryCount, 0, 10);
        DownloadSource = DownloadSource == DownloadSourceKind.Official
            ? DownloadSourceKind.Official
            : DownloadSourceKind.Bmclapi;
        if (Theme is not ("light" or "dark"))
        {
            Theme = "light";
        }
    }

    /// <summary>实际分配给游戏的堆内存（MB）。</summary>
    public int GetEffectiveMemoryMegabytes()
    {
        if (MemoryMegabytes > 0)
        {
            return MemoryMegabytes;
        }

        var totalMb = (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024L * 1024L));
        if (totalMb <= 0)
        {
            return 2048;
        }

        var half = totalMb / 2;
        var rounded = half / 256 * 256;
        return Math.Clamp(rounded, 512, 8192);
    }
}
