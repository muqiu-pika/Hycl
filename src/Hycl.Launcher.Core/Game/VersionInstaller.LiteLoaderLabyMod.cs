using System.Text.Json;
using System.Text.Json.Nodes;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.Game;

public partial class VersionInstaller
{
    /// <summary>
    /// 安装 LiteLoader（移植自 PCL-CE DownloadUi\LiteLoader.cs）：
    /// 以 versions.json 的条目手工构造 launchwrapper 版本 json（--tweakClass + liteloader 库）。
    /// </summary>
    public async Task<string> InstallLiteLoaderAsync(
        string gameDirectory,
        string gameVersion,
        string? targetName,
        LiteLoaderEntry entry,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool downloadAssets = true)
    {
        var reporter = new ProgressReporter(progress);
        var id = targetName ?? $"{gameVersion}-LiteLoader";
        _logger.Info($"开始安装 LiteLoader（{gameVersion} → {id}）");

        // 1) 确保原版就绪
        reporter.Stage("检查原版");
        await EnsureVanillaAsync(
                gameDirectory,
                gameVersion,
                policy,
                threads,
                progress,
                cancellationToken,
                downloadAssets)
            .ConfigureAwait(false);

        // 2) 构造版本 json
        reporter.Stage("写入 LiteLoader 配置");
        var rawJson = entry.RawJson
                      ?? throw new DownloadException("LiteLoader 条目缺少原始 JSON");
        var raw = JsonNode.Parse(rawJson) as JsonObject
                  ?? throw new DownloadException("LiteLoader 条目 JSON 解析失败");
        var tweakClass = raw["tweakClass"]?.ToString();
        if (string.IsNullOrWhiteSpace(tweakClass))
        {
            throw new DownloadException("LiteLoader 条目缺少 tweakClass");
        }

        var releaseDate = (entry.ReleaseTime ?? DateTimeOffset.Now).ToString("yyyy-MM-dd'T'HH:mm:sszzz");
        var libraries = raw["libraries"] is JsonArray rawLibraries
            ? (JsonArray)rawLibraries.DeepClone()
            : new JsonArray();
        libraries.Add(new JsonObject
        {
            ["name"] = $"com.mumfrey:liteloader:{raw["version"]}",
            ["url"] = "https://dl.liteloader.com/versions/"
        });
        var versionJson = new JsonObject
        {
            ["id"] = id,
            ["time"] = releaseDate,
            ["releaseTime"] = releaseDate,
            ["type"] = "release",
            ["arguments"] = new JsonObject
            {
                ["game"] = new JsonArray("--tweakClass", tweakClass)
            },
            ["libraries"] = libraries,
            ["mainClass"] = "net.minecraft.launchwrapper.Launch",
            ["minimumLauncherVersion"] = 18,
            ["inheritsFrom"] = gameVersion,
            ["jar"] = gameVersion
        };

        var versionDirectory = Path.Combine(gameDirectory, "versions", id);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
                Path.Combine(versionDirectory, id + ".json"),
                versionJson.ToJsonString(JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

        // 3) 下载依赖库
        var info = JsonSerializer.Deserialize<VersionInfo>(versionJson.ToJsonString(JsonOptions), JsonOptions)
                   ?? throw new DownloadException("LiteLoader 版本 JSON 解析失败");
        var collected = LibraryResolver.CollectLibraries(info, RuleEnvironment.Current(false), policy);
        reporter.Stage("下载 LiteLoader 依赖", collected.Count);
        await DownloadLibrariesAsync(collected, gameDirectory, threads, reporter, cancellationToken)
            .ConfigureAwait(false);

        reporter.Stage("安装完成");
        _logger.Info($"LiteLoader（{gameVersion}）安装完成：{id}");
        return id;
    }

    /// <summary>
    /// 安装 LabyMod（对应 PCL-CE McDownloadLabyModLoader）：
    /// 每版本 manifest json 直接作为版本 json（含 LabyMod 自己的 libraries），复制原版 jar 后下载依赖。
    /// </summary>
    public async Task<string> InstallLabyModAsync(
        string gameDirectory,
        string gameVersion,
        string? targetName,
        LabyModEntry entry,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool downloadAssets = true)
    {
        var reporter = new ProgressReporter(progress);
        var id = targetName ?? $"labymod-{entry.CommitRef}-{gameVersion}";
        _logger.Info($"开始安装 LabyMod {entry.LabyModVersion}（{entry.Channel}，{gameVersion} → {id}）");

        // 1) 确保原版就绪（客户端 jar 与资源文件）
        reporter.Stage("检查原版");
        await EnsureVanillaAsync(
                gameDirectory,
                gameVersion,
                policy,
                threads,
                progress,
                cancellationToken,
                downloadAssets)
            .ConfigureAwait(false);

        // 2) 获取 LabyMod 版本 json
        reporter.Stage("获取 LabyMod 版本信息");
        var safeName = gameVersion.Replace("∞", "infinite");
        var manifestUrls = new List<string>
        {
            $"https://releases.r2.labymod.net/api/v1/download/manifest/labymod4/{entry.Channel}/{safeName}/{entry.CommitRef}.json"
        };
        var manifestText = await _client.GetStringAsync(manifestUrls, cancellationToken).ConfigureAwait(false);
        var manifestJson = JsonNode.Parse(manifestText) as JsonObject
                           ?? throw new DownloadException("LabyMod 版本 JSON 解析失败");
        manifestJson["id"] = id;

        var versionDirectory = Path.Combine(gameDirectory, "versions", id);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(
                Path.Combine(versionDirectory, id + ".json"),
                manifestJson.ToJsonString(JsonOptions),
                cancellationToken)
            .ConfigureAwait(false);

        // 3) 复制原版客户端 jar
        var vanillaJarPath = Path.Combine(gameDirectory, "versions", gameVersion, gameVersion + ".jar");
        var outputJarPath = Path.Combine(versionDirectory, id + ".jar");
        if (File.Exists(vanillaJarPath) && !File.Exists(outputJarPath))
        {
            File.Copy(vanillaJarPath, outputJarPath, true);
        }

        // 4) 下载 LabyMod 的依赖库
        var info = JsonSerializer.Deserialize<VersionInfo>(manifestJson.ToJsonString(JsonOptions), JsonOptions)
                   ?? throw new DownloadException("LabyMod 版本 JSON 解析失败");
        var collected = LibraryResolver.CollectLibraries(info, RuleEnvironment.Current(false), policy);
        reporter.Stage("下载 LabyMod 依赖", collected.Count);
        await DownloadLibrariesAsync(collected, gameDirectory, threads, reporter, cancellationToken)
            .ConfigureAwait(false);

        // 5) LabyMod manifest 使用与原版相同的资源索引，无需重复下载
        reporter.Stage("安装完成");
        _logger.Info($"LabyMod {entry.LabyModVersion}（{gameVersion}）安装完成：{id}");
        return id;
    }
}
