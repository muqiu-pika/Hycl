namespace Hycl.Launcher.PluginContract;

/// <summary>
/// Hycl-online 仓库 online.json 的反序列化模型（camelCase）。
/// 本类型属于契约层：启动器用 source-gen 序列化它，插件按需自行解析。
/// </summary>
public sealed class HyclOnlineManifest
{
    public int SchemaVersion { get; init; } = 1;

    public string UpdatedAt { get; init; } = "";

    public List<HyclPluginEntry> Plugins { get; init; } = [];

    public Dictionary<string, HyclToolInfo> Tools { get; init; } = new(StringComparer.Ordinal);
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

/// <summary>外部工具信息（如 terracotta），按运行平台挑选资产。</summary>
public sealed class HyclToolInfo
{
    public string Version { get; init; } = "";

    public string Upstream { get; init; } = "";

    public string License { get; init; } = "";

    public Dictionary<string, HyclToolAsset> Assets { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>单个平台资产：urls 按序尝试（本仓库 Release 直链在前，官方镜像兜底），双 SHA-256 校验。</summary>
public sealed class HyclToolAsset
{
    public List<string> Urls { get; init; } = [];

    public string FileName { get; init; } = "";

    public string ExeName { get; init; } = "";

    public string Sha256 { get; init; } = "";

    public string ExeSha256 { get; init; } = "";

    public long Size { get; init; }
}
