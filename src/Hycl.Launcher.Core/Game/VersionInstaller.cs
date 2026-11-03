using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>
/// 版本安装：原版（客户端、依赖库、资源、日志配置）与 Fabric。
/// 这是 PCL-CE 下载模块的跨平台重写，不依赖 Windows API。
/// </summary>
public sealed class VersionInstaller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DownloadClient _client;
    private readonly LauncherLogger _logger;

    public VersionInstaller(DownloadClient client, LauncherLogger logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyList<VersionSummary>> GetVersionListAsync(
        DownloadSourceKind source,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _client
            .GetJsonAsync<VersionManifest>(DownloadSources.GetVersionManifestUrl(source), cancellationToken)
            .ConfigureAwait(false);
        return manifest.Versions;
    }

    /// <summary>安装某一原版版本（已存在的文件会跳过）。</summary>
    public async Task InstallVanillaAsync(
        string gameDirectory,
        VersionSummary summary,
        DownloadSourceKind source,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = new ProgressReporter(progress);
        var versionDirectory = Path.Combine(gameDirectory, "versions", summary.Id);
        Directory.CreateDirectory(versionDirectory);

        reporter.Stage("获取版本信息");
        _logger.Info($"开始安装 {summary.Id}（源：{source}）");
        var rawJson = await _client
            .GetStringAsync(DownloadSources.GetVersionJsonUrl(source, summary.Id, summary.Url), cancellationToken)
            .ConfigureAwait(false);

        var jsonPath = Path.Combine(versionDirectory, summary.Id + ".json");
        await File.WriteAllTextAsync(jsonPath, rawJson, cancellationToken).ConfigureAwait(false);

        var info = JsonSerializer.Deserialize<VersionInfo>(rawJson, JsonOptions)
                   ?? throw new DownloadException($"版本 JSON 解析失败：{summary.Id}");
        if (string.IsNullOrWhiteSpace(info.Id))
        {
            info.Id = summary.Id;
        }

        var environment = RuleEnvironment.Current(false);

        // 客户端 jar
        if (info.Downloads?.Client is not null)
        {
            reporter.Stage("下载客户端");
            var jarPath = Path.Combine(versionDirectory, summary.Id + ".jar");
            var client = info.Downloads.Client;
            var url = DownloadSources.GetClientJarUrl(source, summary.Id, client.Url);
            reporter.BeginFile(summary.Id + ".jar", client.Size);
            await _client.DownloadFileAsync(
                    url,
                    jarPath,
                    client.Size > 0 ? client.Size : null,
                    client.Sha1,
                    new Progress<DownloadByteProgress>(value => reporter.FileBytes(value.ReceivedBytes, value.TotalBytes)),
                    cancellationToken)
                .ConfigureAwait(false);
            reporter.FileCompleted(client.Size > 0 ? client.Size : new FileInfo(jarPath).Length);
        }

        // 依赖库与 natives
        var libraries = LibraryResolver.CollectLibraries(info, environment, source);
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
                        library.Url,
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
        ExtractAllNatives(info, environment, librariesDirectory, nativesDirectory);

        // 资源索引与资源对象
        if (info.AssetIndex is not null)
        {
            await InstallAssetsAsync(gameDirectory, source, threads, info.AssetIndex, reporter, cancellationToken)
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
                        logFile.Url,
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
        _logger.Info($"版本 {summary.Id} 安装完成");
    }

    /// <summary>安装 Fabric（自动确保对应原版已安装）。</summary>
    public async Task<string> InstallFabricAsync(
        string gameDirectory,
        string gameVersion,
        DownloadSourceKind source,
        int threads,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var reporter = new ProgressReporter(progress);
        reporter.Stage("检查原版");
        var baseJson = Path.Combine(gameDirectory, "versions", gameVersion, gameVersion + ".json");
        if (!File.Exists(baseJson))
        {
            var manifest = await GetVersionListAsync(source, cancellationToken).ConfigureAwait(false);
            var summary = manifest.FirstOrDefault(x => x.Id.Equals(gameVersion, StringComparison.OrdinalIgnoreCase))
                          ?? throw new DownloadException($"版本清单中找不到 {gameVersion}");
            await InstallVanillaAsync(gameDirectory, summary, source, threads, progress, cancellationToken)
                .ConfigureAwait(false);
        }

        reporter.Stage("获取 Fabric 版本");
        var loaderJson = await _client.GetStringAsync(
                $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}",
                cancellationToken)
            .ConfigureAwait(false);
        var loaders = JsonSerializer.Deserialize<List<FabricLoaderEntry>>(loaderJson, JsonOptions) ?? new List<FabricLoaderEntry>();
        var loader = loaders.FirstOrDefault(x => x.Loader?.Stable == true)?.Loader ?? loaders.FirstOrDefault()?.Loader;
        if (loader is null || string.IsNullOrWhiteSpace(loader.Version))
        {
            throw new DownloadException($"找不到 {gameVersion} 可用的 Fabric Loader");
        }

        var profileUrl = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loader.Version)}/profile/json";
        var profileJson = await _client.GetStringAsync(profileUrl, cancellationToken).ConfigureAwait(false);
        var profile = JsonSerializer.Deserialize<VersionInfo>(profileJson, JsonOptions)
                      ?? throw new DownloadException("Fabric profile 解析失败");
        var profileId = profile.Id;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            profileId = $"fabric-loader-{loader.Version}-{gameVersion}";
            profile.Id = profileId;
        }

        var versionDirectory = Path.Combine(gameDirectory, "versions", profileId);
        Directory.CreateDirectory(versionDirectory);
        await File.WriteAllTextAsync(Path.Combine(versionDirectory, profileId + ".json"), profileJson, cancellationToken)
            .ConfigureAwait(false);

        reporter.Stage("下载 Fabric 依赖");
        var environment = RuleEnvironment.Current(false);
        var libraries = LibraryResolver.CollectLibraries(profile, environment, source);
        var librariesDirectory = Path.Combine(gameDirectory, "libraries");
        reporter.Stage("下载 Fabric 依赖", libraries.Count);
        await ForEachParallelAsync(
            libraries,
            threads,
            async library =>
            {
                var destination = Path.Combine(librariesDirectory, library.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                reporter.BeginFile(library.RelativePath, library.Size);
                await _client.DownloadFileAsync(
                        library.Url,
                        destination,
                        library.Size > 0 ? library.Size : null,
                        library.Sha1,
                        null,
                        cancellationToken)
                    .ConfigureAwait(false);
                reporter.FileCompleted(library.Size > 0 ? library.Size : 0);
            },
            cancellationToken).ConfigureAwait(false);

        reporter.Stage("安装完成");
        _logger.Info($"Fabric {loader.Version}（{gameVersion}）安装完成");
        return profileId;
    }

    private async Task InstallAssetsAsync(
        string gameDirectory,
        DownloadSourceKind source,
        int threads,
        AssetIndexInfo assetIndex,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        reporter.Stage("下载资源索引");
        var indexUrl = DownloadSources.GetAssetIndexUrl(source, assetIndex.Id, assetIndex.Url);
        var indexJson = await _client.GetStringAsync(indexUrl, cancellationToken).ConfigureAwait(false);
        var indexesDirectory = Path.Combine(gameDirectory, "assets", "indexes");
        Directory.CreateDirectory(indexesDirectory);
        var indexPath = Path.Combine(indexesDirectory, assetIndex.Id + ".json");
        await File.WriteAllTextAsync(indexPath, indexJson, cancellationToken).ConfigureAwait(false);

        var index = JsonSerializer.Deserialize<AssetIndex>(indexJson, JsonOptions)
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
                var url = source == DownloadSourceKind.Official
                    ? $"{DownloadSources.OfficialAssets}/{prefix}/{obj.Hash}"
                    : $"{DownloadSources.BmclapiBase}/assets/{prefix}/{obj.Hash}";
                reporter.BeginFile(obj.Hash, obj.Size);
                await _client.DownloadFileAsync(url, destination, obj.Size, obj.Hash, null, cancellationToken)
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
        string librariesDirectory,
        string nativesDirectory)
    {
        foreach (var library in info.Libraries)
        {
            if (!RuleEvaluator.IsAllowed(library.Rules, environment) || library.Natives is null)
            {
                continue;
            }

            var nativeFile = LibraryResolver.ResolveNative(library, environment);
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

    /// <summary>线程安全的进度聚合。</summary>
    private sealed class ProgressReporter
    {
        private readonly IProgress<InstallProgress>? _progress;
        private string _stage = "";
        private int _filesDone;
        private int _filesTotal;
        private long _bytesDone;
        private long _bytesTotal;
        private string _currentItem = "";

        public ProgressReporter(IProgress<InstallProgress>? progress)
        {
            _progress = progress;
        }

        public void Stage(string stage, int totalFiles = 0)
        {
            _stage = stage;
            _filesDone = 0;
            _filesTotal = totalFiles;
            _bytesDone = 0;
            _bytesTotal = 0;
            _currentItem = "";
            Report();
        }

        public void BeginFile(string item, long expectedBytes)
        {
            _currentItem = item;
            if (expectedBytes > 0)
            {
                Interlocked.Add(ref _bytesTotal, expectedBytes);
            }

            Report();
        }

        public void FileBytes(long received, long total)
        {
            _currentItem = $"{Math.Round((double)received / (1024 * 1024), 1)} MB / {Math.Round((double)total / (1024 * 1024), 1)} MB";
            Report();
        }

        public void FileCompleted(long bytes)
        {
            Interlocked.Increment(ref _filesDone);
            if (bytes > 0)
            {
                Interlocked.Add(ref _bytesDone, bytes);
            }

            Report();
        }

        private void Report()
        {
            _progress?.Report(new InstallProgress(
                _stage,
                _currentItem,
                Volatile.Read(ref _filesDone),
                Volatile.Read(ref _filesTotal),
                Volatile.Read(ref _bytesDone),
                Volatile.Read(ref _bytesTotal)));
        }
    }
}
