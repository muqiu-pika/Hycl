using System.Text.Json.Serialization;

namespace Hycl.Launcher.Core.Game.Loaders;

/// <summary>Fabric 系（Fabric / Quilt / LegacyFabric）loader 版本条目。</summary>
public sealed record FabricLoaderVersion
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }
}

/// <summary>Fabric 系 meta 的 /versions 响应（game/loader/installer 三数组）。</summary>
public sealed class FabricMetaVersions
{
    [JsonPropertyName("game")]
    public List<FabricGameEntry>? Game { get; set; }

    [JsonPropertyName("loader")]
    public List<FabricLoaderVersion>? Loader { get; set; }

    [JsonPropertyName("installer")]
    public List<FabricLoaderVersion>? Installer { get; set; }

    public bool IsValid => Game is { Count: > 0 } && Loader is { Count: > 0 } && Installer is { Count: > 0 };
}

public sealed class FabricGameEntry
{
    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("stable")]
    public bool Stable { get; set; }
}

/// <summary>OptiFine 版本条目（移植自 PCL-CE DlOptiFineListEntry）。</summary>
public sealed record OptiFineEntry
{
    /// <summary>显示名（已去除 HD_U 字样），如 1.12.2 C8。</summary>
    public string? DisplayName { get; init; }

    /// <summary>是否为测试版（pre）。</summary>
    public bool IsPreview { get; init; }

    /// <summary>原始文件名，如 preview_OptiFine_1.11_HD_U_E1_pre.jar。</summary>
    public string? NameFile { get; init; }

    /// <summary>版本名，如 1.13.2-OptiFine_HD_U_E6。</summary>
    public string? NameVersion { get; init; }

    public DateTimeOffset? ReleaseTime { get; init; }

    /// <summary>需要的最低 Forge 版本；null 为无限制。</summary>
    public string? RequiredForgeVersion { get; init; }

    /// <summary>对应的 Minecraft 版本（1.8.0 会规整为 1.8）。</summary>
    public string? Inherit { get; init; }
}

/// <summary>LiteLoader 版本条目（移植自 PCL-CE DlLiteLoaderListEntry）。</summary>
public sealed record LiteLoaderEntry
{
    /// <summary>安装器文件名，如 liteloader-installer-1.12-00-SNAPSHOT.jar。</summary>
    public string? FileName { get; init; }

    /// <summary>对应的 Minecraft 版本。</summary>
    public string? Inherit { get; init; }

    /// <summary>是否为 1.7 及更早的远古版。</summary>
    public bool IsLegacy { get; init; }

    /// <summary>是否为快照版。</summary>
    public bool IsPreview { get; init; }

    /// <summary>文件 MD5。</summary>
    public string? Md5 { get; init; }

    public DateTimeOffset? ReleaseTime { get; init; }

    /// <summary>versions.json 中该条目的原始 JSON（安装时构造 version json 需要 libraries 等）。</summary>
    public string? RawJson { get; init; }
}

/// <summary>LabyMod 渠道条目（production / snapshot）。</summary>
public sealed record LabyModEntry
{
    /// <summary>通道：production / snapshot。</summary>
    public required string Channel { get; init; }

    /// <summary>LabyMod 版本号。</summary>
    public string LabyModVersion { get; init; } = "";

    /// <summary>manifest 的 commitReference（用于拼每版本下载地址）。</summary>
    public string CommitRef { get; init; } = "";

    /// <summary>manifest 声明支持的 Minecraft 版本（tag 与 version 并集）。</summary>
    public IReadOnlySet<string> SupportedVersions { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool IsSnapshot => Channel == "snapshot";
}
