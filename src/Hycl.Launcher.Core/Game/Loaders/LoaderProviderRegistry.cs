using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>
/// 加载器 Provider 注册表：统一持有各加载器列表源与元数据缓存。
/// </summary>
public sealed class LoaderProviderRegistry
{
    public LoaderProviderRegistry(DownloadClient client, LauncherLogger logger, string? cacheDirectory = null)
    {
        var cache = new LoaderMetadataCache();
        Cache = cache;
        Forge = new ForgeProvider(client, logger, cache, cacheDirectory);
        NeoForge = new NeoForgeProvider(client, logger, cache);
        Cleanroom = new CleanroomProvider(client, cache);
        Fabric = new FabricLikeProvider(
            LoaderKind.Fabric,
            "https://meta.fabricmc.net/v2",
            "https://bmclapi2.bangbang93.com/fabric-meta/v2",
            "fabric",
            client,
            logger,
            cache);
        // Quilt 不走 BMCLAPI：其镜像的 game 数组长期过期，会误判"不支持"新版本
        Quilt = new FabricLikeProvider(
            LoaderKind.Quilt,
            "https://meta.quiltmc.org/v3",
            null,
            "quilt",
            client,
            logger,
            cache);
        LegacyFabric = new FabricLikeProvider(
            LoaderKind.LegacyFabric,
            "https://meta.legacyfabric.net/v2",
            null,
            "legacyfabric",
            client,
            logger,
            cache);
        OptiFine = new OptiFineProvider(client, logger, cache);
        LiteLoader = new LiteLoaderProvider(client, logger, cache);
        LabyMod = new LabyModProvider(client, logger, cache);
    }

    public LoaderMetadataCache Cache { get; }

    public ForgeProvider Forge { get; }

    public NeoForgeProvider NeoForge { get; }

    public CleanroomProvider Cleanroom { get; }

    public FabricLikeProvider Fabric { get; }

    public FabricLikeProvider Quilt { get; }

    public FabricLikeProvider LegacyFabric { get; }

    public OptiFineProvider OptiFine { get; }

    public LiteLoaderProvider LiteLoader { get; }

    public LabyModProvider LabyMod { get; }

    public ILoaderVersionProvider Get(LoaderKind kind) => kind switch
    {
        LoaderKind.Forge => Forge,
        LoaderKind.NeoForge => NeoForge,
        LoaderKind.Cleanroom => Cleanroom,
        LoaderKind.Fabric => Fabric,
        LoaderKind.Quilt => Quilt,
        LoaderKind.LegacyFabric => LegacyFabric,
        LoaderKind.OptiFine => OptiFine,
        LoaderKind.LiteLoader => LiteLoader,
        LoaderKind.LabyMod => LabyMod,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
