namespace Hycl.Launcher.PluginContract;

/// <summary>插件日志（由启动器实现，统一落盘到 launcher.log）。</summary>
public interface IPluginLog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message, Exception? exception = null);
}

/// <summary>
/// 下载能力（由启动器实现，复用 Hycl 的多镜像回退 / 断点续传 / 哈希校验管线）。
/// 哈希按长度自动选择算法：64 位 = SHA-256。
/// </summary>
public interface IPluginDownloads
{
    /// <param name="progress">0~1 的整体进度（可为 null）。</param>
    /// <returns>是否真正发生了下载（目标已存在且校验通过时返回 false）。</returns>
    Task<bool> DownloadFileAsync(
        IReadOnlyList<string> urls,
        string destinationPath,
        long? expectedSize,
        string? expectedSha256,
        IProgress<double>? progress,
        CancellationToken cancellationToken);

    /// <summary>按序尝试多个 URL 获取文本（带整体超时与重试）。</summary>
    Task<string> GetStringAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);
}

/// <summary>插件运行环境（由启动器实现；每次启动构建一份，随插件生命周期存续）。</summary>
public interface IPluginContext
{
    /// <summary>当前玩家名（档案名；可能为空字符串）。</summary>
    string PlayerName { get; }

    /// <summary>启动器版本。</summary>
    string LauncherVersion { get; }

    /// <summary>插件专属数据目录（plugins-data/&lt;id&gt;/，已创建；工具二进制等持久文件放这里）。</summary>
    string DataDirectory { get; }

    IPluginLog Log { get; }

    IPluginDownloads Downloads { get; }

    /// <summary>拉取 Hycl-online 清单（启动器侧带镜像回退与缓存；不可达时返回 null）。</summary>
    Task<HyclOnlineManifest?> GetManifestAsync(CancellationToken cancellationToken = default);

    /// <summary>用系统默认浏览器打开链接（链接合法性由启动器校验）。</summary>
    void OpenUrl(string url);

    /// <summary>调度到启动器 UI 线程执行（更新绑定属性前使用）。</summary>
    void Dispatch(Action action);
}
