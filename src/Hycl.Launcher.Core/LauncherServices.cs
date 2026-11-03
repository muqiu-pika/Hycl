using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Java;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core;

/// <summary>
/// 应用服务容器（组合根）。所有业务对象都停留在 Core，UI 只读取。
/// </summary>
public sealed class LauncherServices
{
    private LauncherServices(
        LauncherPaths paths,
        LauncherLogger logger,
        ConfigStore configStore,
        AccountStore accounts,
        JavaDetector javaDetector,
        DownloadClient downloadClient,
        VersionResolver versionResolver,
        VersionInstaller installer,
        Game.Loaders.LoaderProviderRegistry loaderProviders,
        Game.LoaderInstallService loaderInstall,
        Game.Comp.CompRequester compRequester,
        Game.Comp.CompInstallService compInstall,
        InstanceManager instances,
        LaunchService launch,
        SystemService system,
        IAppDispatcher dispatcher)
    {
        Paths = paths;
        Logger = logger;
        ConfigStore = configStore;
        Accounts = accounts;
        JavaDetector = javaDetector;
        DownloadClient = downloadClient;
        VersionResolver = versionResolver;
        Installer = installer;
        LoaderProviders = loaderProviders;
        LoaderInstall = loaderInstall;
        CompRequester = compRequester;
        CompInstall = compInstall;
        Instances = instances;
        Launch = launch;
        System = system;
        Dispatcher = dispatcher;
    }

    public LauncherPaths Paths { get; }

    public LauncherLogger Logger { get; }

    public ConfigStore ConfigStore { get; }

    public LauncherConfig Config => ConfigStore.Config;

    public AccountStore Accounts { get; }

    public JavaDetector JavaDetector { get; }

    public DownloadClient DownloadClient { get; }

    public VersionResolver VersionResolver { get; }

    public VersionInstaller Installer { get; }

    public Game.Loaders.LoaderProviderRegistry LoaderProviders { get; }

    public Game.LoaderInstallService LoaderInstall { get; }

    public Game.Comp.CompRequester CompRequester { get; }

    public Game.Comp.CompInstallService CompInstall { get; }

    public InstanceManager Instances { get; }

    public LaunchService Launch { get; }

    public SystemService System { get; }

    public IAppDispatcher Dispatcher { get; }

    public static async Task<LauncherServices> CreateAsync(
        IAppDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        var paths = LauncherPaths.CreateDefault();
        var logger = new LauncherLogger(paths.LogDirectory);
        logger.Info("Hyplay Craft Launcher 启动");

        var configStore = new ConfigStore(paths.ConfigFile, logger);
        await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);

        var accounts = new AccountStore(paths.AccountFile, logger);
        await accounts.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(accounts.SelectedAccount.Name))
        {
            configStore.Config.UserName = accounts.SelectedAccount.Name;
        }

        var javaDetector = new JavaDetector(logger);
        var downloadClient = new DownloadClient(logger, configStore.Config.RetryCount);
        var versionResolver = new VersionResolver(logger);
        var installer = new VersionInstaller(downloadClient, logger, paths.CacheDirectory, javaDetector);
        var loaderProviders = new Game.Loaders.LoaderProviderRegistry(downloadClient, logger, paths.CacheDirectory);
        var modrinth = new Game.Mods.ModrinthProvider(downloadClient, logger);
        var loaderInstall = new Game.LoaderInstallService(installer, loaderProviders, modrinth, logger);
        var instances = new InstanceManager(logger, versionResolver);
        var launch = new LaunchService(versionResolver, javaDetector, logger);
        var system = new SystemService(logger);

        // 社区资源（Modrinth / CurseForge）
        var compRequester = new Game.Comp.CompRequester(downloadClient, configStore.Config, logger);
        var compInstall = new Game.Comp.CompInstallService(
            compRequester,
            new Game.Comp.CurseForgeClient(compRequester),
            instances,
            loaderInstall,
            logger);

        return new LauncherServices(
            paths,
            logger,
            configStore,
            accounts,
            javaDetector,
            downloadClient,
            versionResolver,
            installer,
            loaderProviders,
            loaderInstall,
            compRequester,
            compInstall,
            instances,
            launch,
            system,
            dispatcher);
    }
}
