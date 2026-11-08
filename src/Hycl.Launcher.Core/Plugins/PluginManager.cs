using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Core.Plugins;

/// <summary>清单条目 + 本地安装态的聚合视图（工具页插件中心渲染用）。</summary>
public sealed class PluginCatalogItem
{
    public PluginCatalogItem(HyclPluginEntry entry, InstalledPluginInfo? installed, bool restartRequired)
    {
        Entry = entry;
        Installed = installed;
        RestartRequired = restartRequired;
    }

    public HyclPluginEntry Entry { get; }

    public InstalledPluginInfo? Installed { get; }

    /// <summary>已装插件有新版本且需要重启启动器才能完成更新。</summary>
    public bool RestartRequired { get; }

    public string Id => Entry.Id;

    public string DisplayName => string.IsNullOrWhiteSpace(Entry.Name) ? Id : Entry.Name;

    /// <summary>未安装 / 已安装 / 可更新 / 更新待重启。</summary>
    public PluginInstallState State
    {
        get
        {
            if (Installed is null)
            {
                return PluginInstallState.NotInstalled;
            }

            if (RestartRequired)
            {
                return PluginInstallState.UpdatePendingRestart;
            }

            return string.IsNullOrWhiteSpace(Entry.Version) || Entry.Version == Installed.Version
                ? PluginInstallState.Installed
                : PluginInstallState.UpdateAvailable;
        }
    }
}

/// <summary>插件安装状态。</summary>
public enum PluginInstallState
{
    NotInstalled,
    Installed,
    UpdateAvailable,
    UpdatePendingRestart,
}

/// <summary>本机已安装插件的元数据（来自 plugin.json）。</summary>
public sealed record InstalledPluginInfo(
    string Id,
    string DisplayName,
    string Version,
    int Abi,
    string Directory,
    bool IsLoaded);

/// <summary>插件包内的 plugin.json。</summary>
public sealed class PluginPackageManifest
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Version { get; init; } = "";

    public int Abi { get; init; } = HyclOnlineDefaults.CurrentAbi;

    /// <summary>入口程序集文件名（相对插件目录），默认 plugin.dll。</summary>
    public string Entry { get; init; } = "plugin.dll";

    public string MinLauncherVersion { get; init; } = "";
}

/// <summary>
/// 插件管理器：清单发现 → 下载安装（SHA-256 锁定）→ 反射加载 → 工具卡聚合 → 退出清理。
/// 加载/安装涉及 UI 卡片创建，须在启动器 UI 线程调用；更新采用「重启生效」模型
/// （默认 ALC 无法卸载同标识程序集）。
/// </summary>
public sealed class PluginManager
{
    private const string StagingSuffix = ".new";
    private const string BackupSuffix = ".old";
    private const string DeleteSuffix = ".pending-delete";

    private readonly LauncherLogger _logger;
    private readonly LauncherPaths _paths;
    private readonly DownloadClient _downloadClient;
    private readonly AccountStore _accounts;
    private readonly LauncherConfig _config;
    private readonly SystemService _system;
    private readonly IAppDispatcher _dispatcher;
    private readonly PluginManifestClient _manifests;

    private readonly Dictionary<string, IHyclPlugin> _loaded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InstalledPluginInfo> _installed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _restartRequired = new(StringComparer.Ordinal);

    /// <summary>已加载插件的工具卡（按加载顺序；绑定到工具页 ItemsControl）。</summary>
    public ObservableCollection<object> ToolCards { get; } = [];

    /// <summary>安装/卸载/首次加载完成后触发（UI 线程）。</summary>
    public event EventHandler? PluginsChanged;

    public PluginManager(
        LauncherLogger logger,
        LauncherPaths paths,
        DownloadClient downloadClient,
        AccountStore accounts,
        LauncherConfig config,
        SystemService system,
        IAppDispatcher dispatcher,
        PluginManifestClient manifests)
    {
        _logger = logger;
        _paths = paths;
        _downloadClient = downloadClient;
        _accounts = accounts;
        _config = config;
        _system = system;
        _dispatcher = dispatcher;
        _manifests = manifests;
        CleanupLeftoverDirectories();
    }

    public PluginManifestClient Manifests => _manifests;

