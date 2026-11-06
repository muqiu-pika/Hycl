using System.IO.Compression;
using System.Text.Json;
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

            var inspection = InspectModpack(packPath);
            var gameVersion = await InstallModpackArchiveAsync(
                    packPath, inspection, gameDirectory, instanceName, reporter,
                    policy, threads, cancellationToken, downloadAssets)
                .ConfigureAwait(false);

            TryDeleteFile(packPath);
            reporter.Stage("安装完成");
            _logger.Info($"整合包安装完成：{instanceName}（{inspection.Kind}，MC {gameVersion}）");
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

    /// <summary>
    /// 整合包识别结果（对应 PCL-CE ModpackType）。
    /// <see cref="BaseFolder"/> 是清单在压缩包内所在目录（含末尾 /，根目录为空），
    /// 用于兼容"压缩包里还套一层文件夹"的常见打包方式。
    /// </summary>
    public sealed record ModpackInspection(string Kind, string FormatName, string? SuggestedName, string BaseFolder)
    {
        /// <summary>当前实现能直接安装的格式。</summary>
        public bool IsSupported => Kind is "modrinth" or "curseforge";
    }

    /// <summary>
    /// 识别本地整合包（压缩包或已解压文件夹）。
    /// 支持判断 PCL-CE 的六种格式，但只有 Modrinth / CurseForge 有安装实现。
    /// </summary>
    public static ModpackInspection InspectModpack(string sourcePath)
    {
        if (Directory.Exists(sourcePath))
        {
            var folderName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var rootKind = InspectFolderManifest(sourcePath, out var rootName, out var hasAddons);
            return rootKind switch
            {
                "modrinth" => new ModpackInspection("modrinth", "Modrinth（modrinth.index.json）", rootName ?? folderName, ""),
                "manifest" => hasAddons
                    ? new ModpackInspection("mcbbs", "MCBBS（manifest.json + addons）", rootName ?? folderName, "")
                    : new ModpackInspection("curseforge", "CurseForge（manifest.json）", rootName ?? folderName, ""),
                "hmcl" => new ModpackInspection("hmcl", "HMCL（modpack.json）", rootName ?? folderName, ""),
                "mmc" => new ModpackInspection("mmc", "MultiMC（mmc-pack.json）", rootName ?? folderName, ""),
                "mcbbs" => new ModpackInspection("mcbbs", "MCBBS（mcbbs.packmeta）", rootName ?? folderName, ""),
                _ => throw new CompException(
                    "该文件夹中没有可识别的整合包清单（modrinth.index.json / manifest.json 等），请确认选择的是已解压的整合包根目录。")
            };
        }

        if (!File.Exists(sourcePath))
        {
            throw new CompException($"整合包文件不存在：{sourcePath}");
        }

        if (Path.GetExtension(sourcePath).Equals(".rar", StringComparison.OrdinalIgnoreCase))
        {
            throw new CompException("暂不支持 RAR 格式，请先解压再用文件夹导入（PCL-CE 也需额外组件才能解压 RAR）。");
        }

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(sourcePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            throw new CompException("无法识别该整合包文件：既不是有效的 ZIP，也不是可识别的整合包格式。" + ex.Message);
        }

        using (archive)
        {
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            var entry = FindManifestEntry(archive, out var manifestName);
            if (entry is null)
            {
                return new ModpackInspection("unknown", "未识别的压缩包", fileName, "");
            }

            var baseFolder = entry.FullName[..(entry.FullName.Length - entry.Name.Length)];
            var suggested = ReadJsonName(archive, entry) ?? fileName;
            var hasAddons = false;
            if (manifestName == "manifest.json")
            {
                hasAddons = ReadManifestHasAddons(archive, entry);
            }

            var (kind, formatName) = manifestName switch
            {
                "modrinth.index.json" => ("modrinth", "Modrinth（modrinth.index.json）"),
                "manifest.json" => hasAddons
                    ? ("mcbbs", "MCBBS（manifest.json + addons）")
                    : ("curseforge", "CurseForge（manifest.json）"),
                "modpack.json" => ("hmcl", "HMCL（modpack.json）"),
                "mmc-pack.json" => ("mmc", "MultiMC（mmc-pack.json）"),
                "mcbbs.packmeta" => ("mcbbs", "MCBBS（mcbbs.packmeta）"),
                "modpack.zip" or "modpack.mrpack" => ("launcher-pack", "启动器整合包（嵌套 modpack.zip）"),
                _ => ("unknown", "未识别的压缩包")
            };
            return new ModpackInspection(kind, formatName, suggested, baseFolder);
        }
    }

    /// <summary>不支持的格式给出统一提示，列出当前实现与 PCL-CE 的差距。</summary>
    private static string UnsupportedModpackMessage(ModpackInspection inspection)
        => inspection.IsSupported
            ? ""
            : $"暂不支持该整合包格式：{inspection.FormatName}。" +
              "当前实现了 Modrinth（.mrpack / modrinth.index.json）与 CurseForge（manifest.json）；" +
              "HMCL / MultiMC / MCBBS / 启动器打包（嵌套 modpack.zip）尚无解析实现，" +
              "可解压后导入其中已展开的 overrides 目录。";

    /// <summary>从已解压的文件夹识别清单（只认根目录，和直接解压的整合包一致）。</summary>
    private static string InspectFolderManifest(string folder, out string? suggestedName, out bool hasAddons)
    {
        suggestedName = null;
        hasAddons = false;
        string? ReadName(string file)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(Path.Combine(folder, file)))?["name"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        if (File.Exists(Path.Combine(folder, "modrinth.index.json")))
        {
            suggestedName = ReadName("modrinth.index.json");
            return "modrinth";
        }

        if (File.Exists(Path.Combine(folder, "manifest.json")))
        {
            suggestedName = ReadName("manifest.json");
            hasAddons = File.Exists(Path.Combine(folder, "manifest.json"))
                        && ReadManifestHasAddons(Path.Combine(folder, "manifest.json"));
            return "manifest";
        }

        if (File.Exists(Path.Combine(folder, "modpack.json")))
        {
            suggestedName = ReadName("modpack.json");
            return "hmcl";
        }

        if (File.Exists(Path.Combine(folder, "mmc-pack.json")))
        {
            return "mmc";
        }

        if (File.Exists(Path.Combine(folder, "mcbbs.packmeta")))
        {
            return "mcbbs";
        }

        return "unknown";
    }

    /// <summary>在压缩包里找优先级最高的清单条目（同格式多层目录时取层数最浅的）。</summary>
    private static ZipArchiveEntry? FindManifestEntry(ZipArchive archive, out string manifestName)
    {
        string[] candidates =
        [
            "modrinth.index.json", "manifest.json", "modpack.json",
            "mmc-pack.json", "mcbbs.packmeta", "modpack.zip", "modpack.mrpack"
        ];
        foreach (var candidate in candidates)
        {
            var found = archive.Entries
                .Where(e => e.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.FullName.Count(c => c == '/'))
                .FirstOrDefault();
            if (found is not null)
            {
                manifestName = candidate;
                return found;
            }
        }

        manifestName = "";
        return null;
    }

    private static string? ReadJsonName(ZipArchive archive, ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var node = JsonNode.Parse(stream);
            return node?["name"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool ReadManifestHasAddons(ZipArchive archive, ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var node = JsonNode.Parse(stream);
            return node?["addons"] is JsonArray;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ReadManifestHasAddons(string path)
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node?["addons"] is JsonArray;
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 从本地压缩包/文件夹导入整合包（对应 PCL-CE 的"导入整合包"入口）。
    /// 已存在同名实例直接拒绝；安装失败会整体回滚本次新建的实例目录。
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
        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);
        if (Directory.Exists(instanceDirectory))
        {
            throw new CompException($"同名实例已存在：{instanceName}，请换一个名称或先删除旧实例。");
        }

        var reporter = new CompProgress(progress);
        string? tempZip = null;
        try
        {
            var packPath = sourcePath;
            ModpackInspection inspection;
            if (Directory.Exists(sourcePath))
            {
                reporter.Stage("整理文件夹");
                tempZip = Path.Combine(Path.GetTempPath(), $"hycl-modpack-{Guid.NewGuid():N}.zip");
                ZipFile.CreateFromDirectory(sourcePath, tempZip, CompressionLevel.Fastest, false);
                packPath = tempZip;
                inspection = InspectModpack(packPath);
            }
            else
            {
                inspection = InspectModpack(packPath);
            }

            if (!inspection.IsSupported)
            {
                throw new CompException(UnsupportedModpackMessage(inspection));
            }

            reporter.Stage("创建实例");
            Directory.CreateDirectory(instanceDirectory);
            var gameVersion = await InstallModpackArchiveAsync(
                    packPath, inspection, gameDirectory, instanceName, reporter,
                    policy, threads, cancellationToken, downloadAssets)
                .ConfigureAwait(false);

            reporter.Stage("安装完成");
            _logger.Info($"整合包导入完成：{instanceName}（{inspection.Kind}，MC {gameVersion}，来源 {sourcePath}）");
            return instanceName;
        }
        catch
        {
            // 导入只新建实例：失败/取消都整体回滚，不留半成品
            try
            {
                if (Directory.Exists(instanceDirectory))
                {
                    Directory.Delete(instanceDirectory, true);
                    _logger.Warn($"整合包导入未完成，已回滚实例目录：{instanceDirectory}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Error("回滚导入实例目录失败", ex);
            }

            throw;
        }
        finally
        {
            if (tempZip is not null)
            {
                TryDeleteFile(tempZip);
            }
        }
    }

    private Task<string> InstallModpackArchiveAsync(
        string packPath,
        ModpackInspection inspection,
        string gameDirectory,
        string instanceName,
        CompProgress reporter,
        DownloadPolicy policy,
        int threads,
        CancellationToken cancellationToken,
        bool downloadAssets)
        => inspection.Kind switch
        {
            "modrinth" => InstallModrinthPackAsync(
                packPath, inspection.BaseFolder, gameDirectory, instanceName, reporter,
                policy, threads, cancellationToken, downloadAssets),
            "curseforge" => InstallCurseForgePackAsync(
                packPath, inspection.BaseFolder, gameDirectory, instanceName, reporter,
                policy, threads, cancellationToken, downloadAssets),
            _ => throw new CompException(UnsupportedModpackMessage(inspection))
        };

    // ---------- Modrinth .mrpack ----------

    /// <summary>
    /// 安装 Modrinth 整合包（对应 PCL-CE InstallPackModrinth）：
    /// 解析 modrinth.index.json → 提取 overrides/client-overrides → 下载 files[] → 安装原版与加载器。
    /// </summary>
    private async Task<string> InstallModrinthPackAsync(
        string packPath,
        string baseFolder,
        string gameDirectory,
        string instanceName,
        CompProgress reporter,
        DownloadPolicy policy,
        int threads,
        CancellationToken cancellationToken,
        bool downloadAssets)
    {
        using var archive = ZipFile.OpenRead(packPath);
        var indexEntry = archive.GetEntry(baseFolder + "modrinth.index.json")
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
        ExtractOverrides(
            archive,
            new[] { baseFolder + "overrides", baseFolder + "client-overrides" },
            instanceDirectory,
            cancellationToken);

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
        string baseFolder,
        string gameDirectory,
        string instanceName,
        CompProgress reporter,
        DownloadPolicy policy,
        int threads,
        CancellationToken cancellationToken,
        bool downloadAssets)
    {
        using var archive = ZipFile.OpenRead(packPath);
        var manifestEntry = archive.GetEntry(baseFolder + "manifest.json")
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
        var overridesFolder = baseFolder + (manifest["overrides"]?.ToString() ?? "overrides");
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

        // 原版安装只会写 versions/<gameVersion>，不会用 TargetName。
        // 整合包实例名与游戏版本不同时补一个 inheritsFrom 的版本 JSON，否则实例不会出现在版本列表。
        if (loaderKind == "vanilla"
            && !string.Equals(instanceName, gameVersion, StringComparison.OrdinalIgnoreCase))
        {
            await WriteVanillaInheritJsonAsync(gameDirectory, instanceName, gameVersion, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private static async Task WriteVanillaInheritJsonAsync(
        string gameDirectory,
        string instanceName,
        string gameVersion,
        CancellationToken cancellationToken)
    {
        var instanceDirectory = Path.Combine(gameDirectory, "versions", instanceName);
        var jsonPath = Path.Combine(instanceDirectory, instanceName + ".json");
        if (File.Exists(jsonPath))
        {
            return;
        }

        Directory.CreateDirectory(instanceDirectory);
        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var json = new JsonObject
        {
            ["id"] = instanceName,
            ["inheritsFrom"] = gameVersion,
            ["type"] = "release",
            ["time"] = now,
            ["releaseTime"] = now
        };
        await File.WriteAllTextAsync(jsonPath, json.ToJsonString(), cancellationToken).ConfigureAwait(false);
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
