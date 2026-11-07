using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 整合包加载器种类（取值与 <see cref="LoaderInstallPlan.Kind"/> 保持一致）。
/// </summary>
public static class ModpackLoaderKind
{
    public const string Vanilla = "vanilla";

    public const string Forge = "forge";

    public const string NeoForge = "neoforge";

    public const string Fabric = "fabric";

    public const string Quilt = "quilt";

    public const string Cleanroom = "cleanroom";
}

/// <summary>整合包清单中的一项待下载文件（Modrinth 直链）。</summary>
public sealed record ModpackFileEntry
{
    /// <summary>实例内相对路径（如 mods/xxx.jar）。</summary>
    public required string Path { get; init; }

    public long Size { get; init; }

    public string? Sha1 { get; init; }

    public IReadOnlyList<string> Urls { get; init; } = [];
}

/// <summary>
/// 规范化后的整合包清单：导入安装与导出共用同一份解析结果。
/// 对应 PCL-CE Modpack 模块解析出的 McInstallRequest + 文件列表。
/// </summary>
public sealed record ModpackDefinition
{
    /// <summary>modrinth / curseforge / hmcl / mcbbs / mmc。</summary>
    public required string Kind { get; init; }

    public required string FormatName { get; init; }

    public required string Name { get; init; }

    public string? PackVersion { get; init; }

    /// <summary>整合包声明的原版版本；空表示清单损坏。</summary>
    public required string GameVersion { get; init; }

    public string LoaderKind { get; init; } = ModpackLoaderKind.Vanilla;

    public string? LoaderVersion { get; init; }

    /// <summary>MCBBS addons 中的 OptiFine 版本（作为附属安装）。</summary>
    public string? OptiFineVersion { get; init; }

    /// <summary>整合包内需要解压到实例目录的前缀（含末尾 /）。</summary>
    public IReadOnlyList<string> OverridePrefixes { get; init; } = [];

    /// <summary>解压时跳过的完整条目名（如清单文件本身、HMCL 的 minecraft/pack.json）。</summary>
    public IReadOnlyList<string> ExcludedEntries { get; init; } = [];

    /// <summary>Modrinth 直链文件。</summary>
    public IReadOnlyList<ModpackFileEntry> Files { get; init; } = [];

    /// <summary>CurseForge 文件 Id（安装时按文件元数据落盘）。</summary>
    public IReadOnlyList<long> CurseForgeFileIds { get; init; } = [];

    /// <summary>CurseForge 可选文件 Id（manifest.required = false）。</summary>
    public IReadOnlyList<long> OptionalCurseForgeFileIds { get; init; } = [];
}

/// <summary>
/// 整合包识别结果。识别与解析是同一套实现，导入与后续导出共用。
/// </summary>
public sealed record ModpackInspection(
    string Kind,
    string FormatName,
    string? SuggestedName,
    string BaseFolder,
    string? RejectReason = null,
    string? NestedEntry = null,
    string? GameVersion = null,
    string? LoaderLabel = null)
{
    /// <summary>当前实现能否直接安装。</summary>
    public bool IsSupported => RejectReason is null;

    /// <summary>统一的拒绝文案（用于 UI 与异常）。</summary>
    public string UnsupportedMessage =>
        RejectReason is null
            ? ""
            : $"暂不支持该整合包格式：{FormatName}。{RejectReason}";
}

/// <summary>
/// 整合包清单识别与解析（对齐 PCL-CE Modpack 模块）：
/// 识别依据文件、判定顺序、dependencies / modLoaders / components / addons 的差异在这里统一处理。
/// </summary>
public static class ModpackReader
{
    /// <summary>PCL-CE 的判定顺序：mcbbs.packmeta → mmc-pack.json → modrinth → manifest → modpack.json → 嵌套包。</summary>
    private static readonly string[] ManifestCandidates =
    [
        "mcbbs.packmeta",
        "mmc-pack.json",
        "modrinth.index.json",
        "manifest.json",
        "modpack.json",
        "modpack.zip",
        "modpack.mrpack"
    ];

