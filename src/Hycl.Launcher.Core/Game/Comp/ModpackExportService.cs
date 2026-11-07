using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>导出格式（与导入侧 ModpackReader 的识别种类一一对应）。</summary>
public enum ModpackExportFormat
{
    /// <summary>Modrinth 整合包（.mrpack：modrinth.index.json + overrides/）。</summary>
    Modrinth,

    /// <summary>CurseForge 整合包（manifest.json + overrides/，扩展名 .zip）。</summary>
    CurseForge,

    /// <summary>MultiMC / Prism 实例包（mmc-pack.json + instance.cfg + .minecraft/，扩展名 .zip）。</summary>
    MultiMc
}

/// <summary>导出时勾选的内容类型与清单元数据。</summary>
public sealed record ModpackExportOptions
{
    public required ModpackExportFormat Format { get; init; }

    public required string Name { get; init; }

    /// <summary>整合包版本号（写入 modrinth.versionId / manifest.version）。</summary>
    public string Version { get; init; } = "1.0.0";

    public string Author { get; init; } = "Hycl";

    public bool IncludeMods { get; init; } = true;

    /// <summary>整合包重要数据：脚本、KubeJS、defaultconfigs。</summary>
    public bool IncludePackData { get; init; } = true;

    /// <summary>模组设置：config 与 options.txt（不含 JEI / 帕秋莉 / 地图的个人数据）。</summary>
    public bool IncludeModConfig { get; init; } = true;

    /// <summary>已绘制的地图：Xaero / JourneyMap / MapWriter 的地图数据与配置。</summary>
    public bool IncludeMaps { get; init; } = true;

    /// <summary>JEI 个人信息（config/jei）。</summary>
    public bool IncludeJei { get; init; } = true;

    /// <summary>帕秋莉手册个人信息（config/patchouli 与 patchouli_books）。</summary>
    public bool IncludePatchouli { get; init; } = true;

    public bool IncludeResourcePacks { get; init; } = true;

    /// <summary>勾选的资源包顶层文件/文件夹名（不区分大小写）；null 表示全部。</summary>
    public IReadOnlySet<string>? SelectedResourcePacks { get; init; }

    public bool IncludeShaderPacks { get; init; } = true;

    /// <summary>数据包（saves/&lt;世界&gt;/datapacks 下的 zip）。</summary>
    public bool IncludeDataPacks { get; init; } = true;

    public bool IncludeSaves { get; init; }

    public bool IncludeScreenshots { get; init; }

    public bool IncludeSchematics { get; init; }

    public bool IncludeServers { get; init; }
}

/// <summary>导出进度：阶段文案 + 文件计数 + 0-100 百分比。</summary>
public sealed record ModpackExportProgress(string Stage, int FilesDone, int FilesTotal, double Percent);

/// <summary>导出结果：清单条目数 / overrides 条目数 / 是否用到了在线反查 / 需要提醒用户的信息。</summary>
public sealed record ModpackExportResult(
    string OutputPath,
    string FormatName,
    int ManifestFileCount,
    int OverrideFileCount,
    bool UsedOnlineLookup,
    string? Warning);

/// <summary>导出内容列表中的资源包条目（顶层 zip 或未压缩文件夹）。</summary>
public sealed record ModpackExportResourcePack(string Name, bool IsDirectory, int FileCount);

/// <summary>导出内容预览：游戏本体的版本/加载器文本、模组数量与资源包清单。</summary>
public sealed record ModpackExportPreview(
    string GameTitle,
    int ModCount,
    IReadOnlyList<ModpackExportResourcePack> ResourcePacks);

