using System.Text.Json.Serialization;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Game.Mods;

namespace Hycl.Launcher.Core.Serialization;

/// <summary>
/// 本地持久化 JSON（config.json 等）的 source-gen 元数据：Web 默认 camelCase + 缩进，
/// 与历史 ConfigStore 的 <c>JsonSerializerDefaults.Web + WriteIndented</c> 输出逐字节一致，
/// 磁盘上的旧配置无需迁移。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(LauncherConfig))]
internal sealed partial class HyclJsonContext : JsonSerializerContext;

/// <summary>
/// 网络接口与版本清单 JSON 的 source-gen 元数据（Web camelCase、紧凑）。
/// 私有嵌套模型（CurseForge/Modrinth 客户端、账号、收藏等）在各所在类内声明自己的嵌套 context，
/// 这里只收公共模型与跨类复用的类型。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(List<FabricLoaderEntry>))]
[JsonSerializable(typeof(AssetIndex))]
[JsonSerializable(typeof(FabricMetaVersions))]
[JsonSerializable(typeof(List<ModrinthVersion>))]
// ArgumentEntryConverter 运行时经 options.GetTypeInfo 取用
[JsonSerializable(typeof(RuleInfo[]))]
// 整合包导出清单（modrinth.index.json / manifest.json / mmc-pack.json）
[JsonSerializable(typeof(ModrinthIndexRoot))]
[JsonSerializable(typeof(CurseForgeManifestRoot))]
[JsonSerializable(typeof(MmcPackRoot))]
internal sealed partial class NetJsonContext : JsonSerializerContext;

/// <summary>modrinth.index.json 根结构：字段名与 Modrinth 规范严格一致（camelCase 命名策略即满足）。</summary>
internal sealed class ModrinthIndexRoot
{
    public int FormatVersion { get; init; }

    public string Game { get; init; } = "minecraft";

    public string VersionId { get; init; } = "";

    public string Name { get; init; } = "";

    public string Summary { get; init; } = "";

    public List<ModrinthIndexFile> Files { get; init; } = [];

    public Dictionary<string, string> Dependencies { get; init; } = new();
}

/// <summary>modrinth.index.json 的单文件条目。</summary>
internal sealed class ModrinthIndexFile
{
    public required string Path { get; init; }

    public Dictionary<string, string> Hashes { get; init; } = new();

    public string[] Downloads { get; init; } = [];

    public long FileSize { get; init; }
}

/// <summary>CurseForge manifest.json 根结构：projectID / fileID 等大小写按官方规范。</summary>
internal sealed class CurseForgeManifestRoot
{
    public string ManifestType { get; init; } = "minecraftModpack";

    public int ManifestVersion { get; init; } = 1;

    public string Name { get; init; } = "";

    public string Version { get; init; } = "";

    public string Author { get; init; } = "";

    public required CurseForgeManifestMinecraft Minecraft { get; init; }

    public List<CurseForgeManifestFile> Files { get; init; } = [];

    public string Overrides { get; init; } = "overrides";
}

/// <summary>CurseForge manifest.json 的 minecraft 节点。</summary>
internal sealed class CurseForgeManifestMinecraft
{
    public string Version { get; init; } = "";

    public List<CurseForgeModLoader> ModLoaders { get; init; } = [];
}

/// <summary>CurseForge manifest.json 的加载器声明。</summary>
internal sealed class CurseForgeModLoader
{
    public string Id { get; init; } = "";

    public bool Primary { get; init; }
}

/// <summary>CurseForge manifest.json 的单文件声明。</summary>
internal sealed class CurseForgeManifestFile
{
    // camelCase 策略会把 ProjectID 写成 projectID，恰好与官方规范一致
    public long ProjectID { get; init; }

    public long FileID { get; init; }

    public bool Required { get; init; }
}

/// <summary>mmc-pack.json（Prism/MultiMC 格式）根结构。</summary>
internal sealed class MmcPackRoot
{
    public int FormatVersion { get; init; } = 1;

    public List<MmcComponent> Components { get; init; } = [];
}

/// <summary>mmc-pack.json 的单组件声明。</summary>
internal sealed class MmcComponent
{
    public string Uid { get; init; } = "";

    public string Version { get; init; } = "";

    public bool Important { get; init; }
}