    private static readonly Regex FullPackRegex = new(
        @"^(?:.*/)?versions/([^/]+)/([^/]+)\.json$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------- 对外入口 -------------------------

    /// <summary>识别本地整合包（压缩包或已解压文件夹），不读取大文件。</summary>
    public static ModpackInspection InspectModpack(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            using var folder = PackSource.FromFolder(sourcePath);
            return Inspect(folder, Path.GetFileName(sourcePath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        if (!File.Exists(sourcePath))
        {
            throw new CompException($"整合包文件不存在：{sourcePath}");
        }

        if (Path.GetExtension(sourcePath).Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            throw new CompException("暂不支持 RAR 格式，请先手动解压，再用文件夹导入（PCL-CE 也需额外组件才能解压 RAR）。");
        }

        using var source = PackSource.FromZip(sourcePath);
        return Inspect(source, Path.GetFileNameWithoutExtension(sourcePath));
    }

    /// <summary>解析整合包清单；不支持或损坏时抛出带明确文案的异常。</summary>
    public static ModpackDefinition ParseModpack(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            using var folder = PackSource.FromFolder(sourcePath);
            var folderDetection = Detect(folder);
            EnsureSupported(folderDetection);
            return Parse(folder, folderDetection, Path.GetFileName(sourcePath.TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
        }

        if (!File.Exists(sourcePath))
        {
            throw new CompException($"整合包文件不存在：{sourcePath}");
        }

        if (Path.GetExtension(sourcePath).Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            throw new CompException("暂不支持 RAR 格式，请先手动解压，再用文件夹导入（PCL-CE 也需额外组件才能解压 RAR）。");
        }

        using var source = PackSource.FromZip(sourcePath);
        var detected = Detect(source);
        EnsureSupported(detected);
        return Parse(source, detected, Path.GetFileNameWithoutExtension(sourcePath));
    }

    /// <summary>把嵌套启动器整合包（modpack.zip / modpack.mrpack）提取到指定文件。</summary>
    public static Task ExtractNestedAsync(
        string sourcePath,
        string nestedEntry,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(sourcePath))
        {
            return Task.Run(
                () =>
                {
                    var from = Path.GetFullPath(Path.Combine(
                        sourcePath,
                        nestedEntry.Replace('/', Path.DirectorySeparatorChar)));
                    var root = Path.GetFullPath(sourcePath);
                    if (!from.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new CompException("嵌套整合包路径非法");
                    }

                    File.Copy(from, destinationPath, true);
                },
                cancellationToken);
        }

        return Task.Run(
            () =>
            {
                using var archive = ZipFile.OpenRead(sourcePath);
                var entry = archive.GetEntry(nestedEntry)
                            ?? throw new CompException($"整合包缺少嵌套文件：{nestedEntry}");
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
                entry.ExtractToFile(destinationPath, true);
            },
            cancellationToken);
    }

    // ------------------------- 识别 -------------------------

    private sealed record Detection(
        string Kind,
        string FormatName,
        string BaseFolder,
        string? ManifestEntry,
        string? RejectReason = null,
        string? NestedEntry = null);

    private static ModpackInspection Inspect(PackSource source, string fallbackName)
    {
        var detected = Detect(source);
        if (detected.RejectReason is not null)
        {
            return new ModpackInspection(
                detected.Kind,
                detected.FormatName,
                fallbackName,
                detected.BaseFolder,
                detected.RejectReason);
        }

        if (detected.Kind == "launcher-pack")
        {
            return new ModpackInspection(
                detected.Kind,
                detected.FormatName,
                fallbackName,
                detected.BaseFolder,
                NestedEntry: detected.NestedEntry);
        }

        var definition = Parse(source, detected, fallbackName);
        return new ModpackInspection(
            detected.Kind,
            detected.FormatName,
            definition.Name,
            detected.BaseFolder,
            GameVersion: definition.GameVersion,
            LoaderLabel: LoaderLabel(definition));
    }

    private static string LoaderLabel(ModpackDefinition definition)
    {
        var name = definition.LoaderKind switch
        {
            ModpackLoaderKind.Forge => "Forge",
            ModpackLoaderKind.NeoForge => "NeoForge",
            ModpackLoaderKind.Fabric => "Fabric",
            ModpackLoaderKind.Quilt => "Quilt",
            ModpackLoaderKind.Cleanroom => "Cleanroom",
            _ => "原版"
        };
        var version = string.IsNullOrWhiteSpace(definition.LoaderVersion)
            ? ""
            : " " + definition.LoaderVersion;
        return $"{name}{version} · MC {definition.GameVersion}";
    }

    private static Detection Detect(PackSource source)
    {
        var found = FindManifest(source);
        if (found is not null)
        {
            var baseFolder = found.Value.BaseFolder;
            var entryName = found.Value.EntryName;
            return Classify(source, baseFolder, entryName);
        }

        // 完整客户端包（含 versions/<id>/<id>.json）：PCL-CE 与 KAMUCL 都只能“解压后作为游戏目录”
        var fullPack = source.Entries
            .Select(entry => FullPackRegex.Match(entry))
            .FirstOrDefault(match => match.Success
                                     && match.Groups[1].Value.Equals(match.Groups[2].Value, StringComparison.OrdinalIgnoreCase));
        if (fullPack is not null)
        {
            return new Detection(
                "compress",
                "完整客户端包（含 versions 目录）",
                "",
                null,
                "这是包含 versions 目录的完整客户端包。请解压后，把启动器设置中的游戏目录指向其中的 .minecraft 文件夹。");
        }

        return new Detection(
            "unknown",
            "未识别的压缩包",
            "",
            null,
            "压缩包中没有找到 modrinth.index.json / manifest.json / modpack.json / mmc-pack.json / mcbbs.packmeta 等清单文件。");
    }

    private static (string BaseFolder, string EntryName)? FindManifest(PackSource source)
    {
        // 1) 根目录：按 PCL-CE 的候选顺序
        foreach (var candidate in ManifestCandidates)
        {
            if (source.Exists(candidate))
            {
                return ("", candidate);
            }
        }

        // 2) 单层子目录（兼容“压缩包里还套一层文件夹”）
        foreach (var candidate in ManifestCandidates)
        {
            var match = source.Entries.FirstOrDefault(entry =>
            {
                var segments = entry.Split('/');
                return segments.Length == 2
                       && segments[1].Equals(candidate, StringComparison.OrdinalIgnoreCase);
            });
            if (match is not null)
            {
                var folder = match.Split('/')[0];
                return (folder + "/", folder + "/" + candidate);
            }
        }

        return null;
    }

    private static Detection Classify(PackSource source, string baseFolder, string entryName)
    {
        var fileName = entryName[(entryName.LastIndexOf('/') + 1)..];
        switch (fileName.ToLowerInvariant())
        {
            case "mcbbs.packmeta":
                return new Detection("mcbbs", "MCBBS（mcbbs.packmeta）", baseFolder, entryName);

            case "manifest.json":
                return ClassifyManifest(source, baseFolder, entryName);

            case "mmc-pack.json":
                if (HasEntriesUnder(source, baseFolder + "patches/") || HasEntriesUnder(source, baseFolder + "libraries/"))
                {
                    return new Detection(
                        "mmc",
                        "MultiMC / Prism（mmc-pack.json）",
                        baseFolder,
                        entryName,
                        "该包包含 patches/ 或 libraries/ 覆盖内容，当前无法安全应用；请用 Prism 启动器安装，或解压后手动迁移。");
                }

                return new Detection("mmc", "MultiMC / Prism（mmc-pack.json）", baseFolder, entryName);

            case "modrinth.index.json":
                return new Detection("modrinth", "Modrinth（modrinth.index.json）", baseFolder, entryName);

            case "modpack.json":
                return new Detection("hmcl", "HMCL（modpack.json）", baseFolder, entryName);

            case "modpack.zip":
            case "modpack.mrpack":
                return new Detection(
                    "launcher-pack",
                    $"启动器打包（嵌套 {fileName}）",
                    baseFolder,
                    entryName,
                    NestedEntry: entryName);

            default:
                return new Detection(
                    "unknown",
                    "未识别的压缩包",
                    baseFolder,
                    entryName,
                    "清单文件类型无法识别。");
        }
    }

    private static Detection ClassifyManifest(PackSource source, string baseFolder, string entryName)
    {
        JsonNode? json;
        try
        {
            json = ParseJson(source.ReadText(entryName));
        }
        catch (Exception ex) when (ex is CompException or IOException)
        {
            return new Detection(
                "unknown",
                "CurseForge / MCBBS（manifest.json）",
                baseFolder,
                entryName,
                $"manifest.json 无法解析：{ex.Message}");
        }

        if (json is JsonObject obj && obj["addons"] is JsonArray)
        {
            return new Detection("mcbbs", "MCBBS（manifest.json + addons）", baseFolder, entryName);
        }

        if (json is JsonObject obj2 && obj2["minecraft"] is JsonObject)
        {
            return new Detection("curseforge", "CurseForge（manifest.json）", baseFolder, entryName);
        }

        return new Detection(
            "unknown",
            "manifest.json（非整合包清单）",
            baseFolder,
            entryName,
            "manifest.json 缺少 minecraft / addons 字段，不是 CurseForge 或 MCBBS 整合包。");
    }

    private static bool HasEntriesUnder(PackSource source, string prefix)
        => source.Entries.Any(entry => entry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static void EnsureSupported(Detection detected)
    {
        if (detected.RejectReason is not null)
        {
            throw new CompException($"暂不支持该整合包格式：{detected.FormatName}。{detected.RejectReason}");
        }

        if (detected.Kind == "launcher-pack")
        {
            throw new CompException("嵌套整合包需要先提取再解析。");
        }
    }

    // ------------------------- 解析 -------------------------

    private static ModpackDefinition Parse(PackSource source, Detection detected, string fallbackName)
        => detected.Kind switch
        {
            "modrinth" => ParseModrinth(source, detected, fallbackName),
            "curseforge" => ParseCurseForge(source, detected, fallbackName),
            "hmcl" => ParseHmcl(source, detected, fallbackName),
            "mcbbs" => ParseMcbbs(source, detected, fallbackName),
            "mmc" => ParseMmc(source, detected, fallbackName),
            _ => throw new CompException($"暂不支持该整合包格式：{detected.FormatName}。")
        };

    private static ModpackDefinition ParseModrinth(PackSource source, Detection detected, string fallbackName)
    {
        var root = ReadJsonObject(source, detected.ManifestEntry!, "modrinth.index.json");
        if (root["game"]?.ToString() is { Length: > 0 } game
            && !game.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
        {
            throw new CompException($"该 Modrinth 包不是 Minecraft 整合包（game = {game}）");
        }

        if (root["formatVersion"] is JsonValue formatValue
            && formatValue.TryGetValue<int>(out var formatVersion)
            && formatVersion != 1)
        {
            throw new CompException($"不支持的 Modrinth 整合包格式版本：{formatVersion}（当前支持 1）");
        }

        var dependencies = root["dependencies"] as JsonObject
                           ?? throw new CompException("Modrinth 整合包缺少 dependencies 声明");
        var gameVersion = dependencies["minecraft"]?.ToString();
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new CompException("Modrinth 整合包未提供 Minecraft 版本（dependencies.minecraft）");
        }

        var (loaderKind, loaderVersion) = ParseLoaderMap(new Dictionary<string, string?>
        {
            [ModpackLoaderKind.Forge] = dependencies["forge"]?.ToString(),
            [ModpackLoaderKind.NeoForge] = (dependencies["neoforge"] ?? dependencies["neo-forge"])?.ToString(),
            [ModpackLoaderKind.Quilt] = dependencies["quilt-loader"]?.ToString(),
            [ModpackLoaderKind.Fabric] = dependencies["fabric-loader"]?.ToString()
        }, gameVersion);

        var files = new List<ModpackFileEntry>();
        foreach (var node in root["files"] as JsonArray ?? [])
        {
            if (node is not JsonObject file)
            {
                continue;
            }

            // 与服务端专用文件一致：env.client = unsupported 跳过；optional 与 required 一并安装
            if (file["env"]?["client"]?.ToString() is { } client
                && client.Equals("unsupported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = file["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new CompException("modrinth.index.json 存在缺少 path 的文件项");
            }

            var urls = (file["downloads"] as JsonArray ?? [])
                .Select(url => url?.ToString())
                .Where(url => !string.IsNullOrWhiteSpace(url))
                .Cast<string>()
                .ToList();
            files.Add(new ModpackFileEntry
            {
                Path = path.Replace('\\', '/'),
                Size = file["fileSize"]?.GetValue<long>() ?? 0,
                Sha1 = file["hashes"]?["sha1"]?.ToString(),
                Urls = urls
            });
        }

        return new ModpackDefinition
        {
            Kind = "modrinth",
            FormatName = detected.FormatName,
            Name = SuggestName(root["name"]?.ToString(), fallbackName),
            PackVersion = root["versionId"]?.ToString(),
            GameVersion = gameVersion,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            OverridePrefixes = [detected.BaseFolder + "overrides/", detected.BaseFolder + "client-overrides/"],
            ExcludedEntries = [detected.ManifestEntry!],
            Files = files
        };
    }

    private static ModpackDefinition ParseCurseForge(PackSource source, Detection detected, string fallbackName)
    {
        var root = ReadJsonObject(source, detected.ManifestEntry!, "manifest.json");
        var minecraft = root["minecraft"] as JsonObject
                        ?? throw new CompException("CurseForge 整合包缺少 minecraft 声明");
        var gameVersion = minecraft["version"]?.ToString();
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new CompException("CurseForge 整合包未提供 Minecraft 版本（minecraft.version）");
        }

        string? forge = null;
        string? neoForge = null;
        string? fabric = null;
        string? quilt = null;
        foreach (var node in minecraft["modLoaders"] as JsonArray ?? [])
        {
            if (node is not JsonObject loader)
            {
                continue;
            }

            var id = loader["id"]?.ToString()?.ToLowerInvariant() ?? "";
            if (id.StartsWith("forge-", StringComparison.Ordinal))
            {
                var version = id["forge-".Length..];
                if (version.Contains("recommended", StringComparison.Ordinal))
                {
                    throw new CompException("该整合包使用 Forge 的 recommended 标记（过旧），当前无法安装");
                }

                forge = NormalizeLoaderVersion(version, gameVersion);
            }
            else if (id.StartsWith("neoforge-", StringComparison.Ordinal))
            {
                neoForge = NormalizeLoaderVersion(id["neoforge-".Length..], gameVersion);
            }
            else if (id.StartsWith("fabric-", StringComparison.Ordinal))
            {
                fabric = id["fabric-".Length..];
            }
            else if (id.StartsWith("quilt-", StringComparison.Ordinal))
            {
                quilt = id["quilt-".Length..];
            }
        }

        var (loaderKind, loaderVersion) = ParseLoaderMap(new Dictionary<string, string?>
        {
            [ModpackLoaderKind.Forge] = forge,
            [ModpackLoaderKind.NeoForge] = neoForge,
            [ModpackLoaderKind.Quilt] = quilt,
            [ModpackLoaderKind.Fabric] = fabric
        }, gameVersion);

        var overrides = root["overrides"]?.ToString() ?? "overrides";
        var overridePrefix = string.IsNullOrWhiteSpace(overrides) || overrides is "." or "./"
            ? detected.BaseFolder
            : detected.BaseFolder + overrides.TrimEnd('/') + "/";

        var fileIds = new List<long>();
        var optionalIds = new List<long>();
        foreach (var node in root["files"] as JsonArray ?? [])
        {
            if (node is not JsonObject file)
            {
                continue;
            }

            var fileId = file["fileID"]?.GetValue<long>();
            if (fileId is null)
            {
                continue;
            }

            fileIds.Add(fileId.Value);
            if (file["required"] is JsonValue required && required.TryGetValue<bool>(out var isRequired) && !isRequired)
            {
                optionalIds.Add(fileId.Value);
            }
        }

        return new ModpackDefinition
        {
            Kind = "curseforge",
            FormatName = detected.FormatName,
            Name = SuggestName(root["name"]?.ToString(), fallbackName),
            PackVersion = root["version"]?.ToString(),
            GameVersion = gameVersion,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            OverridePrefixes = overridePrefix.Length == 0 ? [] : [overridePrefix],
            ExcludedEntries = [detected.ManifestEntry!],
            CurseForgeFileIds = fileIds,
            OptionalCurseForgeFileIds = optionalIds
        };
    }

    private static ModpackDefinition ParseHmcl(PackSource source, Detection detected, string fallbackName)
    {
        var root = ReadJsonObject(source, detected.ManifestEntry!, "modpack.json");
        var packEntry = detected.BaseFolder + "minecraft/pack.json";
        var pack = source.Exists(packEntry) ? ReadJsonObject(source, packEntry, "minecraft/pack.json") : null;

        // 新版 HMCL 包把版本信息放在 minecraft/pack.json 的 jar 字段；旧包放在 modpack.json 的 gameVersion
        var gameVersion = root["gameVersion"]?.ToString();
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            gameVersion = pack?["jar"]?.ToString() ?? pack?["id"]?.ToString();
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new CompException("HMCL 整合包未提供游戏版本（modpack.json 的 gameVersion 或 minecraft/pack.json 的 jar 字段）");
        }

        var (loaderKind, loaderVersion) = DetectHmclLoader(pack, gameVersion);
        var excluded = new List<string> { detected.ManifestEntry! };
        if (pack is not null)
        {
            excluded.Add(packEntry);
        }

        return new ModpackDefinition
        {
            Kind = "hmcl",
            FormatName = detected.FormatName,
            Name = SuggestName(root["name"]?.ToString(), fallbackName),
            PackVersion = root["version"]?.ToString(),
            GameVersion = gameVersion,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            OverridePrefixes = [detected.BaseFolder + "minecraft/"],
            ExcludedEntries = excluded
        };
    }

    private static (string Kind, string? Version) DetectHmclLoader(JsonObject? pack, string gameVersion)
    {
        if (pack?["libraries"] is not JsonArray libraries)
        {
            return (ModpackLoaderKind.Vanilla, null);
        }

        string? forge = null;
        string? neoForge = null;
        string? fabric = null;
        string? quilt = null;
        foreach (var node in libraries)
        {
            var name = node?["name"]?.ToString() ?? "";
            var parts = name.Split(':');
            if (parts.Length < 3)
            {
                continue;
            }

            var group = parts[0];
            var artifact = parts[1];
            var version = parts[2];
            if (group.Equals("net.minecraftforge", StringComparison.OrdinalIgnoreCase)
                && artifact.Equals("forge", StringComparison.OrdinalIgnoreCase))
            {
                forge = NormalizeLoaderVersion(version, gameVersion);
            }
            else if (group.Equals("net.neoforged", StringComparison.OrdinalIgnoreCase)
                     && artifact.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            {
                neoForge = NormalizeLoaderVersion(version, gameVersion);
            }
            else if (group.Equals("net.fabricmc", StringComparison.OrdinalIgnoreCase)
                     && artifact.Equals("fabric-loader", StringComparison.OrdinalIgnoreCase))
            {
                fabric = version;
            }
            else if (group.Equals("org.quiltmc", StringComparison.OrdinalIgnoreCase)
                     && artifact.Equals("quilt-loader", StringComparison.OrdinalIgnoreCase))
            {
                quilt = version;
            }
        }

        return ParseLoaderMap(new Dictionary<string, string?>
        {
            [ModpackLoaderKind.Forge] = forge,
            [ModpackLoaderKind.NeoForge] = neoForge,
            [ModpackLoaderKind.Quilt] = quilt,
            [ModpackLoaderKind.Fabric] = fabric
        }, gameVersion);
    }

    private static ModpackDefinition ParseMcbbs(PackSource source, Detection detected, string fallbackName)
    {
        var root = ReadJsonObject(source, detected.ManifestEntry!, "MCBBS 清单");
        if (root["addons"] is not JsonArray addons)
        {
            throw new CompException("MCBBS 整合包缺少 addons 声明");
        }

        string? gameVersion = null;
        string? forge = null;
        string? neoForge = null;
        string? fabric = null;
        string? quilt = null;
        string? cleanroom = null;
        string? optiFine = null;
        foreach (var node in addons)
        {
            if (node is not JsonObject addon)
            {
                continue;
            }

            var id = addon["id"]?.ToString()?.ToLowerInvariant() ?? "";
            var version = addon["version"]?.ToString();
            switch (id)
            {
                case "game":
                    gameVersion = version;
                    break;
                case "forge":
                    forge = version;
                    break;
                case "neoforge":
                case "neo":
                    neoForge = version;
                    break;
                case "fabric":
                    fabric = version;
                    break;
                case "quilt":
                    quilt = version;
                    break;
                case "cleanroom":
                    cleanroom = version;
                    break;
                case "optifine":
                    optiFine = version;
                    break;
                case "liteloader":
                    throw new CompException("该 MCBBS 整合包依赖 LiteLoader，当前无法与主加载器一并安装；请在对应启动器中手动安装。");
                case "":
                    break;
                default:
                    throw new CompException($"该 MCBBS 整合包包含当前不支持的组件：{id}");
            }
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new CompException("MCBBS 整合包未提供游戏版本（addons 中的 game）");
        }

        string loaderKind;
        string? loaderVersion;
        if (cleanroom is not null)
        {
            loaderKind = ModpackLoaderKind.Cleanroom;
            loaderVersion = cleanroom;
        }
        else
        {
            (loaderKind, loaderVersion) = ParseLoaderMap(new Dictionary<string, string?>
            {
                [ModpackLoaderKind.Forge] = forge,
                [ModpackLoaderKind.NeoForge] = neoForge,
                [ModpackLoaderKind.Quilt] = quilt,
                [ModpackLoaderKind.Fabric] = fabric
            }, gameVersion);
        }

        return new ModpackDefinition
        {
            Kind = "mcbbs",
            FormatName = detected.FormatName,
            Name = SuggestName(root["name"]?.ToString(), fallbackName),
            PackVersion = root["version"]?.ToString(),
            GameVersion = gameVersion,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            OptiFineVersion = string.IsNullOrWhiteSpace(optiFine) ? null : optiFine,
            OverridePrefixes = [detected.BaseFolder + "overrides/"],
            ExcludedEntries = [detected.ManifestEntry!]
        };
    }

    private static ModpackDefinition ParseMmc(PackSource source, Detection detected, string fallbackName)
    {
        var root = ReadJsonObject(source, detected.ManifestEntry!, "mmc-pack.json");
        if (root["components"] is not JsonArray components)
        {
            throw new CompException("MultiMC 整合包缺少 components 声明（mmc-pack.json）");
        }

        string? gameVersion = null;
        string? forge = null;
        string? neoForge = null;
        string? fabric = null;
        string? quilt = null;
        string? cleanroom = null;
        foreach (var node in components)
        {
            if (node is not JsonObject component)
            {
                continue;
            }

            var uid = component["uid"]?.ToString() ?? "";
            var version = component["version"]?.ToString();
            switch (uid)
            {
                case "net.minecraft":
                    gameVersion = version;
                    break;
                case "net.minecraftforge":
                    // PCL-CE：Forge 组件版本以 0. 开头代表 Cleanroom
                    if (version?.StartsWith("0.", StringComparison.Ordinal) == true)
                    {
                        cleanroom = version;
                    }
                    else
                    {
                        forge = version;
                    }

                    break;
                case "net.neoforged":
                    neoForge = version;
                    break;
                case "net.fabricmc.fabric-loader":
                    fabric = version;
                    break;
                case "org.quiltmc.quilt-loader":
                    quilt = version;
                    break;
                case "org.lwjgl":
                    break;
                default:
                    if (component["important"] is JsonValue important
                        && important.TryGetValue<bool>(out var isImportant)
                        && isImportant)
                    {
                        throw new CompException($"该 MultiMC 整合包包含当前无法安装的组件：{uid}");
                    }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            throw new CompException("MultiMC 整合包未提供 Minecraft 版本（components 中的 net.minecraft）");
        }

        string loaderKind;
        string? loaderVersion;
        if (cleanroom is not null)
        {
            loaderKind = ModpackLoaderKind.Cleanroom;
            loaderVersion = cleanroom;
        }
        else
        {
            (loaderKind, loaderVersion) = ParseLoaderMap(new Dictionary<string, string?>
            {
                [ModpackLoaderKind.Forge] = forge,
                [ModpackLoaderKind.NeoForge] = neoForge,
                [ModpackLoaderKind.Quilt] = quilt,
                [ModpackLoaderKind.Fabric] = fabric
            }, gameVersion);
        }

        var cfgEntry = detected.BaseFolder + "instance.cfg";
        var instanceName = source.Exists(cfgEntry)
            ? Regex.Match(source.ReadText(cfgEntry), @"(?m)^name\s*=\s*(.+)$").Groups[1].Value.Trim()
            : "";

        return new ModpackDefinition
        {
            Kind = "mmc",
            FormatName = detected.FormatName,
            Name = SuggestName(instanceName, fallbackName),
            PackVersion = null,
            GameVersion = gameVersion,
            LoaderKind = loaderKind,
            LoaderVersion = loaderVersion,
            OverridePrefixes = [detected.BaseFolder + ".minecraft/"],
            ExcludedEntries = [detected.ManifestEntry!, cfgEntry]
        };
    }

    // ------------------------- 公共工具 -------------------------

    /// <summary>
    /// 多加载器字段优先级：Forge 系 → Quilt → Fabric；版本统一做 “mcVersion-” 前缀剥离。
    /// </summary>
    private static (string Kind, string? Version) ParseLoaderMap(
        IReadOnlyDictionary<string, string?> loaders,
        string gameVersion)
    {
        foreach (var (kind, version) in loaders)
        {
            if (!string.IsNullOrWhiteSpace(version))
            {
                return (kind, NormalizeLoaderVersion(version, gameVersion));
            }
        }

        return (ModpackLoaderKind.Vanilla, null);
    }

    private static string NormalizeLoaderVersion(string version, string gameVersion)
    {
        var trimmed = version.Trim();
        var prefix = gameVersion + "-";
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..]
            : trimmed;
    }

    private static string SuggestName(string? name, string fallback)
    {
        var value = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        // 整合包名常含路径非法字符（如 ": "）：先清洗成可用目录名，用户仍可在命名对话框修改
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        value = value.Replace('!', '-').Replace(';', '-').Trim().TrimEnd('.');
        if (value.Length > 80)
        {
            value = value[..80].Trim();
        }

        return string.IsNullOrWhiteSpace(value) ? "整合包" : value;
    }

    private static JsonObject ReadJsonObject(PackSource source, string entryName, string displayName)
    {
        var text = source.ReadText(entryName);
        try
        {
            return ParseJson(text) as JsonObject
                   ?? throw new CompException($"{displayName} 的根节点不是 JSON 对象");
        }
        catch (CompException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            throw new CompException($"{displayName} 解析失败：{ex.Message}");
        }
    }

    private static JsonNode? ParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new CompException($"JSON 解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 压缩包 / 已解压文件夹的统一视图：识别与解析只写一份逻辑。
    /// </summary>
    private sealed class PackSource : IDisposable
    {
        private readonly ZipArchive? _archive;

        private readonly string? _root;

        private PackSource(ZipArchive? archive, string? root, IReadOnlyList<string> entries)
        {
            _archive = archive;
            _root = root;
            Entries = entries;
        }

        /// <summary>规范化后的相对路径（/ 分隔，不含目录条目）。</summary>
        public IReadOnlyList<string> Entries { get; }

        public static PackSource FromZip(string path)
        {
            ZipArchive archive;
            try
            {
                archive = ZipFile.OpenRead(path);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                throw new CompException("无法识别该整合包文件：不是有效的 ZIP。" + ex.Message);
            }

            var entries = archive.Entries
                .Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\'))
                .Select(entry => entry.FullName.Replace('\\', '/'))
                .ToList();
            return new PackSource(archive, null, entries);
        }

        public static PackSource FromFolder(string path)
            => new(
                null,
                Path.GetFullPath(path),
                Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
                    .Select(file => Path.GetRelativePath(path, file).Replace('\\', '/'))
                    .ToList());

        public bool Exists(string relativePath)
            => Entries.Any(entry => entry.Equals(relativePath, StringComparison.OrdinalIgnoreCase));

        public string ReadText(string relativePath)
        {
            if (_archive is not null)
            {
                var entry = _archive.Entries.FirstOrDefault(e =>
                    e.FullName.Replace('\\', '/').Equals(relativePath, StringComparison.OrdinalIgnoreCase))
                    ?? throw new CompException($"整合包缺少文件：{relativePath}");
                using var stream = entry.Open();
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }

            var root = _root!;
            var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !full.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new CompException("整合包包含非法路径");
            }

            return File.ReadAllText(full);
        }

        public void Dispose() => _archive?.Dispose();
    }
}
