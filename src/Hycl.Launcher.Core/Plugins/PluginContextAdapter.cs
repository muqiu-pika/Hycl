using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Core.Plugins;

/// <summary>把启动器服务适配成插件可见的窄接口；每个插件一份（数据目录与日志前缀隔离）。</summary>
internal sealed class PluginContextAdapter : IPluginContext
{
    private readonly PluginLogAdapter _log;
    private readonly PluginDownloadsAdapter _downloads;
    private readonly LauncherPaths _paths;
    private readonly AccountStore _accounts;
    private readonly LauncherConfig _config;
    private readonly SystemService _system;
    private readonly IAppDispatcher _dispatcher;
    private readonly PluginManifestClient _manifestClient;

    public PluginContextAdapter(
        string pluginId,
        DownloadClient downloadClient,
        LauncherLogger logger,
        LauncherPaths paths,
        AccountStore accounts,
        LauncherConfig config,
        SystemService system,
        IAppDispatcher dispatcher,
        PluginManifestClient manifestClient)
    {
        PluginId = pluginId;
        _log = new PluginLogAdapter(logger, pluginId);
        _downloads = new PluginDownloadsAdapter(downloadClient);
        _paths = paths;
        _accounts = accounts;
        _config = config;
        _system = system;
        _dispatcher = dispatcher;
        _manifestClient = manifestClient;
        Directory.CreateDirectory(DataDirectory);
    }

    public string PluginId { get; }

    public string PlayerName => _accounts.SelectedAccount?.Name ?? _config.UserName ?? "";

    public string LauncherVersion => LaunchService.LauncherVersion;

    public string DataDirectory => Path.Combine(_paths.PluginsDataDirectory, PluginId);

    IPluginLog IPluginContext.Log => _log;

    IPluginDownloads IPluginContext.Downloads => _downloads;

    public async Task<HyclOnlineManifest?> GetManifestAsync(CancellationToken cancellationToken = default)
        => await _manifestClient.RefreshAsync(cancellationToken).ConfigureAwait(false);

    public void OpenUrl(string url) => _system.OpenUrl(url);

    public void Dispatch(Action action) => _dispatcher.Post(action);

    private sealed class PluginLogAdapter(LauncherLogger logger, string pluginId) : IPluginLog
    {
        private readonly string _prefix = $"[插件:{pluginId}]";

        public void Info(string message) => logger.Info(_prefix + " " + message);

        public void Warn(string message) => logger.Warn(_prefix + " " + message);

        public void Error(string message, Exception? exception = null) => logger.Error(_prefix + " " + message, exception);
    }

    private sealed class PluginDownloadsAdapter(DownloadClient downloadClient) : IPluginDownloads
    {
        public Task<bool> DownloadFileAsync(
            IReadOnlyList<string> urls,
            string destinationPath,
            long? expectedSize,
            string? expectedSha256,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            IProgress<DownloadByteProgress>? inner = progress is null
                ? null
                : new Progress<DownloadByteProgress>(p =>
                {
                    var value = p.Percentage;
                    progress.Report(double.IsNaN(value) ? 0 : value);
                });
            return downloadClient.DownloadFileAsync(
                GitHubSources.Expand(urls), destinationPath, expectedSize, expectedSha256, inner, cancellationToken);
        }

        public Task<string> GetStringAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
            => downloadClient.GetStringAsync(GitHubSources.Expand(urls), cancellationToken);
    }
}
