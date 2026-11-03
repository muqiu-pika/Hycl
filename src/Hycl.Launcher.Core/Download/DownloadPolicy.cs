namespace Hycl.Launcher.Core.Download;

/// <summary>下载源三档策略（对应 PCL-CE 的下载源设置）。</summary>
public enum DownloadSourceMode
{
    /// <summary>尽量使用镜像源（BMCLAPI）。</summary>
    MirrorFirst = 0,

    /// <summary>优先使用官方源，在加载缓慢时换用镜像源（自动测速）。</summary>
    OfficialAuto = 1,

    /// <summary>尽量使用官方源。</summary>
    OfficialOnly = 2
}

/// <summary>
/// 一次下载流程的源决策输入：版本列表源与文件源独立成档（对应 PCL-CE 的
/// Config.Download.VersionListSource / FileSource），官方延迟探测结果影响排序。
/// </summary>
public sealed record DownloadPolicy(int VersionListSource, int FileSource, bool MojangFast)
{
    public static readonly DownloadPolicy Default =
        new((int)DownloadSourceMode.OfficialAuto, (int)DownloadSourceMode.OfficialAuto, false);

    /// <summary>下载文件（而非获取版本列表）时是否优先官方源。</summary>
    public bool PreferMojangForFiles =>
        FileSource == (int)DownloadSourceMode.OfficialOnly
        || (FileSource == (int)DownloadSourceMode.OfficialAuto && MojangFast);

    /// <summary>获取版本列表（而非下载文件）时是否优先官方源。</summary>
    public bool PreferMojangForVersionList =>
        VersionListSource == (int)DownloadSourceMode.OfficialOnly
        || (VersionListSource == (int)DownloadSourceMode.OfficialAuto && MojangFast);

    public static DownloadPolicy FromConfig(Config.LauncherConfig config, bool mojangFast = false) =>
        new(config.VersionListSource, config.FileSource, mojangFast || DownloadSourceMonitor.MojangFast);
}
