using Hycl.Launcher.Core.Download;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>加载器种类（对应 PCL-CE 下载页的安装包分区）。</summary>
public enum LoaderKind
{
    Forge,
    NeoForge,
    Cleanroom,
    Fabric,
    Quilt,
    LegacyFabric,
    OptiFine,
    LiteLoader,
    LabyMod
}

/// <summary>加载器的可选版本条目：列表展示与安装所需的最小信息。</summary>
public sealed record LoaderVersionEntry
{
    /// <summary>加载器种类。</summary>
    public required LoaderKind Kind { get; init; }

    /// <summary>对应的 Minecraft 版本（如 1.20.1）。</summary>
    public required string Inherit { get; init; }

    /// <summary>展示用的版本名（如 50.1.9、47.1.99、0.2.4-alpha）。</summary>
    public required string VersionName { get; init; }

    /// <summary>是否为稳定版（false 时 UI 显示“测试版”）。</summary>
    public bool IsStable { get; init; } = true;

    /// <summary>是否为该 MC 版本的推荐版（Forge promos）。</summary>
    public bool IsRecommended { get; init; }

    public DateTimeOffset? ReleaseTime { get; init; }

    /// <summary>Provider 自带的安装载荷（ForgelikeEntry / OptiFineEntry 等）。</summary>
    public object? Payload { get; init; }
}

/// <summary>加载器版本列表的统一获取入口。</summary>
public interface ILoaderVersionProvider
{
    LoaderKind Kind { get; }

    /// <summary>该加载器在指定 Minecraft 版本下的可选版本（从新到旧）。</summary>
    Task<IReadOnlyList<LoaderVersionEntry>> GetVersionsForAsync(
        string minecraftVersion,
        DownloadPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>该加载器支持的 Minecraft 版本列表（从新到旧）。</summary>
    Task<IReadOnlyList<string>> GetSupportedMcVersionsAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default);
}