    public HyclOnlineManifest? Manifest => _manifests.Cached;

    public IReadOnlyDictionary<string, InstalledPluginInfo> Installed => _installed;

    /// <summary>需要重启启动器才能完成更新的插件（已加载插件被覆盖安装时标记）。</summary>
    public bool IsRestartRequired(string pluginId) => _restartRequired.Contains(pluginId);

    // ---------- 清单 ----------

    public Task<HyclOnlineManifest?> RefreshManifestAsync(CancellationToken cancellationToken = default)
        => _manifests.RefreshAsync(cancellationToken);

    /// <summary>按清单聚合当前目录状态；清单未拉取时只显示已安装插件。</summary>
    public IReadOnlyList<PluginCatalogItem> BuildCatalog()
    {
        var manifest = _manifests.Cached;
        var items = new List<PluginCatalogItem>();
        if (manifest is not null)
        {
            foreach (var entry in manifest.Plugins ?? [])
            {
                _installed.TryGetValue(entry.Id, out var installed);
                items.Add(new PluginCatalogItem(entry, installed, _restartRequired.Contains(entry.Id)));
            }
        }

        foreach (var installed in _installed.Values)
        {
            if (manifest is null || (manifest.Plugins ?? []).All(entry => entry.Id != installed.Id))
            {
                items.Add(new PluginCatalogItem(
                    new HyclPluginEntry { Id = installed.Id, Name = installed.DisplayName, Version = installed.Version },
                    installed,
                    false));
            }
        }

        return items;
    }

    // ---------- 启动加载 ----------

