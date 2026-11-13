namespace Hycl.Launcher.PluginContract;

/// <summary>
/// Hycl-online 仓库 online.json 的反序列化模型（camelCase）。
/// 本类型属于契约层：启动器用 source-gen 序列化它，插件按需自行解析。
/// </summary>
public sealed class HyclOnlineManifest
{
    public int SchemaVersion { get; init; } = 1;

    public string UpdatedAt { get; init; } = "";

    /// <summary>社区 EasyTier 公共节点清单地址（Hycl-online 托管；插件建房/加入前拉取）。</summary>
    public string PublicNodesUrl { get; init; } = "";

    public List<HyclPluginEntry> Plugins { get; init; } = [];
}

/// <summary>插件包条目：zip 内含 plugin.json 与插件程序集，由启动器下载解压加载。</summary>
public sealed class HyclPluginEntry
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Version { get; init; } = "";

    /// <summary>插件契约版本；与启动器 ABI 不一致时拒载。</summary>
    public int Abi { get; init; } = 1;

    /// <summary>最低启动器版本（空 = 不限制）。</summary>
    public string MinLauncherVersion { get; init; } = "";

    public string Url { get; init; } = "";

    /// <summary>备用下载地址（EdgeOne 等托管源为主 url 时填 GitHub Release 等，按序尝试；可为空）。</summary>
    public List<string> Mirrors { get; init; } = [];

    public string Sha256 { get; init; } = "";

    public long Size { get; init; }

    public string Description { get; init; } = "";
}
