using System.IO.Compression;
using System.Text.Json.Nodes;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>
/// 社区资源安装服务（对应 PCL-CE Save_Click / QuickDownload / ModpackInstall）：
/// 按资源类型落盘到实例对应子目录；整合包解析 modrinth.index.json / CurseForge manifest.json
/// 并自动安装所需的原版与加载器。
/// </summary>
public sealed class CompInstallService
{
    private readonly CompRequester _requester;
    private readonly CurseForgeClient _curseForge;
    private readonly InstanceManager _instances;
    private readonly LoaderInstallService _loaderInstall;
    private readonly LauncherLogger _logger;

    public CompInstallService(
        CompRequester requester,
        CurseForgeClient curseForge,
        InstanceManager instances,
        LoaderInstallService loaderInstall,
        LauncherLogger logger)
    {
        _requester = requester;
        _curseForge = curseForge;
        _instances = instances;
        _loaderInstall = loaderInstall;
        _logger = logger;
    }

    /// <summary>
    /// 资源类型在内容根目录下的落盘子目录（对应 PCL-CE _GetSubFolder）：
    /// 数据包直接落到版本根目录（空子目录，与 PCL-CE 一致），世界落到 saves。
    /// </summary>
    public static string GetSubFolder(CompResourceType type)
        => type switch
        {
            CompResourceType.Mod => "mods",
            CompResourceType.ResourcePack => "resourcepacks",
            CompResourceType.Shader => "shaderpacks",
            CompResourceType.DataPack => "",
            CompResourceType.World => "saves",
            _ => ""
        };

