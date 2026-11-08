using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 版本安装：原版（客户端、依赖库、资源、日志配置）与加载器。
/// 这是 PCL-CE 下载模块的跨平台重写，不依赖 Windows API。
/// </summary>
public sealed partial class VersionInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>版本列表磁盘缓存的有效期。</summary>
    public static readonly TimeSpan VersionListCacheTtl = TimeSpan.FromHours(24);

    /// <summary>列表竞速：副源加入等待（对应 PCL-CE 双源竞速，Hycl 收紧为 5 秒）。</summary>
    public static readonly TimeSpan RaceJoinDelay = TimeSpan.FromSeconds(5);

    /// <summary>列表单源超时（不重试）。</summary>
    public static readonly TimeSpan ListTimeout = TimeSpan.FromSeconds(8);

    private const string UnlistedVersionsUrl =
        "https://alist.8mi.tech/d/mirror/unlisted-versions-of-minecraft/Auto/version_manifest.json";

    private readonly DownloadClient _client;
    private readonly LauncherLogger _logger;
    private readonly VersionListCache? _versionListCache;
    private readonly Java.JavaDetector _javaDetector;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private readonly string? _cacheDirectory;

    /// <summary>是否对 1.16.5 应用 authlib 2.3.31 修复（对应 PCL-CE ToolFixAuthlib；由设置驱动）。</summary>
    public bool FixAuthlibEnabled { get; set; } = true;

    public VersionInstaller(DownloadClient client, LauncherLogger logger, string? cacheDirectory = null)
        : this(client, logger, cacheDirectory, null)
    {
    }

    public VersionInstaller(
        DownloadClient client,
        LauncherLogger logger,
        string? cacheDirectory,
        Java.JavaDetector? javaDetector)
    {
        _client = client;
        _logger = logger;
        _cacheDirectory = cacheDirectory;
        _javaDetector = javaDetector ?? new Java.JavaDetector(logger);
        _versionListCache = cacheDirectory is null ? null : new VersionListCache(cacheDirectory);
    }

    /// <summary>
    /// 获取版本列表：磁盘缓存（24h TTL）命中即回，并触发后台静默刷新；
    /// 否则按下载源策略双源竞速（主源立即，副源延迟 5 秒加入，单源 8 秒超时不重试）。
    /// </summary>
    public async Task<IReadOnlyList<VersionSummary>> GetVersionListAsync(
        DownloadPolicy policy,
        CancellationToken cancellationToken = default)
    {
        var effectivePolicy = policy with { MojangFast = policy.MojangFast || DownloadSourceMonitor.MojangFast };
        if (_versionListCache is not null)
        {
            var cached = await _versionListCache.TryReadAsync(cancellationToken).ConfigureAwait(false);
            if (cached is not null && DateTimeOffset.Now - cached.Value.FetchedAt < VersionListCacheTtl)
            {
                _ = RefreshVersionListQuietlyAsync(effectivePolicy);
                return cached.Value.Manifest.Versions;
            }
        }

        var manifest = await FetchVersionListAsync(effectivePolicy, cancellationToken).ConfigureAwait(false);
        return manifest.Versions;
    }

    /// <summary>后台静默刷新版本列表缓存（失败忽略）。</summary>
    private async Task RefreshVersionListQuietlyAsync(DownloadPolicy policy)
    {
        if (_versionListCache is null || !await _refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            var manifest = await FetchVersionListAsync(policy, CancellationToken.None).ConfigureAwait(false);
            await _versionListCache.WriteAsync(manifest, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DownloadException or IOException or InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            _logger.Warn($"版本列表后台刷新失败（可忽略）：{ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>竞速获取并解析版本清单，随后尽力合并 UVMC 未收录版本。</summary>
    private async Task<VersionManifest> FetchVersionListAsync(DownloadPolicy policy, CancellationToken cancellationToken)
    {
        var urls = DownloadSources.GetVersionManifestUrls(policy.VersionListSource, policy.MojangFast);
        var primaryUrl = urls[0];
        var secondaryUrl = urls.Count > 1 ? urls[1] : null;
        var primaryIsMojang = primaryUrl.Contains("mojang.com", StringComparison.Ordinal);

        var rawJson = await RaceFetchAsync(primaryUrl, secondaryUrl, cancellationToken)
            .ConfigureAwait(false);
        var manifest = JsonSerializer.Deserialize(rawJson, NetJsonContext.Default.VersionManifest)
                       ?? throw new DownloadException("版本清单解析失败");
        if (manifest.Versions.Count < 200)
        {
            // 与 PCL-CE 一致：清单异常缩水视为无效，交由另一源重取
            throw new DownloadException($"版本清单条目异常（{manifest.Versions.Count}）：{primaryUrl}");
        }

        _logger.Info($"版本清单已获取：{manifest.Versions.Count} 个版本（主源：{(primaryIsMojang ? "官方" : "镜像")}）");
        await MergeUnlistedVersionsAsync(manifest, cancellationToken).ConfigureAwait(false);

        if (_versionListCache is not null)
        {
            await _versionListCache.WriteAsync(manifest, cancellationToken).ConfigureAwait(false);
        }

        return manifest;
    }

    /// <summary>双源竞速：主源立即开始，副源延迟 5 秒加入，先成功者胜出。</summary>
    private async Task<string> RaceFetchAsync(
        string primaryUrl,
        string? secondaryUrl,
        CancellationToken cancellationToken)
    {
        if (secondaryUrl is null)
        {
            return await _client.GetStringOnceAsync(primaryUrl, ListTimeout, cancellationToken).ConfigureAwait(false);
        }

        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures = new CountdownFailures(2, completion);
        _ = RunListFetchAsync(primaryUrl, TimeSpan.Zero, failures, cancellationToken);
        _ = RunListFetchAsync(secondaryUrl, RaceJoinDelay, failures, cancellationToken);

        return await completion.Task.ConfigureAwait(false);
    }

    private async Task RunListFetchAsync(
        string url,
        TimeSpan delay,
        CountdownFailures failures,
        CancellationToken cancellationToken)
    {
        try
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var text = await _client.GetStringOnceAsync(url, ListTimeout, cancellationToken).ConfigureAwait(false);
            watch.Stop();
            if (url.Contains("mojang.com", StringComparison.Ordinal))
            {
                // 官方延迟探测：4 秒内应答则"官方优先(自动)"档在有效期内优先官方
                DownloadSourceMonitor.NotifyMojangLatency(watch.Elapsed);
                _logger.Info($"官方源延迟 {watch.ElapsedMilliseconds} ms");
            }

            failures.TrySetResult(text);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            failures.TryCancel(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or DownloadException)
        {
            failures.TryFail(ex);
        }
    }

    /// <summary>合并 UVMC 未收录版本（失败忽略；对应 PCL-CE _MergeUnlistedVersions）。</summary>
    private async Task MergeUnlistedVersionsAsync(VersionManifest manifest, CancellationToken cancellationToken)
    {
        if (_versionListCache is null)
        {
            return;
        }

        try
        {
            var cachedJson = await _versionListCache
                .TryReadTextAsync(_versionListCache.UnlistedCachePath, TimeSpan.FromDays(1), cancellationToken)
                .ConfigureAwait(false);
            if (cachedJson is null)
            {
                cachedJson = await _client
                    .GetStringOnceAsync(UnlistedVersionsUrl, ListTimeout, cancellationToken)
                    .ConfigureAwait(false);
                await _versionListCache
                    .WriteTextAsync(_versionListCache.UnlistedCachePath, cachedJson, cancellationToken)
                    .ConfigureAwait(false);
            }

            var unlisted = JsonSerializer.Deserialize(cachedJson, NetJsonContext.Default.VersionManifest);
            if (unlisted?.Versions is not { Count: > 0 })
            {
                return;
            }

            var knownIds = manifest.Versions.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var merged = 0;
            foreach (var version in unlisted.Versions)
            {
                if (knownIds.Add(version.Id))
                {
                    manifest.Versions.Add(version);
                    merged++;
                }
            }

            if (merged > 0)
            {
                _logger.Info($"已合并 {merged} 个未收录版本（UVMC）");
            }
        }
        catch (Exception ex) when (ex is DownloadException or HttpRequestException or IOException or InvalidOperationException or TaskCanceledException or JsonException)
        {
            _logger.Warn($"未收录版本合并失败（可忽略）：{ex.Message}");
        }
    }

    /// <summary>竞速失败计数：全部失败时把首个异常交给等待方。</summary>
    private sealed class CountdownFailures(int total, TaskCompletionSource<string> completion) : IDisposable
    {
        private int _remaining = total;
        private Exception? _firstError;

        public void TrySetResult(string text) => completion.TrySetResult(text);

        public void TryCancel(CancellationToken cancellationToken)
            => completion.TrySetCanceled(cancellationToken);

        public void TryFail(Exception error)
        {
            Interlocked.CompareExchange(ref _firstError, error, null);
            if (Interlocked.Decrement(ref _remaining) == 0)
            {
                completion.TrySetException(_firstError ?? new DownloadException("版本清单获取失败"));
            }
        }

        public void Dispose()
        {
        }
    }

    /// <summary>安装某一原版版本（已存在的文件会跳过）。</summary>
    /// <param name="targetName">实例目录名；null 或空表示使用版本 id（同版本重复安装时由调用方传入 26.3-1 等）。</param>
    public async Task InstallVanillaAsync(
        string gameDirectory,
        VersionSummary summary,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool downloadAssets = true,
        string? targetName = null)
    {
        var reporter = new ProgressReporter(progress);
        var displayName = string.IsNullOrWhiteSpace(targetName) ? summary.Id : targetName;
        var versionDirectory = Path.Combine(gameDirectory, "versions", displayName);
        var versionDirectoryExisted = Directory.Exists(versionDirectory);
        Directory.CreateDirectory(versionDirectory);

        try
        {
            reporter.Stage("获取版本信息");
            _logger.Info($"开始安装 {summary.Id} → {displayName}（版本列表源 {policy.VersionListSource} / 文件源 {policy.FileSource}）");
            var rawJson = await _client
                .GetStringAsync(DownloadSources.GetLauncherOrMetaUrls(summary.Url, policy), cancellationToken)
                .ConfigureAwait(false);
            // 1.16.5 的 authlib 2.1.28 存在兼容问题（对应 PCL-CE ToolFixAuthlib，默认开启）
            if (FixAuthlibEnabled && summary.Id.Equals("1.16.5", StringComparison.Ordinal))
            {
                rawJson = VersionJsonMerger.FixAuthlib(rawJson);
            }

            // 以别名（如 26.3-1）安装时改写 json 的 id，保证目录名、json 文件名与 id 三者一致
            if (!displayName.Equals(summary.Id, StringComparison.Ordinal))
            {
                rawJson = ReplaceVersionId(rawJson, displayName);
            }

            var jsonPath = Path.Combine(versionDirectory, displayName + ".json");
            await File.WriteAllTextAsync(jsonPath, rawJson, cancellationToken).ConfigureAwait(false);

            var info = JsonSerializer.Deserialize(rawJson, NetJsonContext.Default.VersionInfo)
                       ?? throw new DownloadException($"版本 JSON 解析失败：{summary.Id}");
            if (string.IsNullOrWhiteSpace(info.Id))
            {
                info.Id = displayName;
            }

            var environment = RuleEnvironment.Current(false);

            // 客户端 jar
            if (info.Downloads?.Client is not null)
            {
                reporter.Stage("下载客户端");
                var jarPath = Path.Combine(versionDirectory, displayName + ".jar");
                var client = info.Downloads.Client;
                var urls = DownloadSources.GetLauncherOrMetaUrls(client.Url, policy);
                reporter.BeginFile(displayName + ".jar", client.Size);
                await _client.DownloadFileAsync(
                        urls,
                        jarPath,
                        client.Size > 0 ? client.Size : null,
                        client.Sha1,
                        new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                        cancellationToken)
                    .ConfigureAwait(false);
                reporter.FileCompleted(client.Size > 0 ? client.Size : new FileInfo(jarPath).Length);
            }

            // 依赖库与 natives
            var libraries = LibraryResolver.CollectLibraries(info, environment, policy);
            reporter.Stage("下载依赖库", libraries.Count);
            var librariesDirectory = Path.Combine(gameDirectory, "libraries");
            var nativesDirectory = Path.Combine(versionDirectory, "natives");

            await ForEachParallelAsync(
                libraries,
                threads,
                async library =>
                {
                    var destination = Path.Combine(librariesDirectory, library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                    reporter.BeginFile(library.RelativePath, library.Size);
                    await _client.DownloadFileAsync(
                            library.Urls,
                            destination,
                            library.Size > 0 ? library.Size : null,
                            library.Sha1,
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                    reporter.FileCompleted(library.Size > 0 ? library.Size : 0);
                },
                cancellationToken).ConfigureAwait(false);

            // 解压 natives（仅旧版本 JSON 带 natives 字段）
            ExtractAllNatives(info, environment, policy, librariesDirectory, nativesDirectory);

            // 资源索引与资源对象
            if (downloadAssets && info.AssetIndex is not null)
            {
                await InstallAssetsAsync(gameDirectory, policy, threads, info.AssetIndex, reporter, cancellationToken)
                    .ConfigureAwait(false);
            }

            // 日志配置（失败不阻断安装）
            if (info.Logging?.Client?.File is not null)
            {
                try
                {
                    var logFile = info.Logging.Client.File;
                    var logDirectory = Path.Combine(gameDirectory, "assets", "log_configs");
                    var destination = Path.Combine(logDirectory, (logFile.Path ?? logFile.Id ?? "client") + ".xml");
                    await _client.DownloadFileAsync(
                            DownloadSources.GetLauncherOrMetaUrls(logFile.Url, policy),
                            destination,
                            logFile.Size > 0 ? logFile.Size : null,
                            logFile.Sha1,
                            null,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is DownloadException or IOException)
                {
                    _logger.Warn($"日志配置下载失败（可忽略）：{ex.Message}");
                }
            }

            reporter.Stage("安装完成");
            _logger.Info($"版本 {displayName} 安装完成");
        }
        catch (Exception ex) when (ex is OperationCanceledException or DownloadException or IOException or HttpRequestException)
        {
            // 取消/失败回滚：只清理本次新建的版本目录，避免留下"只有 json 没有 jar"的残缺实例
            if (!versionDirectoryExisted)
            {
                TryDeleteDirectory(versionDirectory, displayName);
            }

            throw;
        }
    }

    /// <summary>把版本 JSON 的 id 改为实际实例名（同版本重复安装为 26.3-1 时保持目录名与 id 一致）。</summary>
    private static string ReplaceVersionId(string json, string id)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject node)
            {
                return json;
            }

            node["id"] = id;
            return node.ToJsonString(JsonOptions);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    /// <summary>回滚本次安装新建的版本目录（文件被占用时只记录日志，交由下次启动清理）。</summary>
    private void TryDeleteDirectory(string directory, string versionId)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
                _logger.Info($"已回滚未完成的版本目录：{versionId}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn($"回滚版本目录失败：{versionId} · {ex.Message}");
        }
    }

    /// <summary>安装 Fabric（自动确保对应原版已安装；选择最新稳定 loader）。</summary>
    public Task<string> InstallFabricAsync(
        string gameDirectory,
        string gameVersion,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? targetName = null)
        => InstallFabricLikeAsync(
            gameDirectory,
            "fabric",
            gameVersion,
            null,
            targetName,
            policy,
            threads,
            progress,
            cancellationToken);

    /// <summary>
    /// 安装 Fabric 系加载器（Fabric / Quilt / LegacyFabric）：profile json 与原版 json 深合并，
    /// 生成自包含的最终版本 JSON 并下载加载器依赖库（对应 PCL-CE MergeJson 思路）。
    /// </summary>
    /// <param name="kind">fabric / quilt / legacyfabric。</param>
    /// <param name="gameVersion">Minecraft 版本。</param>
    /// <param name="loaderVersion">加载器版本；null 表示自动选择最新稳定版。</param>
    /// <param name="targetName">实例目录名；null 默认 {mc}-{kind}。</param>
    public async Task<string> InstallFabricLikeAsync(
        string gameDirectory,
        string kind,
        string gameVersion,
        string? loaderVersion,
        string? targetName,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default,
        bool downloadAssets = true)
    {
        var reporter = new ProgressReporter(progress);
        kind = kind.ToLowerInvariant();

        // 1) 确保原版已安装
        reporter.Stage("检查原版");
        var vanillaJsonPath = await EnsureVanillaAsync(
                gameDirectory,
                gameVersion,
                policy,
                threads,
                progress,
                cancellationToken,
                downloadAssets)
            .ConfigureAwait(false);

        // 2) 选择 loader 版本
        string version;
        if (!string.IsNullOrWhiteSpace(loaderVersion))
        {
            version = loaderVersion;
        }
        else
        {
            reporter.Stage($"获取 {kind} 版本");
            version = kind switch
            {
                "quilt" => await GetLatestFabricLikeVersionAsync(
                        "https://meta.quiltmc.org/v3/versions/loader/",
                        gameVersion,
                        cancellationToken).ConfigureAwait(false),
                "legacyfabric" => await GetLatestFabricLikeVersionAsync(
                        "https://meta.legacyfabric.net/v2/versions/loader/",
                        gameVersion,
                        cancellationToken).ConfigureAwait(false),
                _ => await GetLatestFabricLikeVersionAsync(
                        "https://meta.fabricmc.net/v2/versions/loader/",
                        gameVersion,
                        cancellationToken).ConfigureAwait(false)
            };
        }

        // 3) 获取 profile json（Fabric：BMCLAPI 在前，官方在后；Quilt/LegacyFabric 仅官方）
        reporter.Stage("获取加载器 profile");
        var safeName = gameVersion.Replace("∞", "infinite");
        var profileUrls = kind switch
        {
            "fabric" => new[]
            {
                $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{safeName}/{version}/profile/json",
                $"https://meta.fabricmc.net/v2/versions/loader/{safeName}/{version}/profile/json"
            },
            "quilt" => new[]
            {
                $"https://meta.quiltmc.org/v3/versions/loader/{safeName}/{version}/profile/json"
            },
            "legacyfabric" => new[]
            {
                $"https://meta.legacyfabric.net/v2/versions/loader/{safeName}/{version}/profile/json"
            },
            _ => throw new ArgumentException($"未知的 Fabric 系加载器：{kind}", nameof(kind))
        };
        var profileJsonText = await _client.GetStringAsync(profileUrls, cancellationToken).ConfigureAwait(false);
        var profileJson = JsonNode.Parse(profileJsonText) as JsonObject
                          ?? throw new DownloadException("加载器 profile 解析失败");

        // 4) 与原版 json 深合并，生成自包含版本 json
        var displayName = targetName;
        if (string.IsNullOrWhiteSpace(displayName))
        {
            displayName = kind == "fabric" ? $"{gameVersion}-fabric" : $"{gameVersion}-{kind}";
        }

        var versionDirectory = Path.Combine(gameDirectory, "versions", displayName);
        var versionDirectoryExisted = Directory.Exists(versionDirectory);
        Directory.CreateDirectory(versionDirectory);
        try
        {
            var vanillaJsonText = await File.ReadAllTextAsync(vanillaJsonPath, cancellationToken).ConfigureAwait(false);
            var vanillaJson = JsonNode.Parse(vanillaJsonText) as JsonObject
                              ?? throw new DownloadException($"原版版本 JSON 解析失败：{gameVersion}");
            var merged = VersionJsonMerger.Merge(displayName, vanillaJson, profileJson);
            await File.WriteAllTextAsync(
                    Path.Combine(versionDirectory, displayName + ".json"),
                    merged.ToJsonString(JsonOptions),
                    cancellationToken)
                .ConfigureAwait(false);

            // 5) 复制原版客户端 jar（合并后 json 的 jar 项已被移除）
            var vanillaJarPath = Path.Combine(gameDirectory, "versions", gameVersion, gameVersion + ".jar");
            var outputJarPath = Path.Combine(versionDirectory, displayName + ".jar");
            if (File.Exists(vanillaJarPath) && !File.Exists(outputJarPath))
            {
                File.Copy(vanillaJarPath, outputJarPath, true);
            }

            // 6) 下载 profile 声明的加载器依赖库（原版库已就位，重复合并项由去重跳过）
            var profile = JsonSerializer.Deserialize(profileJson.ToJsonString(JsonOptions), NetJsonContext.Default.VersionInfo)
                          ?? throw new DownloadException("加载器 profile 解析失败");
            var environment = RuleEnvironment.Current(false);
            var libraries = LibraryResolver.CollectLibraries(profile, environment, policy);
            reporter.Stage("下载加载器依赖", libraries.Count);
            await DownloadLibrariesAsync(libraries, gameDirectory, threads, reporter, cancellationToken)
                .ConfigureAwait(false);

            reporter.Stage("安装完成");
            _logger.Info($"{kind} {version}（{gameVersion}）安装完成：{displayName}");
            return displayName;
        }
        catch (Exception ex) when (ex is OperationCanceledException or DownloadException or IOException or HttpRequestException)
        {
            // 取消/失败回滚：只清理本次新建的实例目录
            if (!versionDirectoryExisted)
            {
                TryDeleteDirectory(versionDirectory, displayName);
            }

            throw;
        }
    }

    /// <summary>确保某 MC 版本的原版文件就绪；返回其版本 json 路径。</summary>
    private async Task<string> EnsureVanillaAsync(
        string gameDirectory,
        string gameVersion,
        DownloadPolicy policy,
        int threads,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken,
        bool downloadAssets = true)
    {
        var vanillaJsonPath = Path.Combine(gameDirectory, "versions", gameVersion, gameVersion + ".json");
        if (File.Exists(vanillaJsonPath))
        {
            return vanillaJsonPath;
        }

        var manifest = await GetVersionListAsync(policy, cancellationToken).ConfigureAwait(false);
        var summary = manifest.FirstOrDefault(x => x.Id.Equals(gameVersion, StringComparison.OrdinalIgnoreCase))
                      ?? throw new DownloadException($"版本清单中找不到 {gameVersion}");
        await InstallVanillaAsync(
                gameDirectory,
                summary,
                policy,
                threads,
                progress,
                cancellationToken,
                downloadAssets)
            .ConfigureAwait(false);
        return vanillaJsonPath;
    }

    /// <summary>
    /// 启动前修复（对应 PCL-CE 启动阶段的 DlClientFix）：补齐缺失的依赖库与资源索引文件。
    /// 返回修复后仍缺失的依赖库数量；0 表示可以继续启动。
    /// </summary>
    public async Task<int> RepairLaunchAssetsAsync(
        VersionInfo info,
        RuleEnvironment environment,
        string gameDirectory,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = new ProgressReporter(progress);
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");

        var libraries = LibraryResolver.CollectLibraries(info, environment, DownloadPolicy.Default);
        var missing = libraries
            .Where(x => !File.Exists(Path.Combine(librariesDirectory, x.RelativePath.Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.Info($"启动前补全：发现 {missing.Count} 个缺失依赖库，开始自动下载");
            reporter.Stage("补全依赖库", missing.Count);
            await DownloadLibrariesAsync(missing, gameDirectory, threads, reporter, cancellationToken)
                .ConfigureAwait(false);
        }

        // 资源索引缺失时单独补索引文件（资源对象由游戏首次启动时自行下载）
        var assetsIndexId = info.AssetIndex?.Id ?? info.Assets;
        if (!string.IsNullOrWhiteSpace(assetsIndexId) && info.AssetIndex is { } assetIndex
            && !File.Exists(Path.Combine(gameDirectory, "assets", "indexes", assetsIndexId + ".json")))
        {
            _logger.Info($"启动前补全：缺失资源索引 {assetsIndexId}.json，开始自动下载");
            reporter.Stage("补全资源索引");
            var indexPath = Path.Combine(gameDirectory, "assets", "indexes", assetsIndexId + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            await _client
                .DownloadFileAsync(
                    DownloadSources.GetLauncherOrMetaUrls(assetIndex.Url, DownloadPolicy.Default),
                    indexPath,
                    assetIndex.Size > 0 ? assetIndex.Size : null,
                    assetIndex.Sha1,
                    null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var stillMissing = libraries
            .Count(x => !File.Exists(Path.Combine(librariesDirectory, x.RelativePath.Replace('/', Path.DirectorySeparatorChar))));
        _logger.Info($"启动前补全完成：仍缺失 {stillMissing} 个依赖库");
        return stillMissing;
    }

    /// <summary>并行下载库文件（已存在且大小匹配的自动跳过）。</summary>
    private async Task DownloadLibrariesAsync(
        IReadOnlyList<LibraryFile> libraries,
        string gameDirectory,
        int threads,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        await ForEachParallelAsync(
            libraries,
            threads,
            async library =>
            {
                var destination = Path.Combine(librariesDirectory, library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                reporter.BeginFile(library.RelativePath, library.Size);
                await _client.DownloadFileAsync(
                        library.Urls,
                        destination,
                        library.Size > 0 ? library.Size : null,
                        library.Sha1,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
                reporter.FileCompleted(library.Size > 0 ? library.Size : 0);
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>拉取 Fabric 系 meta 的 loader 列表并取最新稳定版。</summary>
    private async Task<string> GetLatestFabricLikeVersionAsync(
        string loaderUrlPrefix,
        string gameVersion,
        CancellationToken cancellationToken)
    {
        var loaderJson = await _client
            .GetStringAsync(loaderUrlPrefix + Uri.EscapeDataString(gameVersion), cancellationToken)
            .ConfigureAwait(false);
        var loaders = JsonSerializer.Deserialize(loaderJson, NetJsonContext.Default.ListFabricLoaderEntry)
                      ?? new List<FabricLoaderEntry>();
        var loader = loaders.FirstOrDefault(x => x.Loader?.Stable == true)?.Loader
                     ?? loaders.FirstOrDefault()?.Loader;
        if (loader is null || string.IsNullOrWhiteSpace(loader.Version))
        {
            throw new DownloadException($"找不到 {gameVersion} 可用的加载器版本");
        }

        return loader.Version;
    }

    private async Task InstallAssetsAsync(
        string gameDirectory,
        DownloadPolicy policy,
        int threads,
        AssetIndexInfo assetIndex,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Stage("下载资源索引");
        var indexJson = await _client
            .GetStringAsync(DownloadSources.GetLauncherOrMetaUrls(assetIndex.Url, policy), cancellationToken)
            .ConfigureAwait(false);
        var indexesDirectory = Path.Combine(gameDirectory, "assets", "indexes");
        Directory.CreateDirectory(indexesDirectory);
        var indexPath = Path.Combine(indexesDirectory, assetIndex.Id + ".json");
        await File.WriteAllTextAsync(indexPath, indexJson, cancellationToken).ConfigureAwait(false);

        var index = JsonSerializer.Deserialize(indexJson, NetJsonContext.Default.AssetIndex)
                    ?? throw new DownloadException("资源索引解析失败");
        if (index.Objects is null || index.Objects.Count == 0)
        {
            return;
        }

        var uniqueObjects = index.Objects.Values
            .GroupBy(x => x.Hash, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();

        reporter.Stage("下载资源文件", uniqueObjects.Count);
        var objectsDirectory = Path.Combine(gameDirectory, "assets", "objects");
        await ForEachParallelAsync(
            uniqueObjects,
            threads,
            async obj =>
            {
                var prefix = obj.Hash[..2];
                var destination = Path.Combine(objectsDirectory, prefix, obj.Hash);
                var officialUrl = $"{DownloadSources.OfficialAssets}/{prefix}/{obj.Hash}";
                var urls = DownloadSources.GetAssetUrls(officialUrl, policy);
                reporter.BeginFile(obj.Hash, obj.Size);
                await _client.DownloadFileAsync(urls, destination, obj.Size, obj.Hash, null, cancellationToken)
                    .ConfigureAwait(false);
                reporter.FileCompleted(obj.Size);
            },
            cancellationToken).ConfigureAwait(false);

        // 旧版本（1.6 以前）需要虚拟资源目录
        if (index.Virtual || index.MapToResources)
        {
            reporter.Stage("复制旧版资源");
            var objectsDirectoryFull = Path.Combine(gameDirectory, "assets", "objects");
            var targetDirectory = index.MapToResources
                ? Path.Combine(gameDirectory, "resources")
                : Path.Combine(gameDirectory, "assets", "virtual", assetIndex.Id);
            foreach (var pair in index.Objects)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var sourceFile = Path.Combine(objectsDirectoryFull, pair.Value.Hash[..2], pair.Value.Hash);
                var destination = Path.Combine(targetDirectory, pair.Key.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(sourceFile))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? targetDirectory);
                File.Copy(sourceFile, destination, true);
            }
        }
    }

    private void ExtractAllNatives(
        VersionInfo info,
        RuleEnvironment environment,
        DownloadPolicy policy,
        string librariesDirectory,
        string nativesDirectory)
    {
        foreach (var library in info.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment) || library.Natives is null)
            {
                continue;
            }

            var nativeFile = LibraryResolver.ResolveNative(library, environment, policy);
            if (nativeFile is null)
            {
                continue;
            }

            var archivePath = Path.Combine(librariesDirectory, nativeFile.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(archivePath))
            {
                continue;
            }

            ExtractNativeArchive(archivePath, nativesDirectory, library.Extract?.Exclude);
        }
    }

    private void ExtractNativeArchive(string archivePath, string nativesDirectory, IReadOnlyList<string>? excludes)
    {
        try
        {
            Directory.CreateDirectory(nativesDirectory);
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var name = entry.FullName;
                if (string.IsNullOrEmpty(entry.Name) || name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (excludes is not null && excludes.Any(prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var destination = Path.GetFullPath(Path.Combine(nativesDirectory, name.Replace('/', Path.DirectorySeparatorChar)));
                if (!destination.StartsWith(Path.GetFullPath(nativesDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? nativesDirectory);
                entry.ExtractToFile(destination, true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _logger.Warn($"解压 natives 失败：{archivePath} · {ex.Message}");
        }
    }

    private static async Task ForEachParallelAsync<T>(
        IReadOnlyCollection<T> items,
        int threads,
        Func<T, Task> action,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        var queue = new ConcurrentQueue<T>(items);
        var workerCount = Math.Clamp(threads, 1, Math.Max(1, items.Count));
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (!cancellationToken.IsCancellationRequested && queue.TryDequeue(out var item))
            {
                await action(item).ConfigureAwait(false);
            }
        });

        await Task.WhenAll(workers).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    /// <summary>线程安全的进度聚合（步骤内计数由 <see cref="InstallProgressCounter"/> 统一实现）。</summary>
    private sealed class ProgressReporter
    {
        private readonly InstallProgressCounter _counter;

        public ProgressReporter(IProgress<InstallProgress>? progress)
        {
            _counter = new InstallProgressCounter(progress);
        }

        public void Stage(string stage, int totalFiles = 0) => _counter.Stage(stage, totalFiles);

        public void BeginFile(string item, long expectedBytes) => _counter.BeginFile(item, expectedBytes);

        public void FileBytes(long received, long total) => _counter.FileBytes(received, total);

        public void FileCompleted(long bytes) => _counter.FileCompleted(bytes);
    }
}
