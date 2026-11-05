using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Java;

namespace Hycl.Launcher.Core.Game;

public partial class VersionInstaller
{
    /// <summary>OptiFine 安装请求。</summary>
    public sealed record OptiFineInstallRequest
    {
        public required string GameDirectory { get; init; }

        /// <summary>继承的 Minecraft 版本。</summary>
        public required string GameVersion { get; init; }

        /// <summary>目标实例目录名；null 使用 OptiFine 自带的 id。</summary>
        public string? TargetName { get; init; }

        public required OptiFineEntry Entry { get; init; }

        public required DownloadPolicy Policy { get; init; }

        /// <summary>目标实例是否启用版本隔离；附属 OptiFine 的 mods 目录据此解析。</summary>
        public bool VersionIsolation { get; init; } = true;

        public int Threads { get; init; } = 16;

        public bool DownloadAssets { get; init; } = true;

        public string? JavaPath { get; init; }
    }

    /// <summary>
    /// 安装 OptiFine：
    /// ≥1.14（或 26w 系快照）运行官方 headless 安装器（optifine.Installer，写入临时主目录后回拷）；
    /// &lt;1.14 复制客户端 jar 并手写 launchwrapper 版本 json。
    /// </summary>
    public async Task<string> InstallOptiFineAsync(
        OptiFineInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = new ProgressReporter(progress);
        var entry = request.Entry;
        var inherit = request.GameVersion;
        _logger.Info($"开始安装 OptiFine {entry.NameVersion}（{inherit} → {request.TargetName ?? entry.NameVersion}）");

        // 1) 确保原版就绪
        reporter.Stage("检查原版");
        await EnsureVanillaAsync(
                request.GameDirectory,
                inherit,
                request.Policy,
                request.Threads,
                progress,
                cancellationToken,
                request.DownloadAssets)
            .ConfigureAwait(false);

        // 版本目录快照：安装失败时回滚本次新建的 OptiFine 实例目录（对齐原版/Forge 的回滚策略）
        var versionsDirectory = Path.Combine(request.GameDirectory, "versions");
        var knownVersions = Directory.Exists(versionsDirectory)
            ? Directory.EnumerateDirectories(versionsDirectory).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // 2) 下载 OptiFine 安装器/文件（BMCLAPI 在前，官方 adloadx 解析地址在后）
            reporter.Stage("下载 OptiFine");
            var isNewVersion = inherit.Contains('w')
                               || CompareVersionGreaterOrEqual(inherit, "1.14");
            var target = isNewVersion
                ? Path.Combine(_cacheDirectory ?? Path.GetTempPath(), "OptiFine.jar")
                : GetLegacyOptiFineTarget(request.GameDirectory, entry);
            await DownloadOptiFineFileAsync(entry, request.Policy, target, reporter, cancellationToken)
                .ConfigureAwait(false);

            if (isNewVersion)
            {
                reporter.Stage("运行 OptiFine 安装器");
                await InstallNewOptiFineAsync(target, request, reporter, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                reporter.Stage("写入旧版 OptiFine 配置");
                await InstallLegacyOptiFineAsync(target, request, reporter, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 3) 下载最终版本 json 的库
            string installedName = isNewVersion ? ResolveOptiFineInstalledName(request) : (request.TargetName ?? entry.NameVersion!);
            var versionJsonPath = Path.Combine(request.GameDirectory, "versions", installedName, installedName + ".json");
            if (File.Exists(versionJsonPath))
            {
                var info = JsonSerializer.Deserialize<VersionInfo>(
                    await File.ReadAllTextAsync(versionJsonPath, cancellationToken).ConfigureAwait(false),
                    JsonOptions);
                if (info is not null)
                {
                    var libraries = LibraryResolver.CollectLibraries(info, RuleEnvironment.Current(false), request.Policy);
                    reporter.Stage("下载 OptiFine 依赖", libraries.Count);
                    await DownloadLibrariesAsync(libraries, request.GameDirectory, request.Threads, reporter, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            reporter.Stage("安装完成");
            _logger.Info($"OptiFine {entry.NameVersion}（{inherit}）安装完成：{installedName}");
            return installedName;
        }
        catch (Exception ex) when (ex is OperationCanceledException or DownloadException or IOException or HttpRequestException)
        {
            if (Directory.Exists(versionsDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
                {
                    if (!knownVersions.Contains(directory))
                    {
                        TryDeleteDirectory(directory, Path.GetFileName(directory));
                    }
                }
            }

            throw;
        }
    }

    /// <summary>与 Forge 等加载器共存时，把 OptiFine 安装器 jar 直接放入 mods 目录（对应 PCL-CE OptiFineAsMod）。</summary>
    public async Task<string> InstallOptiFineAsModAsync(
        OptiFineInstallRequest request,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = new ProgressReporter(progress);
        reporter.Stage("下载 OptiFine");
        // 遵循版本隔离：隔离实例的 mods 在实例目录内，而非游戏根目录
        var contentRoot = request.TargetName is null
            ? request.GameDirectory
            : InstanceManager.GetContentRoot(request.GameDirectory, request.TargetName, request.VersionIsolation);
        var modsDirectory = Path.Combine(contentRoot, "mods");
        Directory.CreateDirectory(modsDirectory);
        var target = Path.Combine(modsDirectory, request.Entry.NameFile ?? "OptiFine.jar");
        await DownloadOptiFineFileAsync(request.Entry, request.Policy, target, reporter, cancellationToken)
            .ConfigureAwait(false);
        reporter.Stage("安装完成");
        _logger.Info($"OptiFine 安装器已放入 mods：{target}");
        return target;
    }

    private async Task DownloadOptiFineFileAsync(
        OptiFineEntry entry,
        DownloadPolicy policy,
        string target,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var sources = await GetOptiFineDownloadSourcesAsync(entry, policy, cancellationToken).ConfigureAwait(false);
        reporter.BeginFile(Path.GetFileName(target), 0);
        await _client.DownloadFileAsync(
                sources,
                target,
                null,
                null,
                new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                cancellationToken)
            .ConfigureAwait(false);
        if (new FileInfo(target).Length < 300 * 1024)
        {
            // 对应 PCL-CE FileChecker(300KB)：过小说明下载到的是错误页
            throw new DownloadException($"OptiFine 文件异常（小于 300KB）：{sources.FirstOrDefault()}");
        }
    }

    /// <summary>OptiFine 下载地址：BMCLAPI 镜像 + 官方 adloadx 页面解析（对应 PCL-CE _GetOptiFineDownloadSources）。</summary>
    private async Task<IReadOnlyList<string>> GetOptiFineDownloadSourcesAsync(
        OptiFineEntry entry,
        DownloadPolicy policy,
        CancellationToken cancellationToken)
    {
        var inherit = entry.Inherit ?? "";
        var displayName = entry.DisplayName ?? "";
        var bmclapiInherit = inherit is "1.8" or "1.9" ? inherit + ".0" : inherit;
        var patch = displayName.Replace($"{inherit} ", "");
        var sources = new List<string>
        {
            entry.IsPreview
                ? $"https://bmclapi2.bangbang93.com/optifine/{bmclapiInherit}/HD_U_{patch.Replace(" ", "/")}"
                : $"https://bmclapi2.bangbang93.com/optifine/{bmclapiInherit}/HD_U/{patch}"
        };

        try
        {
            if (!string.IsNullOrWhiteSpace(entry.NameFile))
            {
                var pageData = await _client
                    .GetStringAsync($"https://optifine.net/adloadx?f={Uri.EscapeDataString(entry.NameFile)}", cancellationToken)
                    .ConfigureAwait(false);
                var match = Regex.Match(pageData, "downloadx\\?f=[^\"']+");
                if (match.Success)
                {
                    sources.Add($"https://optifine.net/{match.Value}");
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or DownloadException)
        {
            _logger.Warn($"获取 OptiFine {entry.DisplayName} 官方下载地址失败：{ex.Message}");
        }

        return sources;
    }

    // ---------- 新版（≥1.14）headless 安装 ----------

    private async Task InstallNewOptiFineAsync(
        string installerPath,
        OptiFineInstallRequest request,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var inherit = request.GameVersion;
        var tempHome = Path.Combine(Path.GetTempPath(), "hycl-optifine-" + Guid.NewGuid().ToString("N")[..8]);
        var baseMcFolder = Path.Combine(tempHome, ".minecraft");
        try
        {
            // 准备临时主目录：只带被继承版本，避免安装器扫到无关版本
            var inheritedFolder = Path.Combine(baseMcFolder, "versions", inherit);
            Directory.CreateDirectory(inheritedFolder);
            await File.WriteAllTextAsync(
                    Path.Combine(baseMcFolder, "launcher_profiles.json"),
                    "{\"profiles\":{}}",
                    cancellationToken)
                .ConfigureAwait(false);
            File.Copy(
                Path.Combine(request.GameDirectory, "versions", inherit, inherit + ".json"),
                Path.Combine(inheritedFolder, inherit + ".json"),
                true);
            var vanillaJar = Path.Combine(request.GameDirectory, "versions", inherit, inherit + ".jar");
            if (File.Exists(vanillaJar))
            {
                File.Copy(vanillaJar, Path.Combine(inheritedFolder, inherit + ".jar"), true);
            }

            reporter.FileBytes(600, 10000);
            var java = await SelectOptiFineJavaAsync(inherit, request.JavaPath, cancellationToken).ConfigureAwait(false);
            var arguments = $"-Duser.home=\"{tempHome}\" -cp \"{installerPath}\" optifine.Installer";
            if (java.MajorVersion >= 9)
            {
                arguments = $"--add-exports cpw.mods.bootstraplauncher/cpw.mods.bootstraplauncher=ALL-UNNAMED {arguments}";
            }

            _logger.Info($"运行 OptiFine headless 安装器：{java.ExecutablePath} {arguments}");
            var lastLine = "";
            var lineCount = 0;
            var (exitCode, _) = await RunJavaAsync(
                    java.ExecutablePath,
                    arguments,
                    line =>
                    {
                        if (line is null)
                        {
                            return;
                        }

                        lastLine = line;
                        lineCount++;
                        reporter.FileBytes(Math.Min(9000, 600 + lineCount * 10), 10000);
                    },
                    cancellationToken,
                    // OptiFine 安装器在 Windows 上经 %APPDATA% 定位 .minecraft（对应 PCL-CE 环境变量注入）
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["appdata"] = tempHome
                    },
                    tempHome)
                .ConfigureAwait(false);
            if (lineCount < 1000 || lastLine.Contains("at "))
            {
                throw new DownloadException($"OptiFine 安装器执行失败（exit={exitCode}）：{lastLine}");
            }

            reporter.FileBytes(9600, 10000);

            // 回拷临时 .minecraft 到游戏目录
            var profilesFile = Path.Combine(baseMcFolder, "launcher_profiles.json");
            if (File.Exists(profilesFile))
            {
                File.Delete(profilesFile);
            }

            CopyDirectoryMerge(baseMcFolder, request.GameDirectory);
            reporter.FileBytes(9800, 10000);

            // 目标实例名与安装器产物不同时重命名
            var producedId = ResolveOptiFineInstalledName(request);
            var targetName = request.TargetName;
            if (!string.IsNullOrWhiteSpace(targetName) && targetName != producedId)
            {
                RenameVersionFolder(request.GameDirectory, producedId, targetName);
            }
        }
        finally
        {
            TryDeleteFile(installerPath);
            try
            {
                if (Directory.Exists(tempHome))
                {
                    Directory.Delete(tempHome, true);
                }
            }
            catch (IOException)
            {
                // 临时目录清理失败可忽略
            }
        }
    }

    private async Task<JavaInstallation> SelectOptiFineJavaAsync(
        string inherit,
        string? javaPath,
        CancellationToken cancellationToken)
    {
        // 对应 PCL-CE：MC ≥ 26.1.2 需 Java 21，其余需要 Java 8（优先精确主版本，缺失时回退可用版本）
        var requiredMajor = CompareVersionGreaterOrEqual(inherit, "26.1.2") ? 21 : 8;
        if (!string.IsNullOrWhiteSpace(javaPath))
        {
            var specified = await _javaDetector.InspectAsync(javaPath, cancellationToken).ConfigureAwait(false);
            if (specified is not null)
            {
                return specified;
            }
        }

        var installations = await _javaDetector.DetectAsync(null, cancellationToken).ConfigureAwait(false);
        var exact = installations.FirstOrDefault(x => x.MajorVersion == requiredMajor);
        if (exact is not null)
        {
            return exact;
        }

        var fallback = JavaDetector.FindBest(installations, requiredMajor);
        if (fallback is null)
        {
            throw new DownloadException($"安装 OptiFine 需要 Java {requiredMajor}，未找到可用 Java。请在设置中配置后重试。");
        }

        _logger.Warn($"未找到 Java {requiredMajor}，OptiFine 安装器将使用 {fallback.Detail}");
        return fallback;
    }

    // ---------- 旧版（<1.14）launchwrapper json ----------

    private async Task InstallLegacyOptiFineAsync(
        string downloadedPath,
        OptiFineInstallRequest request,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var entry = request.Entry;
        var inherit = request.GameVersion;
        var id = request.TargetName ?? entry.NameVersion
                 ?? throw new DownloadException("OptiFine 版本缺少实例名");
        var versionFolder = Path.Combine(request.GameDirectory, "versions", id);
        Directory.CreateDirectory(versionFolder);

        // 复制原版客户端 jar
        var versionJarPath = Path.Combine(versionFolder, id + ".jar");
        File.Copy(
            Path.Combine(request.GameDirectory, "versions", inherit, inherit + ".jar"),
            versionJarPath,
            true);
        reporter.FileBytes(7000, 10000);

        // 把下载的 OptiFine jar 放到 libraries 的 optifine 目录
        var nameFile = entry.NameFile
                       ?? throw new DownloadException("OptiFine 版本缺少文件名");
        File.Copy(downloadedPath, GetLegacyOptiFineTarget(request.GameDirectory, entry), true);

        var optiFineVersion = nameFile
            .Replace("OptiFine_", "")
            .Replace(".jar", "")
            .Replace("preview_", "");
        var releaseDate = (entry.ReleaseTime ?? DateTimeOffset.Now).ToString("yyyy-MM-dd");
        var versionJson = new JsonObject
        {
            ["id"] = id,
            ["inheritsFrom"] = inherit,
            ["time"] = $"{releaseDate}T23:33:33+08:00",
            ["releaseTime"] = $"{releaseDate}T23:33:33+08:00",
            ["type"] = "release",
            ["libraries"] = new JsonArray(
                new JsonObject { ["name"] = $"optifine:OptiFine:{optiFineVersion}" },
                new JsonObject { ["name"] = "net.minecraft:launchwrapper:1.12" }),
            ["mainClass"] = "net.minecraft.launchwrapper.Launch"
        };

        // 旧版 json（带 minecraftArguments）用 --tweakClass 追加；新版 json 用 arguments.game
        var vanillaJsonPath = Path.Combine(request.GameDirectory, "versions", inherit, inherit + ".json");
        var vanillaJson = JsonNode.Parse(
            await File.ReadAllTextAsync(vanillaJsonPath, cancellationToken).ConfigureAwait(false)) as JsonObject;
        var minecraftArguments = vanillaJson?["minecraftArguments"]?.ToString();
        var isOldJson = !string.IsNullOrWhiteSpace(minecraftArguments);
        if (isOldJson)
        {
            versionJson["minimumLauncherVersion"] = 18;
            versionJson["minecraftArguments"] = $"{minecraftArguments} --tweakClass optifine.OptiFineTweaker";
        }
        else
        {
            versionJson["minimumLauncherVersion"] = 21;
            versionJson["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--tweakClass", "optifine.OptiFineTweaker")
            };
        }

        await File.WriteAllTextAsync(
                Path.Combine(versionFolder, id + ".json"),
                versionJson.ToJsonString(JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
        TryDeleteFile(downloadedPath);
        reporter.FileBytes(10000, 10000);
    }

    /// <summary>旧版 OptiFine 文件在 libraries 的落点（对应 PCL-CE _GetOptiFineLoaderTarget）。</summary>
    private static string GetLegacyOptiFineTarget(string gameDirectory, OptiFineEntry entry)
    {
        var nameFile = entry.NameFile
                       ?? throw new DownloadException("OptiFine 版本缺少文件名");
        var folder = nameFile
            .Replace("OptiFine_", "")
            .Replace(".jar", "")
            .Replace("preview_", "");
        return Path.Combine(
            gameDirectory,
            "libraries",
            "optifine",
            "OptiFine",
            folder,
            nameFile.Replace("OptiFine_", "OptiFine-").Replace("preview_", ""));
    }

    /// <summary>查找 OptiFine 安装器产出的版本目录名。</summary>
    private string ResolveOptiFineInstalledName(OptiFineInstallRequest request)
    {
        var versionsDirectory = Path.Combine(request.GameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return request.TargetName ?? request.Entry.NameVersion ?? request.GameVersion;
        }

        var produced = Directory.EnumerateDirectories(versionsDirectory)
            .Where(dir => Path.GetFileName(dir).Contains("OptiFine", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetCreationTime)
            .FirstOrDefault();
        return produced is null
            ? request.TargetName ?? request.Entry.NameVersion ?? request.GameVersion
            : Path.GetFileName(produced);
    }

    /// <summary>合并复制目录（目标已存在的文件覆盖）。</summary>
    private static void CopyDirectoryMerge(string source, string target)
    {
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? target);
            File.Copy(file, destination, true);
        }
    }

    private void RenameVersionFolder(string gameDirectory, string fromId, string toId)
    {
        var source = Path.Combine(gameDirectory, "versions", fromId);
        var target = Path.Combine(gameDirectory, "versions", toId);
        if (!Directory.Exists(source) || Directory.Exists(target))
        {
            return;
        }

        var jsonPath = Path.Combine(source, fromId + ".json");
        if (File.Exists(jsonPath))
        {
            var json = JsonNode.Parse(File.ReadAllText(jsonPath)) as JsonObject;
            json?["id"] = toId;
            File.WriteAllText(Path.Combine(source, toId + ".json"), json?.ToJsonString(JsonOptions) ?? "{}");
            File.Delete(jsonPath);
        }

        var jarPath = Path.Combine(source, fromId + ".jar");
        if (File.Exists(jarPath))
        {
            File.Move(jarPath, Path.Combine(source, toId + ".jar"));
        }

        Directory.Move(source, target);
        _logger.Info($"OptiFine 版本目录已重命名：{fromId} → {toId}");
    }

    /// <summary>版本号比较：a ≥ b（对应 PCL-CE CompareVersionGe 的常见用法）。</summary>
    private static bool CompareVersionGreaterOrEqual(string a, string b)
    {
        if (Version.TryParse(a.Split('-')[0].Split('+')[0], out var versionA)
            && Version.TryParse(b, out var versionB))
        {
            return versionA >= versionB;
        }

        return string.CompareOrdinal(a, b) >= 0;
    }
}
