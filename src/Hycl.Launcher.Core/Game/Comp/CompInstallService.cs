using System.IO.Compression;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Loaders;
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
    private readonly LoaderProviderRegistry _loaderProviders;
    private readonly LauncherLogger _logger;

    public CompInstallService(
        CompRequester requester,
        CurseForgeClient curseForge,
        InstanceManager instances,
        LoaderInstallService loaderInstall,
        LoaderProviderRegistry loaderProviders,
        LauncherLogger logger)
    {
        _requester = requester;
        _curseForge = curseForge;
        _instances = instances;
        _loaderInstall = loaderInstall;
        _loaderProviders = loaderProviders;
        _logger = logger;
    }

    /// <summary>
    /// 资源类型在内容根目录下的落盘子目录：
    /// 数据包落到 datapacks（游戏读取数据包的位置，隔离时对该实例全部存档生效，否则对全部实例存档生效），
    /// 世界落到 saves。
    /// </summary>
    public static string GetSubFolder(CompResourceType type)
        => type switch
        {
            CompResourceType.Mod => "mods",
            CompResourceType.ResourcePack => "resourcepacks",
            CompResourceType.Shader => "shaderpacks",
            CompResourceType.DataPack => "datapacks",
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

        // 隔离时落实例目录，否则落游戏根目录：数据包 → datapacks，世界 → saves
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
    /// 从社区资源下载并安装整合包：先下载包文件到临时目录，再走与本地导入完全相同的安装链路。
    /// 返回实际创建的实例名。
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
        var reporter = new CompProgress(progress);
        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"hycl-modpack-{Guid.NewGuid():N}{Path.GetExtension(file.FileName)}");
        reporter.Stage("下载整合包");
        reporter.BeginFile(file.FileName, file.Size);
        try
        {
            await _requester.ClientDownloadAsync(
                    file.DownloadUrls,
                    tempPath,
                    file.Size > 0 ? file.Size : null,
                    file.Hash,
                    new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileCompleted(file.Size);

            return await ImportPackCoreAsync(
                    tempPath,
                    gameDirectory,
                    instanceName,
                    policy,
                    threads,
                    reporter,
                    cancellationToken,
                    downloadAssets,
                    depth: 0)
                .ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>
    /// 识别本地整合包（压缩包或已解压文件夹）。识别与解析统一由 <see cref="ModpackReader"/> 提供，
    /// 导入与后续导出共用同一套实现。
    /// </summary>
    public static ModpackInspection InspectModpack(string sourcePath)
        => ModpackReader.InspectModpack(sourcePath);

    /// <summary>
    /// 从本地压缩包/文件夹导入整合包（对应 PCL-CE 的"导入整合包"入口）：
    /// 新建实例 → 安装原版与加载器 → 解压 overrides → 逐项下载清单内容。
    /// 同名实例已存在时自动追加序号；失败或取消会整体回滚本次新建的实例目录。
    /// </summary>
    public async Task<string> ImportModpackAsync(
        string sourcePath,
        string gameDirectory,
        string instanceName,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool downloadAssets = true)
    {
        var reporter = new CompProgress(progress);
        string? tempZip = null;
        try
        {
            var packPath = sourcePath;
            if (Directory.Exists(sourcePath))
            {
                // 先识别再打包：完整客户端包（含 versions 目录）直接拒绝，不必把整个 .minecraft 压一遍
                var folderInspection = InspectModpack(sourcePath);
                if (!folderInspection.IsSupported)
                {
                    throw new CompException(folderInspection.UnsupportedMessage);
                }

                reporter.Stage("整理文件夹");
                tempZip = Path.Combine(Path.GetTempPath(), $"hycl-modpack-{Guid.NewGuid():N}.zip");
                ZipFile.CreateFromDirectory(sourcePath, tempZip, CompressionLevel.Fastest, false);
                packPath = tempZip;
            }

            return await ImportPackCoreAsync(
                    packPath,
                    gameDirectory,
                    instanceName,
                    policy,
                    threads,
                    reporter,
                    cancellationToken,
                    downloadAssets,
                    depth: 0)
                .ConfigureAwait(false);
        }
        finally
        {
            if (tempZip is not null)
            {
                TryDeleteFile(tempZip);
            }
        }
    }

    /// <summary>
    /// 整合包安装核心：新建实例 → 安装原版与加载器 → 解压 overrides → 逐项下载内容。
    /// 本地导入与社区资源下载共用同一条链路；失败/取消回滚本次新建的实例目录。
    /// </summary>
    private async Task<string> ImportPackCoreAsync(
        string packPath,
        string gameDirectory,
        string desiredName,
        DownloadPolicy policy,
        int threads,
        CompProgress reporter,
        CancellationToken cancellationToken,
        bool downloadAssets,
        int depth)
    {
        var inspection = InspectModpack(packPath);
        if (!inspection.IsSupported)
        {
            throw new CompException(inspection.UnsupportedMessage);
        }

        // 启动器自打包：取出内层 modpack.zip / modpack.mrpack，按内层真实格式继续安装
        if (inspection.Kind == "launcher-pack")
        {
            if (depth >= 2)
            {
                throw new CompException("嵌套整合包层级过深（最多 2 层）。");
            }

            var nested = inspection.NestedEntry
                         ?? throw new CompException("启动器打包缺少内层整合包文件");
            var innerPath = Path.Combine(
                Path.GetTempPath(),
                $"hycl-modpack-inner-{Guid.NewGuid():N}{Path.GetExtension(nested)}");
            reporter.Stage("解压启动器打包");
            try
            {
                await ModpackReader.ExtractNestedAsync(packPath, nested, innerPath, cancellationToken)
                    .ConfigureAwait(false);
                return await ImportPackCoreAsync(
                        innerPath, gameDirectory, desiredName, policy, threads, reporter,
                        cancellationToken, downloadAssets, depth + 1)
                    .ConfigureAwait(false);
            }
            finally
            {
                TryDeleteFile(innerPath);
            }
        }

        var definition = ModpackReader.ParseModpack(packPath);

        var nameError = InstanceNameValidator.ValidateName(desiredName);
        if (nameError is not null)
        {
            throw new CompException(nameError);
        }

        // 同名实例已存在时自动追加序号（26.3 → 26.3-1 …），与安装新版本命名语义一致
        var instanceName = InstanceNameValidator.GetAvailableName(gameDirectory, desiredName);
        if (!instanceName.Equals(desiredName, StringComparison.Ordinal))
        {
            _logger.Info($"整合包实例名 {desiredName} 已存在，改用 {instanceName}");
        }

        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);
        // 回滚标记：目录是本次新建的才回滚，避免误删已存在的实例
        var createdInstanceDirectory = !Directory.Exists(instanceDirectory);
        try
        {
            // 1) 新建实例并安装原版 + 加载器（加载器安装会写 versions/<实例名>/<实例名>.json）
            var plan = await BuildInstallPlanAsync(
                    definition, gameDirectory, instanceName, policy, threads, downloadAssets, cancellationToken)
                .ConfigureAwait(false);
            reporter.Stage(definition.LoaderKind == ModpackLoaderKind.Vanilla
                ? $"安装原版 {definition.GameVersion}"
                : $"安装原版与 {LoaderDisplay(definition)}");
            await _loaderInstall.InstallAsync(plan, reporter, cancellationToken).ConfigureAwait(false);

            // 2) overrides 解压到实例目录
            reporter.Stage("解压整合包内容");
            await ExtractOverridesAsync(packPath, definition, instanceDirectory, cancellationToken)
                .ConfigureAwait(false);

            // 3) 清单文件逐项下载到 mods / resourcepacks / shaderpacks / saves 等目录
            await InstallPackFilesAsync(definition, instanceDirectory, reporter, cancellationToken)
                .ConfigureAwait(false);

            reporter.Stage("安装完成");
            _logger.Info($"整合包安装完成：{instanceName}（{definition.FormatName}，MC {definition.GameVersion}）");
            return instanceName;
        }
        catch
        {
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

    /// <summary>按清单声明的原版 / 加载器 / 附属 OptiFine 组装安装计划。</summary>
    private async Task<LoaderInstallPlan> BuildInstallPlanAsync(
        ModpackDefinition definition,
        string gameDirectory,
        string instanceName,
        DownloadPolicy policy,
        int threads,
        bool downloadAssets,
        CancellationToken cancellationToken)
    {
        var kind = definition.LoaderKind;
        OptiFineEntry? optiFine = null;
        if (!string.IsNullOrWhiteSpace(definition.OptiFineVersion))
        {
            var versions = await _loaderProviders.OptiFine
                .GetVersionsForAsync(definition.GameVersion, policy, cancellationToken)
                .ConfigureAwait(false);
            var wanted = definition.OptiFineVersion!;
            var selected = versions.FirstOrDefault(v =>
                    v.VersionName.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                ?? versions.FirstOrDefault(v =>
                    v.VersionName.EndsWith(wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new CompException(
                    $"整合包声明的 OptiFine {wanted} 在 MC {definition.GameVersion} 下不可用");
            optiFine = selected.Payload as OptiFineEntry
                       ?? throw new CompException("OptiFine 版本条目缺少安装载荷");
            if (kind == ModpackLoaderKind.Vanilla)
            {
                kind = "optifine";
            }
        }

        return new LoaderInstallPlan
        {
            Kind = kind,
            GameDirectory = gameDirectory,
            GameVersion = definition.GameVersion,
            TargetName = instanceName,
            LoaderVersion = definition.LoaderVersion,
            OptiFine = optiFine,
            Policy = policy,
            Threads = threads,
            DownloadAssets = downloadAssets,
            VersionIsolation = true
        };
    }

    private static string LoaderDisplay(ModpackDefinition definition)
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
        return string.IsNullOrWhiteSpace(definition.LoaderVersion)
            ? name
            : $"{name} {definition.LoaderVersion}";
    }

    /// <summary>逐项下载清单内容：Modrinth 直链按 path 落盘；CurseForge 按文件元数据判定目录。</summary>
    private async Task InstallPackFilesAsync(
        ModpackDefinition definition,
        string instanceDirectory,
        CompProgress reporter,
        CancellationToken cancellationToken)
    {
        if (definition.Files.Count > 0)
        {
            reporter.Stage("下载整合包资源", definition.Files.Count);
            foreach (var file in definition.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = ResolveInstancePath(instanceDirectory, file.Path)
                                  ?? throw new CompException($"整合包文件路径非法：{file.Path}");
                var urls = file.Urls
                    .SelectMany(url => DownloadSources.GetModDownloadUrls(url, _requester.Solution))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (urls.Count == 0)
                {
                    _logger.Warn($"整合包文件缺少下载地址，跳过：{file.Path}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? instanceDirectory);
                reporter.BeginFile(Path.GetFileName(destination), file.Size);
                await _requester.ClientDownloadAsync(
                        urls, destination, file.Size > 0 ? file.Size : null, file.Sha1,
                        new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                        cancellationToken)
                    .ConfigureAwait(false);
                reporter.FileCompleted(file.Size);
            }
        }

        var curseForgeIds = definition.CurseForgeFileIds
            .Concat(definition.OptionalCurseForgeFileIds)
            .Distinct()
            .ToList();
        if (curseForgeIds.Count == 0)
        {
            return;
        }

        var metadata = await _curseForge.GetFilesByIdsAsync(curseForgeIds, cancellationToken).ConfigureAwait(false);
        var metadataById = metadata.ToDictionary(file => long.Parse(file.Id), file => file);
        reporter.Stage("下载整合包资源", curseForgeIds.Count);
        foreach (var fileId in curseForgeIds)
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
            var fileName = SanitizeFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                _logger.Warn($"整合包文件 {fileId} 文件名无效，跳过");
                continue;
            }

            var destination = Path.Combine(destinationDirectory, fileName);
            reporter.BeginFile(fileName, file.Size);
            await _requester.ClientDownloadAsync(
                    file.DownloadUrls, destination, file.Size > 0 ? file.Size : null, file.Hash,
                    new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileCompleted(file.Size);
        }
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

    /// <summary>解压整合包 overrides 目录到实例目录（跳过清单文件本身与指定排除项）。</summary>
    private static Task ExtractOverridesAsync(
        string packPath,
        ModpackDefinition definition,
        string instanceDirectory,
        CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                using var archive = ZipFile.OpenRead(packPath);
                var excluded = definition.ExcludedEntries.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var fullName = entry.FullName.Replace('\\', '/');
                    if (fullName.EndsWith('/') || excluded.Contains(fullName))
                    {
                        continue;
                    }

                    string? relative = null;
                    foreach (var root in definition.OverridePrefixes)
                    {
                        if (root.Length == 0)
                        {
                            relative = fullName;
                            break;
                        }

                        if (fullName.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            relative = fullName[root.Length..];
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(relative))
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
            },
            cancellationToken);

    /// <summary>解析实例内相对路径；检测并拒绝路径逃逸。</summary>
    private static string? ResolveInstancePath(string instanceDirectory, string relativePath)
    {
        var root = Path.GetFullPath(instanceDirectory);
        var destination = Path.GetFullPath(
            Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return destination.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || destination.Equals(root, StringComparison.OrdinalIgnoreCase)
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
