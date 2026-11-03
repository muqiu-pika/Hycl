using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Config;

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

    /// <summary>版本列表源：0 镜像优先 / 1 官方优先（缓慢时自动切换）/ 2 仅官方（对应 PCL-CE ToolDownloadVersion）。</summary>
    public int VersionListSource { get; set; } = (int)Download.DownloadSourceMode.OfficialAuto;

    /// <summary>下载文件源：0 镜像优先 / 1 官方优先（缓慢时自动切换）/ 2 仅官方（对应 PCL-CE ToolDownloadSource）。</summary>
    public int FileSource { get; set; } = (int)Download.DownloadSourceMode.OfficialAuto;

    /// <summary>旧版配置的单一下载源字段（"Official"/"Bmclapi"），仅用于读取迁移，不再写出。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? DownloadSource { get; set; }

    public int DownloadThreads { get; set; } = 16;

    public int RetryCount { get; set; } = 3;

    /// <summary>社区资源源策略：0 镜像优先 / 1 官方优先（失败转镜像）/ 2 仅官方（对应 PCL-CE ToolDownloadMod）。</summary>
    public int CommunitySourceSolution { get; set; } = 1;

    /// <summary>下载模组时自动解析并安装前置（对应 PCL-CE ToolDownloadAutoInstallDependencies）。</summary>
    public bool CompAutoInstallDependencies { get; set; } = true;

    /// <summary>修复 1.16.5 的 authlib 2.1.28 兼容问题（对应 PCL-CE ToolFixAuthlib，默认开启）。</summary>
    public bool FixAuthlib { get; set; } = true;

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
        VersionListSource = Math.Clamp(VersionListSource, 0, 2);
        FileSource = Math.Clamp(FileSource, 0, 2);
        // 迁移旧版单一下载源字段：仅在用户尚未改动新字段时生效
        if (VersionListSource == (int)Download.DownloadSourceMode.OfficialAuto
            && FileSource == (int)Download.DownloadSourceMode.OfficialAuto)
        {
            if (DownloadSource == "Official")
            {
                VersionListSource = (int)Download.DownloadSourceMode.OfficialOnly;
                FileSource = (int)Download.DownloadSourceMode.OfficialOnly;
            }
            else if (DownloadSource == "Bmclapi")
            {
                VersionListSource = (int)Download.DownloadSourceMode.MirrorFirst;
                FileSource = (int)Download.DownloadSourceMode.MirrorFirst;
            }
        }

        DownloadSource = null;
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