/// <summary>
/// 整合包导出：识别与清单解析复用 <see cref="ModpackReader"/>，这里只负责反向收集内容、在线反查与打包。
/// <para>
/// 内容分流规则：
/// <list type="bullet">
/// <item>MOD、资源包、光影、数据包先算哈希再按当前格式反查（Modrinth 用 sha1，CurseForge 用 murmur2 指纹），
/// 命中且哈希一致的项写入清单 files[]，由启动器按项目 ID 重新下载。</item>
/// <item>配置、脚本、存档、截图、投影、服务器列表，以及未命中的自装 jar / 被禁用（.disabled）的文件，
/// 原样放进 overrides（MultiMC 为 .minecraft/），保证换台机器也能还原。</item>
/// <item>versions/ 下的版本 JSON 与客户端 jar 不导出：加载器由清单声明，客户端由启动器自行下载。</item>
/// </list>
/// </para>
/// </summary>
public sealed class ModpackExportService(
    ModrinthClient modrinth,
    CurseForgeClient curseForge,
    VersionResolver versionResolver,
    LauncherLogger logger)
{
    private const string OverridesPrefix = "overrides/";

    private const string MmcPrefix = ".minecraft/";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public async Task<ModpackExportResult> ExportAsync(
        MinecraftInstance instance,
        string contentRoot,
        string outputPath,
        ModpackExportOptions options,
        IProgress<ModpackExportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.Name))
        {
            throw new IOException("整合包名称不能为空");
        }

        var meta = await ReadPackMetaAsync(instance, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(meta.GameVersion))
        {
            throw new IOException("无法确定实例的 Minecraft 版本，不能生成整合包清单");
        }

        Report(progress, "扫描实例内容", 0, 0, 0);
        var candidates = CollectFiles(contentRoot, options, cancellationToken);
        if (candidates.Count == 0)
        {
            throw new IOException("没有可导出的内容，请至少勾选一项");
        }

        // 1) 只有“可按项目 ID 还原”的候选才需要哈希（清单里要 sha1/sha512/指纹）
        var hashed = new Dictionary<string, ModpackFileHashes>(StringComparer.OrdinalIgnoreCase);
        var eligible = candidates.Where(c => c.LookupEligible).ToList();
        for (var index = 0; index < eligible.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = eligible[index];
            Report(progress, $"正在计算文件哈希（{index + 1}/{eligible.Count}）",
                index, eligible.Count, 3 + 32.0 * (index + 1) / eligible.Count);
            try
            {
                hashed[candidate.RelativePath] = await ModpackHash
                    .ComputeAsync(candidate.SourcePath, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 单个文件读不了就退化为 overrides 内容，不让整次导出失败
                logger.Warn($"导出时读取文件失败，将跳过在线反查：{candidate.SourcePath} · {ex.Message}");
                candidate.LookupEligible = false;
            }
        }

        // 2) 按格式在线反查
        var matches = new List<MatchedFile>();
        var usedOnlineLookup = false;
        string? lookupWarning = null;
        if (options.Format != ModpackExportFormat.MultiMc && eligible.Count > 0)
        {
            Report(progress, options.Format == ModpackExportFormat.Modrinth
                ? "正在查询 Modrinth"
                : "正在查询 CurseForge", 0, eligible.Count, 36);
            try
            {
                matches = options.Format == ModpackExportFormat.Modrinth
                    ? await FindModrinthMatchesAsync(candidates, hashed, cancellationToken).ConfigureAwait(false)
                    : await FindCurseForgeMatchesAsync(candidates, hashed, cancellationToken).ConfigureAwait(false);
                usedOnlineLookup = true;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.Warn($"导出时在线反查失败，全部内容将打包进 overrides：{ex.Message}");
                lookupWarning = $"在线反查失败（{ex.Message}），已把全部内容打包进 overrides。";
            }
        }

        var manifestPaths = matches
            .Select(match => match.Candidate.RelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var overrides = candidates
            .Where(candidate => !manifestPaths.Contains(candidate.RelativePath))
            .ToList();

        // 3) 打包：先写清单，再逐文件写 overrides；临时文件校验通过后才替换目标文件
        var temporary = outputPath + ".hycl-tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            await using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                await WriteManifestAsync(archive, options, meta, matches, cancellationToken).ConfigureAwait(false);

                var prefix = options.Format == ModpackExportFormat.MultiMc ? MmcPrefix : OverridesPrefix;
                for (var index = 0; index < overrides.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = overrides[index];
                    Report(progress, $"正在打包 overrides（{index + 1}/{overrides.Count}）",
                        index, overrides.Count, 45 + 54.0 * (index + 1) / overrides.Count);
                    await WriteFileEntryAsync(archive, prefix + item.RelativePath, item.SourcePath, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            Report(progress, "正在校验导出结果", overrides.Count, overrides.Count, 99);
            var inspection = ModpackReader.InspectModpack(temporary);
            var expectedKind = options.Format switch
            {
                ModpackExportFormat.Modrinth => "modrinth",
                ModpackExportFormat.CurseForge => "curseforge",
                _ => "mmc"
            };
            if (!inspection.IsSupported || inspection.Kind != expectedKind)
            {
                throw new IOException($"导出文件自检失败：{inspection.UnsupportedMessage}");
            }

            File.Move(temporary, outputPath, true);
            Report(progress, "导出完成", candidates.Count, candidates.Count, 100);
            logger.Info($"整合包导出完成：{outputPath}（{FormatName(options.Format)}，清单 {matches.Count} 项，overrides {overrides.Count} 项）");

            var warnings = new List<string>();
            if (lookupWarning is not null)
            {
                warnings.Add(lookupWarning);
            }

            if (options.Format == ModpackExportFormat.MultiMc)
            {
                warnings.Add("MultiMC / Prism 实例包不按项目 ID 记录内容，全部文件已打包进 .minecraft/。");
            }
            else if (usedOnlineLookup && eligible.Count > 0 && matches.Count < eligible.Count)
            {
                warnings.Add($"{eligible.Count - matches.Count} 个文件未在"
                             + (options.Format == ModpackExportFormat.Modrinth ? " Modrinth " : " CurseForge ")
                             + "找到，已放入 overrides。");
            }

            if (meta.LoaderVersion is null && meta.LoaderKind != ModpackLoaderKind.Vanilla)
            {
                warnings.Add("未能从版本 JSON 读出加载器版本，清单不会声明加载器。");
            }

            if (meta.LoaderKind is ModpackLoaderKind.Cleanroom && options.Format != ModpackExportFormat.MultiMc)
            {
                warnings.Add("Cleanroom 无法写入该格式的加载器字段，清单将只声明原版。");
            }

            return new ModpackExportResult(
                outputPath,
                FormatName(options.Format),
                matches.Count,
                overrides.Count,
                usedOnlineLookup,
                warnings.Count == 0 ? null : string.Join(" ", warnings));
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    /// <summary>导出内容列表预览：游戏本体的版本/加载器文本、模组数量与资源包顶层条目。</summary>
    public async Task<ModpackExportPreview> PreviewAsync(
        MinecraftInstance instance,
        string contentRoot,
        CancellationToken cancellationToken = default)
    {
        var meta = await ReadPackMetaAsync(instance, cancellationToken).ConfigureAwait(false);
        var typeText = instance.Type switch
        {
            "release" => "正式版",
            "snapshot" => "快照",
            "old_beta" => "旧版 Beta",
            "old_alpha" => "旧版 Alpha",
            _ => instance.Type
        };
        var title = string.IsNullOrWhiteSpace(typeText)
            ? $"Minecraft {meta.GameVersion}"
            : $"{typeText} {meta.GameVersion}";
        var loaderName = meta.LoaderKind switch
        {
            ModpackLoaderKind.Forge => "Forge",
            ModpackLoaderKind.NeoForge => "NeoForge",
            ModpackLoaderKind.Fabric => "Fabric",
            ModpackLoaderKind.Quilt => "Quilt",
            ModpackLoaderKind.Cleanroom => "Cleanroom",
            _ => ""
        };
        if (loaderName.Length > 0)
        {
            title += string.IsNullOrEmpty(meta.LoaderVersion)
                ? $", {loaderName}"
                : $", {loaderName} {meta.LoaderVersion}";
        }

        var mods = Path.Combine(contentRoot, "mods");
        var modCount = Directory.Exists(mods) ? Directory.EnumerateFiles(mods).Count() : 0;
        return new ModpackExportPreview(title, modCount, CollectResourcePackEntries(contentRoot));
    }

    private static IReadOnlyList<ModpackExportResourcePack> CollectResourcePackEntries(string contentRoot)
    {
        var directory = Path.Combine(contentRoot, "resourcepacks");
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var entries = new List<ModpackExportResourcePack>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            entries.Add(new ModpackExportResourcePack(Path.GetFileName(file), false, 1));
        }

        foreach (var folder in Directory.EnumerateDirectories(directory))
        {
            var count = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Count();
            entries.Add(new ModpackExportResourcePack(Path.GetFileName(folder), true, count));
        }

        return entries.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<MatchedFile>> FindModrinthMatchesAsync(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<string, ModpackFileHashes> hashed,
        CancellationToken cancellationToken)
    {
        var sha1s = hashed.Values.Select(value => value.Sha1).ToList();
        var hits = await modrinth.LookupBySha1Async(sha1s, cancellationToken).ConfigureAwait(false);
        var results = new List<MatchedFile>();
        foreach (var candidate in candidates)
        {
            if (!candidate.LookupEligible || !hashed.TryGetValue(candidate.RelativePath, out var value))
            {
                continue;
            }

            if (!hits.TryGetValue(value.Sha1, out var hit) || string.IsNullOrEmpty(hit.ProjectId))
            {
                continue;
            }

            results.Add(new MatchedFile
            {
                Candidate = candidate,
                ProjectId = hit.ProjectId,
                Sha1 = hit.Sha1,
                Sha512 = hit.Sha512,
                DownloadUrl = hit.DownloadUrl,
                Size = hit.Size > 0 ? hit.Size : value.Size
            });
        }

        return results;
    }

    private async Task<List<MatchedFile>> FindCurseForgeMatchesAsync(
        IReadOnlyList<Candidate> candidates,
        IReadOnlyDictionary<string, ModpackFileHashes> hashed,
        CancellationToken cancellationToken)
    {
        var fingerprints = hashed.Values.Select(value => value.Murmur2).ToList();
        var files = await curseForge.LookupFingerprintsAsync(fingerprints, cancellationToken).ConfigureAwait(false);
        // CurseForge 响应不回传指纹，按 sha1 对齐；只保留有 projectID / fileID 的项
        var bySha1 = new Dictionary<string, CompFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            if (!string.IsNullOrEmpty(file.Hash))
            {
                bySha1[file.Hash] = file;
            }
        }

        var results = new List<MatchedFile>();
        foreach (var candidate in candidates)
        {
            if (!candidate.LookupEligible || !hashed.TryGetValue(candidate.RelativePath, out var value))
            {
                continue;
            }

            if (!bySha1.TryGetValue(value.Sha1, out var file)
                || string.IsNullOrEmpty(file.ProjectId)
                || string.IsNullOrEmpty(file.Id))
            {
                continue;
            }

            results.Add(new MatchedFile
            {
                Candidate = candidate,
                ProjectId = file.ProjectId,
                FileId = file.Id,
                Sha1 = value.Sha1,
                Sha512 = value.Sha512,
                Size = value.Size
            });
        }

        return results;
    }

    // ------------------------- 清单 -------------------------

    private static async Task WriteManifestAsync(
        ZipArchive archive,
        ModpackExportOptions options,
        PackMeta meta,
        IReadOnlyList<MatchedFile> matches,
        CancellationToken cancellationToken)
    {
        switch (options.Format)
        {
            case ModpackExportFormat.Modrinth:
                await WriteTextEntryAsync(archive, "modrinth.index.json",
                    BuildModrinthIndex(options, meta, matches), cancellationToken).ConfigureAwait(false);
                break;
            case ModpackExportFormat.CurseForge:
                await WriteTextEntryAsync(archive, "manifest.json",
                    BuildCurseForgeManifest(options, meta, matches), cancellationToken).ConfigureAwait(false);
                break;
            default:
                await WriteTextEntryAsync(archive, "mmc-pack.json",
                    BuildMmcPack(meta), cancellationToken).ConfigureAwait(false);
                await WriteTextEntryAsync(archive, "instance.cfg",
                    $"InstanceType=OneSix\r\nname={options.Name}\r\n", cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>modrinth.index.json：字段名与大小写严格按 Modrinth 规范。</summary>
    private static string BuildModrinthIndex(
        ModpackExportOptions options,
        PackMeta meta,
        IReadOnlyList<MatchedFile> matches)
    {
        var dependencies = new Dictionary<string, string>
        {
            ["minecraft"] = meta.GameVersion
        };
        if (!string.IsNullOrEmpty(meta.LoaderVersion))
        {
            var key = meta.LoaderKind switch
            {
                ModpackLoaderKind.Forge => "forge",
                ModpackLoaderKind.NeoForge => "neoforge",
                ModpackLoaderKind.Fabric => "fabric-loader",
                ModpackLoaderKind.Quilt => "quilt-loader",
                _ => ""
            };
            if (key.Length > 0)
            {
                dependencies[key] = meta.LoaderVersion;
            }
        }

        var files = new List<object>();
        foreach (var match in matches)
        {
            if (string.IsNullOrEmpty(match.DownloadUrl) || string.IsNullOrEmpty(match.Sha1))
            {
                continue;
            }

            var hashes = new Dictionary<string, string> { ["sha1"] = match.Sha1 };
            if (!string.IsNullOrEmpty(match.Sha512))
            {
                hashes["sha512"] = match.Sha512;
            }

            files.Add(new Dictionary<string, object?>
            {
                ["path"] = match.Candidate.RelativePath,
                ["hashes"] = hashes,
                ["downloads"] = new[] { match.DownloadUrl },
                ["fileSize"] = match.Size
            });
        }

        var root = new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["game"] = "minecraft",
            ["versionId"] = options.Version,
            ["name"] = options.Name,
            ["summary"] = $"{options.Name} · Minecraft {meta.GameVersion}",
            ["files"] = files,
            ["dependencies"] = dependencies
        };
        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>CurseForge manifest.json：projectID / fileID / modLoaders 等字段大小写按官方规范。</summary>
    private static string BuildCurseForgeManifest(
        ModpackExportOptions options,
        PackMeta meta,
        IReadOnlyList<MatchedFile> matches)
    {
        var modLoaders = new List<object>();
        var prefix = meta.LoaderKind switch
        {
            ModpackLoaderKind.Forge => "forge",
            ModpackLoaderKind.NeoForge => "neoforge",
            ModpackLoaderKind.Fabric => "fabric",
            ModpackLoaderKind.Quilt => "quilt",
            _ => ""
        };
        if (prefix.Length > 0 && !string.IsNullOrEmpty(meta.LoaderVersion))
        {
            modLoaders.Add(new Dictionary<string, object?>
            {
                ["id"] = $"{prefix}-{meta.LoaderVersion}",
                ["primary"] = true
            });
        }

        var files = new List<object>();
        foreach (var match in matches)
        {
            if (!long.TryParse(match.ProjectId, out var projectId)
                || !long.TryParse(match.FileId, out var fileId))
            {
                continue;
            }

            files.Add(new Dictionary<string, object?>
            {
                ["projectID"] = projectId,
                ["fileID"] = fileId,
                ["required"] = true
            });
        }

        var root = new Dictionary<string, object?>
        {
            ["manifestType"] = "minecraftModpack",
            ["manifestVersion"] = 1,
            ["name"] = options.Name,
            ["version"] = options.Version,
            ["author"] = options.Author,
            ["minecraft"] = new Dictionary<string, object?>
            {
                ["version"] = meta.GameVersion,
                ["modLoaders"] = modLoaders
            },
            ["files"] = files,
            ["overrides"] = "overrides"
        };
        return JsonSerializer.Serialize(root, JsonOptions);
    }

    /// <summary>mmc-pack.json：组件 uid 与导入侧 ParseMmc 的映射完全一致。</summary>
    private static string BuildMmcPack(PackMeta meta)
    {
        var components = new List<object>
        {
            new Dictionary<string, object?>
            {
                ["uid"] = "net.minecraft",
                ["version"] = meta.GameVersion,
                ["important"] = true
            }
        };
        if (!string.IsNullOrEmpty(meta.LoaderVersion))
        {
            var uid = meta.LoaderKind switch
            {
                ModpackLoaderKind.Forge => "net.minecraftforge",
                ModpackLoaderKind.Cleanroom => "net.minecraftforge",
                ModpackLoaderKind.NeoForge => "net.neoforged",
                ModpackLoaderKind.Fabric => "net.fabricmc.fabric-loader",
                ModpackLoaderKind.Quilt => "org.quiltmc.quilt-loader",
                _ => ""
            };
            if (uid.Length > 0)
            {
                components.Add(new Dictionary<string, object?>
                {
                    ["uid"] = uid,
                    ["version"] = meta.LoaderVersion,
                    ["important"] = true
                });
            }
        }

        // Prism 的 Fabric / Quilt 实例还需要 Java 中间映射组件，否则启动时无法解析原版类名
        if (meta.LoaderKind is ModpackLoaderKind.Fabric or ModpackLoaderKind.Quilt)
        {
            components.Add(new Dictionary<string, object?>
            {
                ["uid"] = "net.fabricmc.intermediary",
                ["version"] = meta.GameVersion,
                ["important"] = false
            });
        }

        var root = new Dictionary<string, object?>
        {
            ["formatVersion"] = 1,
            ["components"] = components
        };
        return JsonSerializer.Serialize(root, JsonOptions);
    }

    // ------------------------- 收集内容 -------------------------

    private sealed class Candidate(string sourcePath, string relativePath, bool lookupEligible)
    {
        public string SourcePath { get; } = sourcePath;

        public string RelativePath { get; } = relativePath;

        public bool LookupEligible { get; set; } = lookupEligible;
    }

    private sealed class MatchedFile
    {
        public required Candidate Candidate { get; init; }

        public string? ProjectId { get; init; }

        public string? FileId { get; init; }

        public string? DownloadUrl { get; init; }

        public string? Sha1 { get; init; }

        public string? Sha512 { get; init; }

        public long Size { get; init; }
    }

    private sealed record PackMeta(string GameVersion, string LoaderKind, string? LoaderVersion);

    private IReadOnlyList<Candidate> CollectFiles(
        string contentRoot,
        ModpackExportOptions options,
        CancellationToken cancellationToken)
    {
        var files = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        void Add(string sourcePath, string relativePath, bool lookupEligible)
        {
            var normalized = relativePath.Replace('\\', '/').TrimStart('/');
            if (normalized.Length == 0 || normalized.Contains("..", StringComparison.Ordinal))
            {
                return;
            }

            var existing = files.GetValueOrDefault(normalized);
            if (existing is null)
            {
                files[normalized] = new Candidate(sourcePath, normalized, lookupEligible);
            }
            else if (lookupEligible)
            {
                // 同一文件既属于存档又属于数据包时，保留“可在线还原”的属性
                existing.LookupEligible = true;
            }
        }

        void AddTree(
            string directory,
            string prefix,
            bool lookupEligible,
            Func<string, bool>? exclude = null)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
                if (exclude is not null && exclude(relative))
                {
                    continue;
                }

                Add(file, $"{prefix}/{relative}", lookupEligible);
            }
        }

        if (options.IncludeMods)
        {
            foreach (var file in EnumerateTopFiles(Path.Combine(contentRoot, "mods")))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                Add(file, $"mods/{name}", enabled && name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase));
            }
        }

        if (options.IncludeResourcePacks)
        {
            AddPackFolder(
                Path.Combine(contentRoot, "resourcepacks"),
                "resourcepacks",
                Add,
                options.SelectedResourcePacks);
        }

        if (options.IncludeShaderPacks)
        {
            AddPackFolder(Path.Combine(contentRoot, "shaderpacks"), "shaderpacks", Add);
        }

        if (options.IncludeDataPacks)
        {
            var savesRoot = Path.Combine(contentRoot, "saves");
            if (Directory.Exists(savesRoot))
            {
                foreach (var world in Directory.EnumerateDirectories(savesRoot))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var datapacks = Path.Combine(world, "datapacks");
                    if (!Directory.Exists(datapacks))
                    {
                        continue;
                    }

                    var worldName = Path.GetFileName(world);
                    foreach (var file in Directory.EnumerateFiles(datapacks, "*", SearchOption.AllDirectories))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var name = Path.GetFileName(file);
                        var relative = Path.GetRelativePath(datapacks, file).Replace('\\', '/');
                        var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                        Add(file, $"saves/{worldName}/datapacks/{relative}",
                            enabled && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
        }

        if (options.IncludeModConfig)
        {
            // 个人数据（JEI / 帕秋莉 / 地图）挂在模组分组的子项上，未勾选时不能混进「模组设置」
            AddTree(Path.Combine(contentRoot, "config"), "config", false, IsPersonalConfigEntry);
            foreach (var name in new[] { "options.txt", "optionsof.txt" })
            {
                var file = Path.Combine(contentRoot, name);
                if (File.Exists(file))
                {
                    Add(file, name, false);
                }
            }
        }

        if (options.IncludePackData)
        {
            AddTree(Path.Combine(contentRoot, "defaultconfigs"), "defaultconfigs", false);
            AddTree(Path.Combine(contentRoot, "scripts"), "scripts", false);
            AddTree(Path.Combine(contentRoot, "kubejs"), "kubejs", false);
        }

        if (options.IncludeMaps)
        {
            foreach (var name in new[] { "XaeroWorldMap", "xaero", "journeymap", "mapwriter" })
            {
                AddTree(Path.Combine(contentRoot, name), name, false);
            }

            var configDirectory = Path.Combine(contentRoot, "config");
            if (Directory.Exists(configDirectory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(configDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(entry);
                    if (!IsMapEntry(name))
                    {
                        continue;
                    }

                    if (Directory.Exists(entry))
                    {
                        AddTree(entry, $"config/{name}", false);
                    }
                    else
                    {
                        Add(entry, $"config/{name}", false);
                    }
                }
            }
        }

        if (options.IncludeJei)
        {
            AddTree(Path.Combine(contentRoot, "config", "jei"), "config/jei", false);
        }

        if (options.IncludePatchouli)
        {
            AddTree(Path.Combine(contentRoot, "patchouli_books"), "patchouli_books", false);
            AddTree(Path.Combine(contentRoot, "config", "patchouli"), "config/patchouli", false);
            AddTree(Path.Combine(contentRoot, "config", "patchouli_books"), "config/patchouli_books", false);
        }

        if (options.IncludeSaves)
        {
            AddTree(Path.Combine(contentRoot, "saves"), "saves", false);
        }

        if (options.IncludeScreenshots)
        {
            AddTree(Path.Combine(contentRoot, "screenshots"), "screenshots", false);
        }

        if (options.IncludeSchematics)
        {
            AddTree(Path.Combine(contentRoot, "schematics"), "schematics", false);
        }

        if (options.IncludeServers)
        {
            var file = Path.Combine(contentRoot, "servers.dat");
            if (File.Exists(file))
            {
                Add(file, "servers.dat", false);
            }
        }

        return files.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();

        static IEnumerable<string> EnumerateTopFiles(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return [];
            }

            return Directory.EnumerateFiles(directory);
        }

        static bool IsMapEntry(string name)
            => name.StartsWith("xaero", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("journeymap", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("mapwriter", StringComparison.OrdinalIgnoreCase);

        static bool IsPersonalConfigEntry(string relative)
        {
            var first = relative.Split('/')[0];
            return first.Equals("jei", StringComparison.OrdinalIgnoreCase)
                   || first.StartsWith("patchouli", StringComparison.OrdinalIgnoreCase)
                   || IsMapEntry(first);
        }

        void AddPackFolder(
            string directory,
            string prefix,
            Action<string, string, bool> add,
            IReadOnlySet<string>? selected = null)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            bool IsSelected(string name) => selected is null || selected.Contains(name);

            foreach (var file in EnumerateTopFiles(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (!IsSelected(name))
                {
                    continue;
                }

                var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                add(file, $"{prefix}/{name}", enabled && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            }

            foreach (var folder in Directory.EnumerateDirectories(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = Path.GetFileName(folder);
                if (!IsSelected(name))
                {
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
                    add(file, $"{prefix}/{name}/{relative}", false);
                }
            }
        }
    }

    private async Task<PackMeta> ReadPackMetaAsync(MinecraftInstance instance, CancellationToken cancellationToken)
    {
        var gameVersion = instance.GameVersion;
        var loaderKind = ModpackLoaderKind.Vanilla;
        string? loaderVersion = null;
        // instance.Directory = <游戏目录>/versions/<实例 Id>，向上两级才是 ResolveAsync 需要的游戏目录
        var versionsDirectory = Path.GetDirectoryName(instance.Directory);
        var gameDirectory = versionsDirectory is null ? null : Path.GetDirectoryName(versionsDirectory);
        if (!string.IsNullOrEmpty(gameDirectory))
        {
            var resolved = await versionResolver
                .ResolveAsync(gameDirectory, instance.Id, cancellationToken)
                .ConfigureAwait(false);
            if (resolved is not null)
            {
                if (string.IsNullOrWhiteSpace(gameVersion))
                {
                    gameVersion = resolved.RootId;
                }

                // 部分加载器版本 JSON 不写 inheritsFrom（如 Fabric 自建实例），
                // 此时 RootId 就是实例名，需从实例名里找出真正的原版版本目录。
                if (instance.Loader != "原版"
                    && gameVersion.Equals(instance.Id, StringComparison.OrdinalIgnoreCase))
                {
                    gameVersion = DeriveGameVersion(instance.Id, versionsDirectory, gameVersion);
                }

                (loaderKind, loaderVersion) = ModpackReader.DetectLoaderFromLibraries(
                    resolved.Info.Libraries.Select(library => library.Name),
                    gameVersion);

                // 新版本 NeoForge / Forge 的版本 JSON 不再带 net.neoforged:neoforge 库，
                // 加载器信息只写在版本 id（neoforge-26.3.0.101）或 --fml.*Version 参数里，
                // 这里按实例记录的加载器类型补一次，避免清单漏掉加载器字段。
                if (instance.Loader != "原版")
                {
                    var raw = await versionResolver
                        .LoadAsync(versionsDirectory!, instance.Id, cancellationToken)
                        .ConfigureAwait(false);
                    var fallback = DetectLoaderFromInstance(
                        instance.Loader,
                        raw?.Id,
                        raw?.Arguments?.Game.Select(entry => entry.Values).ToList(),
                        gameVersion);
                    if (fallback.Kind.Length > 0)
                    {
                        loaderKind = fallback.Kind;
                        loaderVersion ??= fallback.Version;
                    }
                }
            }
        }

        return new PackMeta(gameVersion, loaderKind, loaderVersion);
    }

    /// <summary>
    /// 从实例名中挑出真实的原版版本：按 "-" 切分后枚举连续片段，取 versions 目录下存在的最长片段。
    /// 例如 26.3-fabric 或 fabric-loader-0.20.1-26.3 都能得到 26.3。
    /// </summary>
    private static string DeriveGameVersion(string instanceId, string? versionsDirectory, string fallback)
    {
        if (versionsDirectory is null || !Directory.Exists(versionsDirectory))
        {
            return fallback;
        }

        var tokens = instanceId.Split('-', StringSplitOptions.RemoveEmptyEntries);
        var matches = new List<string>();
        for (var start = 0; start < tokens.Length; start++)
        {
            for (var end = start + 1; end <= tokens.Length; end++)
            {
                var candidate = string.Join('-', tokens[start..end]);
                if (candidate.Equals(instanceId, StringComparison.OrdinalIgnoreCase)
                    || !char.IsAsciiDigit(candidate[0])
                    || !candidate.Contains('.'))
                {
                    continue;
                }

                if (Directory.Exists(Path.Combine(versionsDirectory, candidate)))
                {
                    matches.Add(candidate);
                }
            }
        }

        return matches.OrderByDescending(value => value.Length).FirstOrDefault() ?? fallback;
    }

    private static (string Kind, string? Version) DetectLoaderFromInstance(
        string instanceLoader,
        string? versionJsonId,
        IReadOnlyList<string[]>? gameArguments,
        string gameVersion)
    {
        var kind = instanceLoader switch
        {
            "Forge" => ModpackLoaderKind.Forge,
            "NeoForge" => ModpackLoaderKind.NeoForge,
            "Fabric" => ModpackLoaderKind.Fabric,
            "Quilt" => ModpackLoaderKind.Quilt,
            "Cleanroom" => ModpackLoaderKind.Cleanroom,
            _ => ""
        };
        if (kind.Length == 0)
        {
            return ("", null);
        }

        var version = ExtractLoaderVersion(versionJsonId, kind, gameVersion)
                      ?? ReadFmlArgumentVersion(gameArguments, kind);
        return (kind, version);
    }

    private static string? ExtractLoaderVersion(string? versionId, string kind, string gameVersion)
    {
        var prefixes = kind switch
        {
            ModpackLoaderKind.NeoForge => new[] { "neoforge-" },
            ModpackLoaderKind.Forge => new[] { "forge-" },
            ModpackLoaderKind.Fabric => new[] { "fabric-loader-", "fabric-" },
            ModpackLoaderKind.Quilt => new[] { "quilt-loader-", "quilt-" },
            ModpackLoaderKind.Cleanroom => new[] { "cleanroom-" },
            _ => []
        };
        if (string.IsNullOrWhiteSpace(versionId))
        {
            return null;
        }

        foreach (var prefix in prefixes)
        {
            if (!versionId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = versionId[prefix.Length..];
            var suffix = "-" + gameVersion;
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length];
            }

            return value.Trim();
        }

        return null;
    }

    private static string? ReadFmlArgumentVersion(IReadOnlyList<string[]>? gameArguments, string kind)
    {
        var key = kind switch
        {
            ModpackLoaderKind.NeoForge => "--fml.neoForgeVersion",
            ModpackLoaderKind.Forge => "--fml.forgeVersion",
            _ => null
        };
        if (key is null || gameArguments is null)
        {
            return null;
        }

        foreach (var values in gameArguments)
        {
            for (var index = 0; index + 1 < values.Length; index++)
            {
                if (values[index].Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return values[index + 1];
                }
            }
        }

        return null;
    }

    // ------------------------- 打包工具 -------------------------

    private static async Task WriteTextEntryAsync(
        ZipArchive archive,
        string entryName,
        string content,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await using var writer = new StreamWriter(output, new UTF8Encoding(false));
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteFileEntryAsync(
        ZipArchive archive,
        string entryName,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(entryName.Replace('\\', '/'), CompressionLevel.Optimal);
        await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var output = entry.Open();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
    }

    private static void Report(
        IProgress<ModpackExportProgress>? progress,
        string stage,
        int done,
        int total,
        double percent)
        => progress?.Report(new ModpackExportProgress(stage, done, total, Math.Clamp(percent, 0, 100)));

    private static string FormatName(ModpackExportFormat format)
        => format switch
        {
            ModpackExportFormat.Modrinth => "Modrinth（.mrpack）",
            ModpackExportFormat.CurseForge => "CurseForge（manifest.json）",
            _ => "MultiMC / Prism"
        };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
