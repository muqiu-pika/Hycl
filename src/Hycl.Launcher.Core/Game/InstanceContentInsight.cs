using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hycl.Launcher.Core.Game;

/// <summary>本地内容文件里能读到的元数据（模组 id/名称/版本/描述、资源包描述、依赖与冲突）。</summary>
public sealed record LocalContentMetadata(
    string Name,
    string Version,
    string Description,
    string ModId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> RequiredDependencies,
    IReadOnlyList<string> IncompatibleDependencies);

/// <summary>
/// 从 mods / resourcepacks / shaderpacks / datapacks 目录中的文件读取元数据。
/// 只读取文件内已有的元数据（fabric.mod.json / mods.toml / mcmod.info / pack.mcmeta），不做猜测。
/// </summary>
public static class LocalContentMetadataReader
{
    private static readonly Regex TomlDependencyBlock = new(
        @"\[\[dependencies\.[^\]]+\]\](?<body>.*?)(?=\[\[|\z)",
        RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public static LocalContentMetadata Read(string path, bool isDirectory)
    {
        var fallbackName = EffectiveName(path);
        try
        {
            var result = isDirectory ? ReadDirectory(path, fallbackName) : ReadArchive(path, fallbackName);
            return result ?? new LocalContentMetadata(fallbackName, "", "", "", [], [], []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                   or JsonException or ArgumentException)
        {
            return new LocalContentMetadata(fallbackName, "", "", "", [], [], []);
        }
    }

    private static LocalContentMetadata? ReadArchive(string path, string fallbackName)
    {
        using var archive = ZipFile.OpenRead(path);
        var fabric = ReadEntry(archive, "fabric.mod.json");
        if (!string.IsNullOrWhiteSpace(fabric))
        {
            return ParseFabric(fabric, fallbackName);
        }

        var modsToml = ReadEntry(archive, "META-INF/neoforge.mods.toml")
                       ?? ReadEntry(archive, "META-INF/mods.toml");
        if (!string.IsNullOrWhiteSpace(modsToml))
        {
            return ParseModsToml(modsToml, fallbackName);
        }

        var legacy = ReadEntry(archive, "mcmod.info");
        if (!string.IsNullOrWhiteSpace(legacy))
        {
            return ParseMcmodInfo(legacy, fallbackName);
        }

        var packMeta = ReadEntry(archive, "pack.mcmeta");
        if (!string.IsNullOrWhiteSpace(packMeta))
        {
            return ParsePackMeta(packMeta, fallbackName);
        }

        return null;
    }

    private static LocalContentMetadata? ReadDirectory(string path, string fallbackName)
    {
        var fabric = ReadFile(Path.Combine(path, "fabric.mod.json"));
        if (!string.IsNullOrWhiteSpace(fabric))
        {
            return ParseFabric(fabric, fallbackName);
        }

        var modsToml = ReadFile(Path.Combine(path, "META-INF", "neoforge.mods.toml"))
                       ?? ReadFile(Path.Combine(path, "META-INF", "mods.toml"));
        if (!string.IsNullOrWhiteSpace(modsToml))
        {
            return ParseModsToml(modsToml, fallbackName);
        }

        var packMeta = ReadFile(Path.Combine(path, "pack.mcmeta"));
        return !string.IsNullOrWhiteSpace(packMeta) ? ParsePackMeta(packMeta, fallbackName) : null;
    }

    private static LocalContentMetadata ParseFabric(string json, string fallbackName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var name = JsonString(root, "name");
        var version = JsonString(root, "version");
        var description = JsonString(root, "description");
        var id = JsonString(root, "id");
        var tags = new List<string>();
        foreach (var property in new[] { "environment" })
        {
            var value = JsonString(root, property);
            if (value.Length > 0)
            {
                tags.Add(value);
            }
        }

        var required = new List<string>();
        var incompatible = new List<string>();
        foreach (var (property, target) in new[]
                 {
                     ("depends", required),
                     ("breaks", incompatible)
                 })
        {
            var node = FindProperty(root, property);
            if (node is null || node.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var dependency in node.Value.EnumerateObject())
            {
                if (!string.IsNullOrWhiteSpace(dependency.Name))
                {
                    target.Add(dependency.Name);
                }
            }
        }

        return new LocalContentMetadata(
            name.Length > 0 ? name : fallbackName,
            NormalizeVersion(version),
            description,
            id,
            tags,
            required,
            incompatible);
    }

    private static LocalContentMetadata ParseModsToml(string text, string fallbackName)
    {
        var id = MatchValue(text, "modId");
        var name = MatchValue(text, "displayName");
        var version = MatchValue(text, "version");
        var description = MatchValue(text, "description");
        var required = new List<string>();
        var incompatible = new List<string>();
        foreach (Match match in TomlDependencyBlock.Matches(text))
        {
            var body = match.Groups["body"].Value;
            var dependencyId = MatchValue(body, "modId");
            if (dependencyId.Length == 0)
            {
                continue;
            }

            var type = MatchValue(body, "type").ToLowerInvariant();
            var mandatory = Regex.Match(body, @"mandatory\s*=\s*(true|false)", RegexOptions.IgnoreCase);
            if (type == "incompatible")
            {
                incompatible.Add(dependencyId);
            }
            else if (type == "required" || (type.Length == 0 && mandatory.Success
                                                          && mandatory.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                required.Add(dependencyId);
            }
        }

        return new LocalContentMetadata(
            name.Length > 0 ? name : fallbackName,
            NormalizeVersion(version),
            description,
            id,
            [],
            required.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            incompatible.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static LocalContentMetadata ParseMcmodInfo(string json, string fallbackName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().FirstOrDefault()
            : document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return new LocalContentMetadata(fallbackName, "", "", "", [], [], []);
        }

        return new LocalContentMetadata(
            JsonString(root, "name") is { Length: > 0 } name ? name : fallbackName,
            NormalizeVersion(JsonString(root, "version")),
            JsonString(root, "description"),
            JsonString(root, "modid"),
            [],
            // 1.6 时代的依赖是显示名而非可靠 modid，不参与缺失判断，避免误报
            [],
            []);
    }

    private static LocalContentMetadata ParsePackMeta(string json, string fallbackName)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var pack = FindProperty(root, "pack");
        var description = "";
        if (pack is not null && pack.Value.ValueKind == JsonValueKind.Object)
        {
            var node = FindProperty(pack.Value, "description");
            if (node is not null)
            {
                description = node.Value.ValueKind switch
                {
                    JsonValueKind.String => node.Value.GetString() ?? "",
                    JsonValueKind.Object => JsonString(node.Value, "text"),
                    _ => ""
                };
            }
        }

        return new LocalContentMetadata(fallbackName, "", description, "", [], [], []);
    }

    private static string EffectiveName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? name[..^".disabled".Length]
            : name;
    }

    private static string NormalizeVersion(string version)
        => version.StartsWith("${", StringComparison.Ordinal) ? "" : version.Trim();

    private static string MatchValue(string text, string key)
    {
        var match = Regex.Match(text, $@"\b{Regex.Escape(key)}\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string? ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? ReadFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var reader = new StreamReader(path, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static JsonElement? FindProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static string JsonString(JsonElement element, string name)
    {
        var node = FindProperty(element, name);
        return node is { ValueKind: JsonValueKind.String } ? node.Value.GetString() ?? "" : "";
    }
}

/// <summary>模组关系中的一行：谁依赖谁、缺失前置或冲突。</summary>
public sealed record ModRelationLine(
    string ModName,
    string FileName,
    string Relation,
    string Detail,
    bool IsProblem);

/// <summary>
/// 基于 jar 内元数据（fabric.mod.json / mods.toml）分析模组依赖关系：
/// 已安装但被禁用的前置、缺失前置、冲突（breaks / incompatible）。
/// 只统计同目录下已安装的模组，不联网补全。
/// </summary>
public static class ModRelationAnalyzer
{
    private static readonly HashSet<string> BuiltInDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "forge", "neoforge", "fabricloader", "fabric-loader", "fabric", "quilt_loader",
        "quiltloader", "java", "liteloader", "optifine", "cleanroom"
    };

    public static IReadOnlyList<ModRelationLine> Analyze(
        IEnumerable<(LocalContentFile File, LocalContentMetadata Metadata)> mods)
    {
        var entries = mods
            .Where(entry => entry.Metadata.ModId.Length > 0)
            .ToList();
        var installed = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, metadata) in entries)
        {
            installed.TryGetValue(metadata.ModId, out var existing);
            installed[metadata.ModId] = existing || file.IsEnabled;
        }

        var lines = new List<ModRelationLine>();
        foreach (var (file, metadata) in entries)
        {
            var display = metadata.Name.Length > 0 ? metadata.Name : file.Name;
            foreach (var dependency in metadata.RequiredDependencies)
            {
                if (BuiltInDependencies.Contains(dependency)
                    || dependency.Equals(metadata.ModId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (installed.TryGetValue(dependency, out var enabled))
                {
                    lines.Add(new ModRelationLine(
                        display,
                        file.Name,
                        "依赖",
                        enabled ? $"依赖 {dependency}（已安装）" : $"依赖 {dependency}（已安装但被禁用）",
                        !enabled));
                }
                else
                {
                    lines.Add(new ModRelationLine(
                        display,
                        file.Name,
                        "缺失前置",
                        $"需要 {dependency}，当前模组目录中未找到",
                        true));
                }
            }

            foreach (var incompatible in metadata.IncompatibleDependencies)
            {
                if (installed.ContainsKey(incompatible))
                {
                    lines.Add(new ModRelationLine(
                        display,
                        file.Name,
                        "冲突",
                        $"与已安装的 {incompatible} 不兼容",
                        true));
                }
            }
        }

        return lines;
    }
}
