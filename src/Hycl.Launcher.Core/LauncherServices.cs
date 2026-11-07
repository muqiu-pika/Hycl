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
        Game.Comp.CompDependencyResolver compDependencies,
        InstanceManager instances,
        LaunchService launch,
        SystemService system,
        Auth.MicrosoftAuthService microsoftAuth,
        Auth.AuthlibAuthService authlibAuth,
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
        CompDependencies = compDependencies;
        Instances = instances;
        Launch = launch;
        System = system;
        MicrosoftAuth = microsoftAuth;
        AuthlibAuth = authlibAuth;
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

    public Game.Comp.CompDependencyResolver CompDependencies { get; }

    public InstanceManager Instances { get; }

    public LaunchService Launch { get; }

    public SystemService System { get; }

    public Auth.MicrosoftAuthService MicrosoftAuth { get; }

    public Auth.AuthlibAuthService AuthlibAuth { get; }

    public IAppDispatcher Dispatcher { get; }

    public static async Task<LauncherServices> CreateAsync(
        IAppDispatcher dispatcher,
        CancellationToken cancellationToken = default)
    {
        var paths = LauncherPaths.CreateDefault();
        var logger = new LauncherLogger(paths.LogDirectory);
        logger.Info("Hyplay Craft Launcher 启动");
        PerfTrace.Mark("core_create_begin");

        var configStore = new ConfigStore(paths.ConfigFile, logger);
        await configStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        PerfTrace.Mark("config_loaded");

        // 内置 CurseForge 密钥解密自检（失败只降级镜像，不影响启动）
        HyclSecrets.Initialize(configStore.Config, logger);

        var accounts = new AccountStore(paths.AccountFile, logger);
        await accounts.LoadAsync(cancellationToken).ConfigureAwait(false);
        PerfTrace.Mark("accounts_loaded");
        if (accounts.SelectedAccount is { } currentAccount
            && !string.IsNullOrWhiteSpace(currentAccount.Name))
        {
            configStore.Config.UserName = currentAccount.Name;
        }

        // 临时文件清理涉及上万文件的目录遍历（实测 0.5s 以上），放到后台执行，不阻塞首帧；
        // 清理只针对较旧的 .hycl-dl，避免误删刚创建的在途下载临时文件。
        var gameDirectory = configStore.Config.EffectiveGameDirectory;
        _ = Task.Run(() =>
        {
            StaleTempCleaner.Run(gameDirectory, logger);
            PerfTrace.Mark("stale_clean_done");
        });

        var javaDetector = new JavaDetector(logger);
        var downloadClient = new DownloadClient(logger, configStore.Config.RetryCount);
        var versionResolver = new VersionResolver(logger);
        var installer = new VersionInstaller(downloadClient, logger, paths.CacheDirectory, javaDetector)
        {
            FixAuthlibEnabled = configStore.Config.FixAuthlib
        };
        var loaderProviders = new Game.Loaders.LoaderProviderRegistry(downloadClient, logger, paths.CacheDirectory);
        var modrinth = new Game.Mods.ModrinthProvider(downloadClient, logger);
        var loaderInstall = new Game.LoaderInstallService(installer, loaderProviders, modrinth, logger);
        var instances = new InstanceManager(logger, versionResolver);
        var launch = new LaunchService(versionResolver, javaDetector, logger, installer);
        var system = new SystemService(logger);
        var microsoftAuth = new Auth.MicrosoftAuthService(logger);
        var authlibAuth = new Auth.AuthlibAuthService(logger);

        // 社区资源（Modrinth / CurseForge）
        var compRequester = new Game.Comp.CompRequester(downloadClient, configStore.Config, logger);
        var curseForge = new Game.Comp.CurseForgeClient(compRequester);
        var compInstall = new Game.Comp.CompInstallService(
            compRequester,
            curseForge,
            instances,
            loaderInstall,
            logger);
        var compDependencies = new Game.Comp.CompDependencyResolver(
            curseForge,
            new Game.Comp.ModrinthClient(compRequester),
            logger);

        PerfTrace.Mark("core_create_done");
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
            compDependencies,
            instances,
            launch,
            system,
            microsoftAuth,
            authlibAuth,
            dispatcher);
    }
}