    /// <summary>候选目标实例：优先与资源游戏版本兼容的最近实例，否则取最近修改的实例。</summary>
    public async Task<MinecraftInstance?> ResolveTargetAsync(
        string gameDirectory,
        CompFile file,
        CancellationToken cancellationToken = default)
    {
        var instances = await _instances.ScanAsync(gameDirectory, cancellationToken).ConfigureAwait(false);
        if (instances.Count == 0)
        {
            return null;
        }

        var compatible = instances
            .Where(instance => file.GameVersions.Count == 0
                               || file.GameVersions.Any(v => v.Equals(instance.GameVersion, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(instance => instance.LastWriteTime)
            .FirstOrDefault();
        return compatible ?? instances.OrderByDescending(instance => instance.LastWriteTime).First();
    }

    /// <summary>把资源文件安装进指定实例（内容根目录按版本隔离规则解析）。</summary>
    public async Task<string> InstallFileAsync(
        CompFile file,
        string gameDirectory,
        string instanceId,
        bool versionIsolation,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceId);
        if (!Directory.Exists(instanceDirectory))
        {
            throw new CompException($"目标实例不存在：{instanceId}");
        }

        // 隔离时落实例目录，否则落游戏根目录：数据包 → 版本根目录，世界 → saves
        var contentRoot = InstanceManager.GetContentRoot(gameDirectory, instanceId, versionIsolation);
        var subFolder = GetSubFolder(file.Type);
        var targetDirectory = string.IsNullOrEmpty(subFolder)
            ? contentRoot
            : Path.Combine(contentRoot, subFolder);
        return await InstallFileToDirectoryAsync(file, targetDirectory, progress, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>把资源文件下载到用户选择的目录（对应 PCL-CE Save_Click 的另存为）；目录不存在时自动创建。</summary>
    public async Task<string> InstallFileToDirectoryAsync(
        CompFile file,
        string targetDirectory,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(targetDirectory);
        if (File.Exists(directory))
        {
            throw new CompException($"保存位置是文件而不是文件夹：{directory}");
        }

        Directory.CreateDirectory(directory);
        var fileName = SanitizeFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new CompException("资源文件缺少有效的文件名");
        }

        var targetPath = Path.Combine(directory, fileName);
        var reporter = new CompProgress(progress);
        reporter.Stage($"下载 {file.DisplayName}");
        reporter.BeginFile(fileName, file.Size);
        await _requester.ClientDownloadAsync(
                file.DownloadUrls,
                targetPath,
                file.Size > 0 ? file.Size : null,
                file.Hash,
                new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                cancellationToken)
            .ConfigureAwait(false);
        reporter.FileCompleted(file.Size);

        // 世界以压缩包发布：下载完成后解压到 saves 目录并删除压缩包（对应 PCL-CE InstallWorld + CleanCache）
        if (file.Type == CompResourceType.World)
        {
            reporter.Stage("解压世界存档");
            ExtractWorld(targetPath, directory);
            TryDeleteFile(targetPath);
            targetPath = directory;
        }

        reporter.Stage("安装完成");
        _logger.Info($"社区资源已保存：{fileName} → {targetPath}");
        return targetPath;
    }

    /// <summary>解压世界压缩包；失败时回滚本次新建的文件/目录，避免留下半截存档。</summary>
    private static void ExtractWorld(string archivePath, string extractDirectory)
    {
        var root = Path.GetFullPath(extractDirectory);
        var created = new List<string>();
        try
        {
            using var archive = ZipFile.OpenRead(archivePath);
            if (archive.Entries.Count == 0)
            {
                throw new CompException("世界压缩包为空");
            }

            foreach (var entry in archive.Entries)
            {
                var destination = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !destination.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new CompException("世界压缩包包含非法路径");
                }

                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                {
                    if (!Directory.Exists(destination))
                    {
                        Directory.CreateDirectory(destination);
                        created.Add(destination);
                    }

                    continue;
                }

                var parent = Path.GetDirectoryName(destination);
                if (parent is not null && !Directory.Exists(parent))
                {
                    Directory.CreateDirectory(parent);
                    created.Add(parent);
                }

                if (!File.Exists(destination))
                {
                    created.Add(destination);
                }

                entry.ExtractToFile(destination, true);
            }
        }
        catch
        {
            // 回滚：先删文件再删目录，失败时不掩盖原始异常
            for (var index = created.Count - 1; index >= 0; index--)
            {
                var path = created[index];
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    else if (Directory.Exists(path))
                    {
                        Directory.Delete(path, true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 回滚尽力而为
                }
            }

            throw;
        }
    }

    // ---------- 整合包安装 ----------

    /// <summary>
    /// 安装整合包：下载包文件到 versions/{实例名}/，按类型分发
    /// （Modrinth .mrpack 与 CurseForge manifest 完整实现；MMC/HMCL 等格式给出明确错误——留待后续）。
    /// 返回实例名。
    /// </summary>
    public async Task<string> InstallModpackAsync(
        CompFile file,
        string gameDirectory,
        string instanceName,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool downloadAssets = true)
    {
        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);
        // 回滚标记：目录是本次新建的才回滚，避免误删已存在的实例
        var createdInstanceDirectory = !Directory.Exists(instanceDirectory);
        Directory.CreateDirectory(instanceDirectory);
        var packPath = Path.Combine(instanceDirectory, "原始整合包" + Path.GetExtension(file.FileName));

        var reporter = new CompProgress(progress);
        reporter.Stage("下载整合包");
        reporter.BeginFile(file.FileName, file.Size);
        try
        {
            await _requester.ClientDownloadAsync(
                    file.DownloadUrls,
                    packPath,
                    file.Size > 0 ? file.Size : null,
                    file.Hash,
                    new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileCompleted(file.Size);

            var modpackType = DetectModpackType(packPath);
            var gameVersion = modpackType switch
            {
                "modrinth" => await InstallModrinthPackAsync(
                        packPath, gameDirectory, instanceName, reporter, policy, threads, cancellationToken, downloadAssets)
                    .ConfigureAwait(false),
                "curseforge" => await InstallCurseForgePackAsync(
                        packPath, gameDirectory, instanceName, reporter, policy, threads, cancellationToken, downloadAssets)
                    .ConfigureAwait(false),
                _ => throw new CompException(
                    "暂不支持该整合包格式：当前支持 Modrinth（.mrpack）与 CurseForge（manifest.json）；" +
                    "MultiMC/HMCL 等格式可解压后将 overrides 导入 versions 目录手动使用。")
            };

            TryDeleteFile(packPath);
            reporter.Stage("安装完成");
            _logger.Info($"整合包安装完成：{instanceName}（{modpackType}，MC {gameVersion}）");
            return instanceName;
        }
        catch
        {
            // 取消/失败/格式不支持：清掉整合包临时文件；本次新建的实例目录整体回滚，
            // 不留半成品（overrides 已解压一半、 mods 只下了一部分等都属于无效状态）
            TryDeleteFile(packPath);
            if (createdInstanceDirectory && Directory.Exists(instanceDirectory))
            {
                try
                {
                    Directory.Delete(instanceDirectory, true);
                    _logger.Warn($"整合包安装未完成，已回滚实例目录：{instanceDirectory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Error("回滚整合包实例目录失败", ex);
                }
            }

            throw;
        }
    }

    /// <summary>识别整合包类型（对应 PCL-CE _DetectModpackType 的常用分支）。</summary>
    private static string DetectModpackType(string packPath)
    {
        using var archive = ZipFile.OpenRead(packPath);
        bool HasEntry(string name)
            => archive.GetEntry(name) is not null
               || archive.Entries.Any(e => e.FullName.EndsWith("/" + name, StringComparison.OrdinalIgnoreCase));
        if (HasEntry("modrinth.index.json"))
        {
            return "modrinth";
        }

        if (HasEntry("manifest.json"))
        {
            return "curseforge";
        }

        if (HasEntry("mcbbs.packmeta") || HasEntry("mmc-pack.json") || HasEntry("modpack.json"))
        {
            return "unsupported-mmc";
        }

        return "unknown";
    }

    // ---------- Modrinth .mrpack ----------

    /// <summary>
    /// 安装 Modrinth 整合包（对应 PCL-CE InstallPackModrinth）：
    /// 解析 modrinth.index.json → 提取 overrides/client-overrides → 下载 files[] → 安装原版与加载器。
    /// </summary>
    private async Task<string> InstallModrinthPackAsync(
        string packPath,
        string gameDirectory,
        string instanceName,
        CompProgress reporter,
        DownloadPolicy policy,
        int threads,
        CancellationToken cancellationToken,
        bool downloadAssets)
    {
        using var archive = ZipFile.OpenRead(packPath);
        var indexEntry = archive.GetEntry("modrinth.index.json")
                         ?? throw new CompException("整合包缺少 modrinth.index.json");
        string indexText;
        await using (var stream = indexEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            indexText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var index = JsonNode.Parse(indexText) as JsonObject
                    ?? throw new CompException("modrinth.index.json 解析失败");
        var dependencies = index["dependencies"] as JsonObject
                           ?? throw new CompException("整合包缺少 dependencies 声明");
        var gameVersion = dependencies["minecraft"]?.ToString()
                          ?? throw new CompException("整合包缺少 minecraft 版本声明");
        var forgeVersion = dependencies["forge"]?.ToString();
        var neoForgeVersion = (dependencies["neoforge"] ?? dependencies["neo-forge"])?.ToString();
        var fabricVersion = dependencies["fabric-loader"]?.ToString();
        if (dependencies["quilt-loader"] is not null)
        {
            throw new CompException("该整合包使用 Quilt 加载器：当前不支持 Quilt 整合包（与 PCL-CE 一致）");
        }

        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);

        // 1) 提取 overrides / client-overrides
        reporter.Stage("解压整合包内容");
        ExtractOverrides(archive, new[] { "overrides", "client-overrides" }, instanceDirectory, cancellationToken);

        // 2) 下载 files[]（env.client=unsupported 跳过，optional 一并安装）
        var files = index["files"] as JsonArray ?? [];
        reporter.Stage("下载整合包资源", files.Count);
        for (var i = 0; i < files.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (files[i] is not JsonObject fileJson)
            {
                continue;
            }

            var envClient = fileJson["env"]?["client"]?.ToString();
            if (envClient == "unsupported")
            {
                continue;
            }

            var path = fileJson["path"]?.ToString() ?? throw new CompException("整合包文件缺少 path");
            var destination = ResolveInstancePath(instanceDirectory, path);
            if (destination is null)
            {
                continue;
            }

            var urls = (fileJson["downloads"] as JsonArray ?? [])
                .Select(n => n?.ToString())
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .SelectMany(u => DownloadSources.GetModDownloadUrls(u!, _requester.Solution))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (urls.Count == 0)
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? instanceDirectory);
            var size = fileJson["fileSize"]?.GetValue<long>() ?? 0;
            var sha1 = fileJson["hashes"]?["sha1"]?.ToString();
            reporter.BeginFile(Path.GetFileName(destination), size);
            await _requester.ClientDownloadAsync(
                    urls, destination, size > 0 ? size : null, sha1,
                    new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileCompleted(size);
        }

        // 3) 安装原版 + 加载器（对应 PCL-CE McInstallLoader 合并阶段）
        await InstallBaseGameAsync(
                gameDirectory, instanceName, gameVersion,
                forgeVersion, neoForgeVersion, fabricVersion,
                policy, threads, reporter, cancellationToken, downloadAssets)
            .ConfigureAwait(false);
        return gameVersion;
    }

    // ---------- CurseForge manifest ----------

    /// <summary>
    /// 安装 CurseForge 整合包（对应 PCL-CE InstallPackCurseForge）：
    /// 解析 manifest.json → POST /v1/mods/files 批量取文件 → 按模块类型落盘 → 安装原版与加载器。
    /// </summary>
    private async Task<string> InstallCurseForgePackAsync(
        string packPath,
        string gameDirectory,
        string instanceName,
        CompProgress reporter,
        DownloadPolicy policy,
        int threads,
        CancellationToken cancellationToken,
        bool downloadAssets)
    {
        using var archive = ZipFile.OpenRead(packPath);
        var manifestEntry = archive.GetEntry("manifest.json")
                            ?? throw new CompException("整合包缺少 manifest.json");
        string manifestText;
        await using (var stream = manifestEntry.Open())
        using (var reader = new StreamReader(stream))
        {
            manifestText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }

        var manifest = JsonNode.Parse(manifestText) as JsonObject
                       ?? throw new CompException("manifest.json 解析失败");
        var gameVersion = manifest["minecraft"]?["version"]?.ToString()
                          ?? throw new CompException("整合包缺少 minecraft 版本声明");
        var overridesFolder = manifest["overrides"]?.ToString() ?? "overrides";
        string? forgeVersion = null;
        string? neoForgeVersion = null;
        string? fabricVersion = null;
        foreach (var loader in manifest["minecraft"]?["modLoaders"] as JsonArray ?? [])
        {
            var id = loader?["id"]?.ToString() ?? "";
            if (id.StartsWith("forge-", StringComparison.Ordinal))
            {
                forgeVersion = id["forge-".Length..];
            }
            else if (id.StartsWith("neoforge-", StringComparison.Ordinal))
            {
                neoForgeVersion = id["neoforge-".Length..];
            }
            else if (id.StartsWith("fabric-", StringComparison.Ordinal))
            {
                fabricVersion = id["fabric-".Length..];
            }
        }

        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);

        // 1) 提取 overrides（目录名由 manifest.overrides 指定，默认 overrides）
        reporter.Stage("解压整合包内容");
        ExtractOverrides(archive, [overridesFolder], instanceDirectory, cancellationToken);

        // 2) 批量获取文件元数据并按模块类型落盘
        var fileEntries = manifest["files"] as JsonArray ?? [];
        var fileIds = fileEntries
            .Select(f => f?["fileID"]?.GetValue<long>())
            .Where(id => id is not null)
            .Cast<long>()
            .ToList();
        var metadata = await _curseForge.GetFilesByIdsAsync(fileIds, cancellationToken).ConfigureAwait(false);
        var metadataById = metadata.ToDictionary(f => long.Parse(f.Id), f => f);
        reporter.Stage("下载整合包资源", fileIds.Count);
        foreach (var fileId in fileIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!metadataById.TryGetValue(fileId, out var file))
            {
                _logger.Warn($"整合包文件 {fileId} 已被 CurseForge 删除，跳过");
                continue;
            }

            var (subFolder, _) = ResolveCurseForgeTargetFolder(file);
            var destinationDirectory = string.IsNullOrEmpty(subFolder)
                ? instanceDirectory
                : Path.Combine(instanceDirectory, subFolder);
            Directory.CreateDirectory(destinationDirectory);
            var destination = Path.Combine(destinationDirectory, SanitizeFileName(file.FileName));
            reporter.BeginFile(file.FileName, file.Size);
            await _requester.ClientDownloadAsync(
                    file.DownloadUrls, destination, file.Size > 0 ? file.Size : null, file.Hash,
                    new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileCompleted(file.Size);
        }

        // 3) 安装原版 + 加载器
        await InstallBaseGameAsync(
                gameDirectory, instanceName, gameVersion,
                forgeVersion, neoForgeVersion, fabricVersion,
                policy, threads, reporter, cancellationToken, downloadAssets)
            .ConfigureAwait(false);
        return gameVersion;
    }

    /// <summary>按 jar 内模块名判定落盘目录（对应 PCL-CE _ResolveCurseForgeTargetFolder）。</summary>
    private static (string SubFolder, CompResourceType Type) ResolveCurseForgeTargetFolder(CompFile file)
    {
        var modules = file.ModuleNames;
        bool Has(string name) => modules.Any(m => m.Contains(name, StringComparison.OrdinalIgnoreCase));
        if (Has("META-INF") || Has("mcmod.info") || file.FileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
        {
            return ("mods", CompResourceType.Mod);
        }

        if (Has("pack.mcmeta"))
        {
            return ("resourcepacks", CompResourceType.ResourcePack);
        }

        if (Has("level.dat"))
        {
            return ("saves", CompResourceType.World);
        }

        return ("shaderpacks", CompResourceType.Shader);
    }

    /// <summary>解压整合包 overrides 目录到实例目录。</summary>
    private static void ExtractOverrides(
        ZipArchive archive,
        IReadOnlyList<string> roots,
        string instanceDirectory,
        CancellationToken cancellationToken)
    {
        foreach (var root in roots)
        {
            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.FullName.StartsWith(root + "/", StringComparison.Ordinal))
                {
                    continue;
                }

                var relative = entry.FullName[(root.Length + 1)..];
                if (string.IsNullOrEmpty(relative) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var destination = ResolveInstancePath(instanceDirectory, relative);
                if (destination is null)
                {
                    continue; // 路径逃逸防护（对应 PCL-CE 前缀校验）
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? instanceDirectory);
                entry.ExtractToFile(destination, true);
            }
        }
    }

    /// <summary>解析实例内相对路径；检测并拒绝路径逃逸。</summary>
    private static string? ResolveInstancePath(string instanceDirectory, string relativePath)
    {
        var destination = Path.GetFullPath(
            Path.Combine(instanceDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return destination.StartsWith(Path.GetFullPath(instanceDirectory), StringComparison.OrdinalIgnoreCase)
            ? destination
            : null;
    }

    /// <summary>
    /// 文件名消毒：去掉目录部分、拒绝 "…" 上级跳转与非法字符（资源名来自第三方 API，不可信任）。
    /// </summary>
    internal static string SanitizeFileName(string rawName)
    {
        var name = Path.GetFileName(rawName?.Trim());
        if (string.IsNullOrWhiteSpace(name) || name is "." or "..")
        {
            return "";
        }

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name.Length > 180 ? name[..180] : name;
    }

    /// <summary>安装整合包所需的原版与加载器（复用加载器安装门面）。</summary>
    private async Task InstallBaseGameAsync(
        string gameDirectory,
        string instanceName,
        string gameVersion,
        string? forgeVersion,
        string? neoForgeVersion,
        string? fabricVersion,
        DownloadPolicy policy,
        int threads,
        CompProgress reporter,
        CancellationToken cancellationToken,
        bool downloadAssets)
    {
        var loaderKind = forgeVersion is not null ? "forge"
            : neoForgeVersion is not null ? "neoforge"
            : fabricVersion is not null ? "fabric"
            : "vanilla";
        var plan = new LoaderInstallPlan
        {
            Kind = loaderKind,
            GameDirectory = gameDirectory,
            GameVersion = gameVersion,
            TargetName = instanceName,
            LoaderVersion = forgeVersion ?? neoForgeVersion ?? fabricVersion,
            Policy = policy,
            Threads = threads,
            DownloadAssets = downloadAssets
        };
        reporter.Stage(loaderKind == "vanilla" ? "安装原版" : $"安装 {loaderKind}");
        await _loaderInstall.InstallAsync(plan, reporter, cancellationToken).ConfigureAwait(false);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 清理失败可忽略
        }
    }
}