    /// <summary>启动时加载全部已安装插件（UI 线程调用；单个插件失败不影响其余）。</summary>
    public async Task LoadInstalledAsync()
    {
        var root = _paths.PluginsDirectory;
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if (directory.EndsWith(StagingSuffix, StringComparison.OrdinalIgnoreCase)
                || directory.EndsWith(BackupSuffix, StringComparison.OrdinalIgnoreCase)
                || directory.EndsWith(DeleteSuffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                await TryLoadAsync(directory).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Error("[插件] 加载失败：" + Path.GetFileName(directory), ex);
            }
        }

        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从安装目录加载并激活插件。
    /// 裁剪说明：插件程序集在运行时才进入进程，不在宿主裁剪图内（LoadFrom/GetExportedTypes/
    /// CreateInstance 的 IL2026/IL2072 为误报）；宿主只需保证契约接口类型完整（根类型不被裁剪）。
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "插件程序集运行时加载，不属于宿主裁剪图")]
    [UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "加载的程序集按 IHyclPlugin 契约实例化，公共无参构造由插件作者保证")]
    private async Task TryLoadAsync(string directory)
    {
        var package = ReadPackageManifest(directory);
        if (package is null || string.IsNullOrWhiteSpace(package.Id))
        {
            _logger.Warn("[插件] 跳过缺少 plugin.json 的目录：" + Path.GetFileName(directory));
            return;
        }

        if (package.Abi != HyclOnlineDefaults.CurrentAbi)
        {
            _logger.Warn($"[插件] {package.Id} ABI {package.Abi} 与启动器 {HyclOnlineDefaults.CurrentAbi} 不匹配，拒绝加载");
            return;
        }

        if (!SatisfiesMinLauncher(package.MinLauncherVersion))
        {
            _logger.Warn($"[插件] {package.Id} 需要启动器 {package.MinLauncherVersion} 及以上，拒绝加载");
            return;
        }

        var entryFile = Path.Combine(directory, string.IsNullOrWhiteSpace(package.Entry) ? "plugin.dll" : package.Entry);
        if (!IsInsideDirectory(directory, entryFile) || !File.Exists(entryFile))
        {
            _logger.Warn("[插件] " + package.Id + " 入口程序集缺失：" + package.Entry);
            return;
        }

        var assembly = Assembly.LoadFrom(entryFile);
        var pluginType = assembly
            .GetExportedTypes()
            .FirstOrDefault(type => !type.IsAbstract && typeof(IHyclPlugin).IsAssignableFrom(type));
        if (pluginType is null)
        {
            _logger.Warn("[插件] " + package.Id + " 未找到 IHyclPlugin 实现");
            return;
        }

        if (Activator.CreateInstance(pluginType) is not IHyclPlugin plugin)
        {
            _logger.Warn("[插件] " + package.Id + " 插件实例化失败");
            return;
        }

        if (plugin.AbiVersion != HyclOnlineDefaults.CurrentAbi)
        {
            _logger.Warn($"[插件] {package.Id} 运行时 ABI {plugin.AbiVersion} 与启动器不匹配，拒绝激活");
            return;
        }

        await plugin
            .InitializeAsync(new PluginContextAdapter(
                package.Id, _downloadClient, _logger, _paths, _accounts, _config, _system, _dispatcher, _manifests))
            .ConfigureAwait(true);
        _loaded[package.Id] = plugin;
        _installed[package.Id] = new InstalledPluginInfo(
            package.Id,
            string.IsNullOrWhiteSpace(package.Name) ? plugin.DisplayName : package.Name,
            package.Version,
            package.Abi,
            directory,
            IsLoaded: true);
        ToolCards.Add(plugin.CreateToolCard());
        _logger.Info($"[插件] 已加载：{package.Id} v{package.Version}");
    }

    // ---------- 安装 / 卸载 ----------

    /// <summary>
    /// 从清单安装/更新插件：下载 zip（SHA-256 校验）→ 解压暂存 → 校验 → 原子换目录。
    /// 已加载的插件覆盖安装后标记重启生效；未加载的立即可用。
    /// </summary>
    public async Task InstallAsync(string pluginId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifest = await _manifests.RefreshAsync(cancellationToken).ConfigureAwait(true)
            ?? throw new InvalidOperationException("插件清单不可用，请检查网络或清单源设置");
        var entry = manifest.Plugins.FirstOrDefault(item => item.Id == pluginId)
            ?? throw new InvalidOperationException("清单中没有该插件：" + pluginId);
        if (string.IsNullOrWhiteSpace(entry.Url))
        {
            throw new InvalidOperationException("该插件尚未发布安装包（清单 url 为空）");
        }

        var finalDirectory = Path.Combine(_paths.PluginsDirectory, pluginId);
        var staging = finalDirectory + StagingSuffix;
        var zipPath = staging + ".zip";
        CleanDirectory(staging);
        CleanFile(zipPath);
        try
        {
            IProgress<DownloadByteProgress>? inner = progress is null
                ? null
                : new Progress<DownloadByteProgress>(p => progress.Report(p.Percentage));
            // 主 url + 备用镜像（EdgeOne 优先时 GitHub Release 自动经镜像池展开，原站托底）
            // 注：source-gen 反序列化缺失的集合属性为 null，统一判空
            var downloadUrls = GitHubSources.Expand([entry.Url, ..(entry.Mirrors ?? [])]);
            await _downloadClient
                .DownloadFileAsync(
                    downloadUrls,
                    zipPath,
                    entry.Size > 0 ? entry.Size : null,
                    string.IsNullOrWhiteSpace(entry.Sha256) ? null : entry.Sha256,
                    inner,
                    cancellationToken)
                .ConfigureAwait(true);

            await Task.Run(() => ExtractZip(zipPath, staging), cancellationToken).ConfigureAwait(true);
            var package = ReadPackageManifest(staging)
                ?? throw new InvalidOperationException("安装包缺少 plugin.json");
            if (!string.Equals(package.Id, pluginId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"安装包 id（{package.Id}）与清单（{pluginId}）不一致");
            }

            if (package.Abi != HyclOnlineDefaults.CurrentAbi)
            {
                throw new InvalidOperationException($"插件 ABI {package.Abi} 与启动器 {HyclOnlineDefaults.CurrentAbi} 不匹配");
            }

            var backup = finalDirectory + BackupSuffix;
            if (Directory.Exists(finalDirectory))
            {
                CleanDirectory(backup);
                Directory.Move(finalDirectory, backup);
            }

            Directory.Move(staging, finalDirectory);
            if (_loaded.ContainsKey(pluginId))
            {
                _restartRequired.Add(pluginId);
                _logger.Info($"[插件] {pluginId} 已更新到 v{package.Version}，重启启动器后生效");
            }
            else
            {
                try
                {
                    await TryLoadAsync(finalDirectory).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    // 文件已安装：加载失败只记录（下次启动重试），不让安装整体回滚
                    _logger.Error("[插件] 安装后加载失败（重启启动器后将重试）：" + pluginId, ex);
                    _installed[pluginId] = new InstalledPluginInfo(
                        package.Id,
                        package.Name,
                        package.Version,
                        package.Abi,
                        finalDirectory,
                        IsLoaded: false);
                }
            }

            _manifests.Invalidate();
            PluginsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
            CleanDirectory(staging);
            throw;
        }
        finally
        {
            CleanFile(zipPath);
        }
    }

    /// <summary>卸载插件：立即从界面移除；目录删除失败（文件占用）则标记重启后删除。</summary>
    public async Task UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        if (_loaded.Remove(pluginId, out var plugin))
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await plugin.ShutdownAsync().WaitAsync(timeout.Token).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                _logger.Warn("[插件] 卸载时关闭失败：" + pluginId + "：" + ex.Message);
            }
        }

        var directory = Path.Combine(_paths.PluginsDirectory, pluginId);
        if (Directory.Exists(directory))
        {
            await Task.Run(() =>
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    var pending = directory + DeleteSuffix;
                    CleanDirectory(pending);
                    Directory.Move(directory, pending);
                }
            }, cancellationToken).ConfigureAwait(true);
        }

        _installed.Remove(pluginId);
        _restartRequired.Remove(pluginId);
        _logger.Info("[插件] 已卸载：" + pluginId);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 退出清理 ----------

    /// <summary>启动器退出前逐个关闭插件（每个限时 grace；并行执行）。</summary>
    public void ShutdownAll(TimeSpan grace)
    {
        if (_loaded.Count == 0)
        {
            return;
        }

        var tasks = _loaded.Values
            .Select(async plugin =>
            {
                try
                {
                    using var timeout = new CancellationTokenSource(grace);
                    await plugin.ShutdownAsync().WaitAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.Warn("[插件] 退出清理失败：" + plugin.Id + "：" + ex.Message);
                }
            })
            .ToArray();
        try
        {
            Task.WaitAll(tasks, grace + TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // 已逐个记录；退出路径不再上抛
        }
        catch (TimeoutException)
        {
        }
    }

    // ---------- 内部工具 ----------

    private PluginPackageManifest? ReadPackageManifest(string directory)
    {
        var path = Path.Combine(directory, "plugin.json");
        if (!File.Exists(path))
        {
            return null;
        }

        return JsonSerializer.Deserialize(File.ReadAllText(path), NetJsonContext.Default.PluginPackageManifest);
    }

    private static void ExtractZip(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        var fullDestination = Path.GetFullPath(destination);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith("/", StringComparison.Ordinal))
            {
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(fullDestination, entry.FullName));
            if (!target.StartsWith(fullDestination + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !string.Equals(target, fullDestination, StringComparison.Ordinal))
            {
                throw new IOException("安装包含有越界路径：" + entry.FullName);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static bool IsInsideDirectory(string directory, string path)
    {
        var fullDirectory = Path.GetFullPath(directory);
        var fullPath = Path.GetFullPath(path);
        return fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool SatisfiesMinLauncher(string minVersion)
    {
        if (string.IsNullOrWhiteSpace(minVersion))
        {
            return true;
        }

        if (!Version.TryParse(minVersion.TrimStart('v', 'V'), out var min))
        {
            return true;
        }

        return !Version.TryParse(LaunchService.LauncherVersion, out var current) || current >= min;
    }

    private void CleanupLeftoverDirectories()
    {
        var root = _paths.PluginsDirectory;
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root).ToArray())
        {
            var name = Path.GetFileName(directory);
            if (name.EndsWith(BackupSuffix, StringComparison.Ordinal)
                || name.EndsWith(DeleteSuffix, StringComparison.Ordinal))
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 文件仍被占用：保留目录，下次启动再清
                }
            }
        }
    }

    private void CleanDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("[插件] 目录清理失败：" + directory + "：" + ex.Message);
        }
    }

    private void CleanFile(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("[插件] 文件清理失败：" + file + "：" + ex.Message);
        }
    }
}
