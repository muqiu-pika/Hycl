using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Java;
using Hycl.Launcher.Core.Serialization;

namespace Hycl.Launcher.Core.Game;

public partial class VersionInstaller
{
    /// <summary>Forge 系安装请求。</summary>
    public sealed record ForgelikeInstallRequest
    {
        public required string GameDirectory { get; init; }

        /// <summary>继承的 Minecraft 版本。</summary>
        public required string GameVersion { get; init; }

        /// <summary>目标实例目录名。</summary>
        public required string TargetName { get; init; }

        /// <summary>加载器条目（ForgeEntry / NeoForgeEntry / CleanroomEntry）。</summary>
        public required ForgelikeEntry Entry { get; init; }

        public required DownloadPolicy Policy { get; init; }

        public int Threads { get; init; } = 16;

        public bool DownloadAssets { get; init; } = true;

        /// <summary>手动指定的 Java；空则自动探测。</summary>
        public string? JavaPath { get; init; }
    }

    /// <summary>
    /// 安装 Forge / NeoForge / Cleanroom：
    /// 新版（Forge ≥ 20、NeoForge ≥ 1.20.2、Cleanroom 全部）走 BMCLAPI headless 引导器；
    /// 旧版（Forge &lt; 1.13）与 NeoForge 1.20.1 走安装器解包（install_profile 方法一/二）。
    /// </summary>
    public async Task<string> InstallForgelikeAsync(
        ForgelikeInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = new ProgressReporter(progress);
        var entry = request.Entry;
        var loaderName = entry.Kind.ToString();
        _logger.Info($"开始安装 {loaderName} {entry.VersionName}（{request.GameVersion} → {request.TargetName}）");

        // 1) 确保原版就绪
        reporter.Stage("检查原版");
        var vanillaJsonPath = await EnsureVanillaAsync(
                request.GameDirectory,
                request.GameVersion,
                request.Policy,
                request.Threads,
                progress,
                cancellationToken,
                request.DownloadAssets)
            .ConfigureAwait(false);

        // 2) 下载安装器
        reporter.Stage($"下载 {loaderName} 安装器");
        var cacheDirectory = _cacheDirectory
                             ?? Path.Combine(Path.GetTempPath(), "hycl", "cache");
        var installerPath = Path.Combine(
            cacheDirectory,
            $"{loaderName.ToLowerInvariant()}-installer-{entry.VersionName.Replace('/', '_')}.jar");
        var installerUrls = entry switch
        {
            ForgeEntry forge => forge.GetInstallerUrls(),
            NeoForgeEntry neoForge => neoForge.GetInstallerUrls(),
            CleanroomEntry cleanroom => new[] { cleanroom.UrlBase + "-installer.jar" },
            _ => throw new ArgumentException($"未知的 Forge 系条目：{entry.GetType().Name}")
        };
        var installerHash = entry is ForgeEntry forgeEntry ? forgeEntry.Hash : null;
        reporter.BeginFile(Path.GetFileName(installerPath), 0);
        await _client.DownloadFileAsync(
                installerUrls,
                installerPath,
                null,
                installerHash,
                new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                cancellationToken)
            .ConfigureAwait(false);
        if (new FileInfo(installerPath).Length < 64 * 1024)
        {
            throw new DownloadException($"{loaderName} 安装器异常（小于 64KB）：{installerUrls.FirstOrDefault()}");
        }

        // 3) 分路径安装
        var isModern = entry switch
        {
            ForgeEntry forge => forge.NumericVersion.Major >= 20,
            NeoForgeEntry neoForge => !neoForge.LegacyPackage,
            CleanroomEntry => true,
            _ => false
        };
        var versionDirectory = Path.Combine(request.GameDirectory, "versions", request.TargetName);
        // 回滚标记：目录是本次新建的才回滚，避免误删已存在的实例
        var createdVersionDirectory = !Directory.Exists(versionDirectory);
        try
        {
            if (isModern)
            {
                reporter.Stage($"分析 {loaderName} 依赖");
                var installerLibs = await AnalyzeForgelikeInstallerAsync(
                        installerPath,
                        vanillaJsonPath,
                        request,
                        cancellationToken)
                    .ConfigureAwait(false);
                reporter.Stage($"下载 {loaderName} 依赖", installerLibs.Count);
                await DownloadLibrariesAsync(installerLibs, request.GameDirectory, request.Threads, reporter, cancellationToken)
                    .ConfigureAwait(false);

                var versionsSnapshot = Directory.Exists(Path.Combine(request.GameDirectory, "versions"))
                    ? Directory.EnumerateDirectories(Path.Combine(request.GameDirectory, "versions")).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                reporter.Stage($"运行 {loaderName} 安装器");
                await RunForgelikeHeadlessInstallerAsync(
                        installerPath,
                        cacheDirectory,
                        request,
                        reporter,
                        cancellationToken)
                    .ConfigureAwait(false);

                CopyGeneratedForgelikeInstance(request.GameDirectory, versionsSnapshot, versionDirectory, request.TargetName, loaderName);
            }
            else
            {
                reporter.Stage($"解包 {loaderName} 安装器");
                await InstallLegacyForgelikeAsync(
                        installerPath,
                        vanillaJsonPath,
                        request,
                        reporter,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // 下载依赖失败/用户取消：回滚本次新建的实例目录，不留半成品（对齐原版安装的回滚策略）
            if (createdVersionDirectory && Directory.Exists(versionDirectory))
            {
                try
                {
                    Directory.Delete(versionDirectory, true);
                    _logger.Warn($"{loaderName} 安装未完成，已回滚实例目录：{versionDirectory}");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.Error("回滚实例目录失败", ex);
                }
            }

            throw;
        }

        TryDeleteFile(installerPath);
        reporter.Stage("安装完成");
        _logger.Info($"{loaderName} {entry.VersionName}（{request.GameVersion}）安装完成：{request.TargetName}");
        return request.TargetName;
    }

    // ---------- 新版 headless 安装 ----------

    /// <summary>解析安装器（install_profile.json + version.json 合并）得到预下载库清单。</summary>
    private async Task<IReadOnlyList<LibraryFile>> AnalyzeForgelikeInstallerAsync(
        string installerPath,
        string vanillaJsonPath,
        ForgelikeInstallRequest request,
        CancellationToken cancellationToken)
    {
        using var installer = ZipFile.OpenRead(installerPath);
        var profileEntry = installer.GetEntry("install_profile.json")
                           ?? throw new DownloadException("安装器缺少 install_profile.json");
        var versionEntry = installer.GetEntry("version.json")
                           ?? throw new DownloadException("安装器缺少 version.json");
        var merged = JsonNode.Parse(await ReadEntryAsync(profileEntry, cancellationToken).ConfigureAwait(false)) as JsonObject
                     ?? throw new DownloadException("install_profile.json 解析失败");
        var versionJson = JsonNode.Parse(await ReadEntryAsync(versionEntry, cancellationToken).ConfigureAwait(false)) as JsonObject
                          ?? throw new DownloadException("version.json 解析失败");
        VersionJsonMerger.MergeInto(merged, versionJson);

        // 1.16.5 的 authlib 修复（对应 PCL-CE _AnalyzeForgelikeLibraries）
        if (FixAuthlibEnabled && request.GameVersion.Equals("1.16.5", StringComparison.Ordinal))
        {
            merged = JsonNode.Parse(VersionJsonMerger.FixAuthlib(merged.ToJsonString(JsonOptions))) as JsonObject
                     ?? throw new DownloadException("install_profile.json 解析失败");
        }

        var info = JsonSerializer.Deserialize(merged.ToJsonString(JsonOptions), NetJsonContext.Default.VersionInfo)
                   ?? throw new DownloadException("install_profile.json 解析失败");
        var environment = RuleEnvironment.Current(false);
        var libs = LibraryResolver.CollectLibraries(info, environment, request.Policy).ToList();

        // MOJMAPS：预下载客户端映射，headless 安装器会直接使用（对应 PCL-CE data.MOJMAPS 处理）
        if (merged["data"]?["MOJMAPS"]?["client"] is { } mappingsClientNode)
        {
            var mappingsClient = mappingsClientNode.ToString().Trim('[', ']');
            var segments = mappingsClient.Split('@');
            if (segments.Length == 2)
            {
                // 坐标可能带 :mappings 分类器（如 net.minecraft:client:1.20.1-xxx:mappings@txt），
                // maven 路径的 -mappings 后缀由扩展名替换产生，需先去掉分类器避免双写
                var coordinateParts = segments[0].Split(':');
                var originalName = coordinateParts.Length >= 4
                    ? string.Join(':', coordinateParts.Take(3))
                    : segments[0];
                var relativePath = LibraryResolver.GetPathFromName(originalName)
                    .Replace(".jar", $"-mappings.{segments[1]}", StringComparison.Ordinal);
                var vanillaJson = JsonNode.Parse(
                                      await File.ReadAllTextAsync(vanillaJsonPath, cancellationToken).ConfigureAwait(false))
                                  as JsonObject
                                  ?? throw new DownloadException("原版版本 JSON 解析失败");
                var mappings = vanillaJson["downloads"]?["client_mappings"];
                if (mappings?["url"] is { } mappingsUrl)
                {
                    libs.Add(new LibraryFile(
                        relativePath,
                        DownloadSources.GetLauncherOrMetaUrls(mappingsUrl.ToString(), request.Policy),
                        mappings["size"]?.GetValue<long>() ?? 0,
                        mappings["sha1"]?.ToString()));
                    _logger.Info($"需要下载客户端映射：{mappingsUrl}");
                }
            }
        }

        // 移除 Forge 自带的客户端 jar（安装器生成时会处理）
        var packageKind = GetPackageKindLower(request.Entry);
        var versionName = request.Entry.VersionName;
        return libs
            .Where(lib => !lib.RelativePath.EndsWith($"{packageKind}-{request.GameVersion}-{versionName}.jar", StringComparison.OrdinalIgnoreCase)
                          && !lib.RelativePath.EndsWith($"{packageKind}-{request.GameVersion}-{versionName}-client.jar", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>运行 BMCLAPI headless 引导器（成功判定：输出末 5 行含 "true"）。</summary>
    private async Task RunForgelikeHeadlessInstallerAsync(
        string installerPath,
        string cacheDirectory,
        ForgelikeInstallRequest request,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var mcFolder = request.GameDirectory;

        // launcher_profiles.json（官方安装器与引导器都需要）
        var profilesPath = Path.Combine(mcFolder, "launcher_profiles.json");
        if (!File.Exists(profilesPath))
        {
            Directory.CreateDirectory(mcFolder);
            await File.WriteAllTextAsync(profilesPath, "{\"profiles\":{}}", cancellationToken).ConfigureAwait(false);
        }

        // 释放内嵌引导器
        var bootstrapperPath = Path.Combine(cacheDirectory, "forge_installer.jar");
        await ExtractEmbeddedInstallerAsync(bootstrapperPath, cancellationToken).ConfigureAwait(false);

        var java = await SelectInstallerJavaAsync(request.JavaPath, cancellationToken).ConfigureAwait(false);
        var arguments = $"-cp \"{bootstrapperPath};{installerPath}\" com.bangbang93.ForgeInstaller \"{mcFolder}\"";
        if (java.MajorVersion >= 9)
        {
            arguments = $"--add-exports cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED {arguments}";
        }

        _logger.Info($"运行 {request.Entry.Kind} headless 安装器：{java.ExecutablePath} {arguments}");
        reporter.BeginFile("安装中", 10000);
        var lines = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var (exitCode, success) = await RunJavaAsync(
                java.ExecutablePath,
                arguments,
                line =>
                {
                    if (line is null)
                    {
                        return;
                    }

                    lines.Enqueue(line);
                    _logger.Info($"[Installer] {line}");
                    if (MapInstallerLineProgress(line) is { } progressValue)
                    {
                        reporter.FileBytes((long)(progressValue * 10000), 10000);
                    }
                },
                cancellationToken)
            .ConfigureAwait(false);
        reporter.FileBytes(success ? 10000 : 9900, 10000);

        var lastLines = lines.Reverse().Take(5).ToList();
        if (!success || !lastLines.Any(line => line.Trim() == "true"))
        {
            throw new DownloadException(
                $"{request.Entry.Kind} 安装器执行失败（exit={exitCode}）：{string.Join(Environment.NewLine, lastLines)}");
        }
    }

    /// <summary>安装器输出行 → 相对进度（对应 PCL-CE ForgelikeInjectorLine）。</summary>
    private static double? MapInstallerLineProgress(string line)
        => line switch
        {
            "Extracting json" => 0.07,
            "Downloading libraries" => 0.08,
            "  File exists: Checksum validated." => null,
            "Building Processors" => 0.18,
            "Task: DOWNLOAD_MOJMAPS" => 0.2,
            "Task: MERGE_MAPPING" => 0.3,
            "Splitting: " => 0.35,
            "Parameter Annotations" => 0.4,
            "Processing Complete" => 0.5,
            "log: null" => 0.5,
            "Sorting" => 0.65,
            "Remapping final jar" => 0.72,
            "Remapping jar... 50%" => 0.76,
            "Remapping jar... 100%" => 0.81,
            "Injecting profile" => 0.91,
            _ => null
        };

    /// <summary>拷贝 headless 安装器新生成的版本 JSON 到目标实例目录（对应 PCL-CE _CopyGeneratedForgelikeInstance）。</summary>
    private void CopyGeneratedForgelikeInstance(
        string gameDirectory,
        IReadOnlySet<string> versionsSnapshot,
        string versionDirectory,
        string targetName,
        string loaderName)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        // 先枚举新增目录，再创建目标目录，避免目标目录本身混入 delta
        var delta = Directory.EnumerateDirectories(versionsDirectory)
            .Where(dir => !versionsSnapshot.Contains(dir))
            .Where(dir => !string.Equals(dir, versionDirectory, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Directory.CreateDirectory(versionDirectory);
        if (delta.Count > 1)
        {
            delta = delta
                .Where(dir => dir.Contains("forge", StringComparison.OrdinalIgnoreCase)
                              || dir.Contains("neoforge", StringComparison.OrdinalIgnoreCase)
                              || dir.Contains("cleanroom", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        switch (delta.Count)
        {
            case 1:
            {
                var generatedJson = Path.Combine(delta[0], Path.GetFileName(delta[0]) + ".json");
                if (!File.Exists(generatedJson))
                {
                    _logger.Warn($"安装器生成目录缺少版本 JSON：{delta[0]}");
                    return;
                }

                File.Copy(generatedJson, Path.Combine(versionDirectory, targetName + ".json"), true);
                _logger.Info($"已拷贝安装器生成的版本 JSON：{generatedJson}");
                break;
            }
            case > 1:
                _logger.Warn($"有多个疑似新生成的实例目录，无法确定：{string.Join(";", delta.Select(Path.GetFileName))}");
                break;
            default:
                _logger.Warn("未找到安装器新生成的版本目录，可能安装器已直接写入目标目录");
                break;
        }
    }

    // ---------- 旧版 installer 解包 ----------

    /// <summary>
    /// 旧版 Forge（&lt;1.13）与 NeoForge 1.20.1 安装：
    /// install_profile.json 无 "install" → 方法一（解压 maven/ + 内嵌版本 json）；
    /// 有 → 方法二（写 install.filePath 到 libraries + versionInfo json）。
    /// </summary>
    private async Task InstallLegacyForgelikeAsync(
        string installerPath,
        string vanillaJsonPath,
        ForgelikeInstallRequest request,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        using var installer = ZipFile.OpenRead(installerPath);
        var profileEntry = installer.GetEntry("install_profile.json")
                           ?? throw new DownloadException("安装器缺少 install_profile.json");
        var profileJson = JsonNode.Parse(await ReadEntryAsync(profileEntry, cancellationToken).ConfigureAwait(false)) as JsonObject
                          ?? throw new DownloadException("install_profile.json 解析失败");
        Directory.CreateDirectory(Path.Combine(request.GameDirectory, "versions", request.TargetName));

        if (profileJson["install"] is null)
        {
            // 方法一
            _logger.Info($"开始 {request.Entry.Kind} 旧版安装（方式一）：{installerPath}");
            var versionEntryName = profileJson["json"]?.ToString()
                                   ?? throw new DownloadException("安装器缺少版本 Json 信息");
            var versionEntry = installer.GetEntry(versionEntryName.TrimStart('/'))
                               ?? throw new DownloadException("安装器缺少版本 Json 文件");
            var versionJson = JsonNode.Parse(await ReadEntryAsync(versionEntry, cancellationToken).ConfigureAwait(false)) as JsonObject
                              ?? throw new DownloadException("安装器版本 Json 解析失败");
            versionJson["id"] = request.TargetName;
            var versionDirectory = Path.Combine(request.GameDirectory, "versions", request.TargetName);
            await File.WriteAllTextAsync(
                    Path.Combine(versionDirectory, request.TargetName + ".json"),
                    versionJson.ToJsonString(JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileBytes(6000, 10000);

            // 解压 maven/ 目录到 libraries/
            var librariesDirectory = Path.Combine(request.GameDirectory, "libraries");
            foreach (var entry in installer.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!entry.FullName.StartsWith("maven/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var relativePath = entry.FullName["maven/".Length..];
                if (string.IsNullOrEmpty(relativePath) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
                {
                    continue;
                }

                var destination = Path.Combine(librariesDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var fullDestination = Path.GetFullPath(destination);
                if (!fullDestination.StartsWith(Path.GetFullPath(librariesDirectory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Warn($"跳过越界条目（zip-slip 防护）：{entry.FullName}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(fullDestination) ?? librariesDirectory);
                entry.ExtractToFile(fullDestination, true);
            }
        }
        else
        {
            // 方法二
            _logger.Info($"开始 {request.Entry.Kind} 旧版安装（方式二）：{installerPath}");
            var installInfo = profileJson["install"]
                              ?? throw new DownloadException("安装器缺少 install 信息");
            var coordinate = installInfo["path"]?.ToString()
                             ?? throw new DownloadException("安装器缺少安装坐标");
            var relativePath = LibraryResolver.GetPathFromName(coordinate);
            var jarPath = Path.Combine(request.GameDirectory, "libraries", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(jarPath) ?? request.GameDirectory);
            var installEntryName = installInfo["filePath"]?.ToString()
                                   ?? throw new DownloadException("安装器缺少安装文件名");
            var installEntry = installer.GetEntry(installEntryName)
                               ?? throw new DownloadException("安装器缺少安装文件");
            await using (var source = installEntry.Open())
            await using (var target = File.Create(jarPath))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }

            reporter.FileBytes(9000, 10000);
            var versionInfo = profileJson["versionInfo"] as JsonObject
                              ?? throw new DownloadException("安装器缺少 versionInfo 信息");
            versionInfo["id"] = request.TargetName;
            if (versionInfo["inheritsFrom"] is null)
            {
                versionInfo["inheritsFrom"] = request.GameVersion;
            }

            await File.WriteAllTextAsync(
                    Path.Combine(request.GameDirectory, "versions", request.TargetName, request.TargetName + ".json"),
                    versionInfo.ToJsonString(JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // 下载该版本 json 声明的库（版本目录里的 json 已写好）
        var writtenJsonPath = Path.Combine(request.GameDirectory, "versions", request.TargetName, request.TargetName + ".json");
        var writtenInfo = JsonSerializer.Deserialize(
            await File.ReadAllTextAsync(writtenJsonPath, cancellationToken).ConfigureAwait(false),
            NetJsonContext.Default.VersionInfo);
        if (writtenInfo is not null)
        {
            var environment = RuleEnvironment.Current(false);
            var libraries = LibraryResolver.CollectLibraries(writtenInfo, environment, request.Policy);
            reporter.Stage("下载旧版依赖", libraries.Count);
            await DownloadLibrariesAsync(libraries, request.GameDirectory, request.Threads, reporter, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    // ---------- 公共辅助 ----------

    private async Task ExtractEmbeddedInstallerAsync(string destinationPath, CancellationToken cancellationToken)
    {
        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 1024)
        {
            return;
        }

        var resourceName = "Hycl.Launcher.Core.Download.Resources.forge-installer.jar";
        await using var stream = typeof(VersionInstaller).Assembly.GetManifestResourceStream(resourceName)
                                 ?? throw new DownloadException($"缺少内嵌资源 {resourceName}");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath) ?? ".");
        await using var target = File.Create(destinationPath);
        await stream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JavaInstallation> SelectInstallerJavaAsync(string? javaPath, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            var specified = await _javaDetector.InspectAsync(javaPath, cancellationToken).ConfigureAwait(false);
            if (specified is not null)
            {
                return specified;
            }
        }

        var installations = await _javaDetector.DetectAsync(null, cancellationToken).ConfigureAwait(false);
        var best = JavaDetector.FindBest(installations, 8);
        if (best is null)
        {
            throw new DownloadException(
                "未找到可用的 Java 运行时（安装 Forge 需要 Java 8 及以上）。请在设置中配置 Java 后重试。");
        }

        if (best.MajorVersion < 8)
        {
            throw new DownloadException($"安装 Forge 需要 Java 8 及以上，当前最佳匹配：{best.Detail}");
        }

        return best;
    }

    /// <summary>运行 Java 进程并逐行回传输出；返回 (退出码, 是否正常退出)。</summary>
    private async Task<(int ExitCode, bool ExitedCleanly)> RunJavaAsync(
        string executable,
        string arguments,
        Action<string?> onLine,
        CancellationToken cancellationToken,
        IDictionary<string, string>? environment = null,
        string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (workingDirectory is not null)
        {
            info.WorkingDirectory = workingDirectory;
        }

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
            {
                info.EnvironmentVariables[key] = value;
            }
        }

        foreach (var part in SplitArguments(arguments))
        {
            info.ArgumentList.Add(part);
        }

        using var process = Process.Start(info) ?? throw new DownloadException("无法启动 Java 进程");
        var outputTask = PumpStreamAsync(process.StandardOutput, onLine, cancellationToken);
        var errorTask = PumpStreamAsync(process.StandardError, onLine, cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 取消/关窗：终止安装器子进程（含子进程树），避免进程退出后仍继续写版本目录
            TryKillProcess(process);
            throw;
        }

        await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
        onLine(null);
        return (process.ExitCode, process.ExitCode == 0);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or SystemException)
        {
            // 进程已自行退出或句柄失效，无需处理
        }
    }

    private static async Task PumpStreamAsync(StreamReader reader, Action<string?> onLine, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            onLine(line);
        }
    }

    /// <summary>简单命令行拆分（支持双引号）；参数已按平台用 ArgumentList 传递。</summary>
    private static IReadOnlyList<string> SplitArguments(string arguments)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var ch in arguments)
        {
            switch (ch)
            {
                case '"':
                    inQuotes = !inQuotes;
                    break;
                case ' ' when !inQuotes:
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }

                    break;
                default:
                    current.Append(ch);
                    break;
            }
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>该条目在 maven 中的包名（NeoForge 1.20.1 用旧 forge 包名）。</summary>
    private static string GetPackageKindLower(ForgelikeEntry entry)
        => entry switch
        {
            NeoForgeEntry { LegacyPackage: true } => "forge",
            NeoForgeEntry => "neoforge",
            CleanroomEntry => "cleanroom",
            _ => "forge"
        };

    private static async Task<string> ReadEntryAsync(ZipArchiveEntry entry, CancellationToken cancellationToken)
    {
        await using var stream = entry.Open();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
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
            // 忽略清理失败
        }
    }
}
