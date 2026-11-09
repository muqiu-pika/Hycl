using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Core.Plugins;

/// <summary>插件启用状态（plugins-state.json）：只记录被禁用的 id，未记录视为启用。</summary>
public sealed class PluginState
{
    public List<string> Disabled { get; init; } = [];
}

/// <summary>
/// 工具页的插件入口描述：只有已启用的插件才会出现在这里。
/// <paramref name="FeaturePageFactory"/> 延迟创建「点进去」后的功能/设置页控件，
/// 避免启动加载插件时就构造 Avalonia 控件并触发插件内部的后台校验。
/// </summary>
public sealed record PluginEntryInfo(
    string Id,
    string Title,
    string Description,
    string Version,
    Func<object?>? FeaturePageFactory)
{
    public bool HasFeaturePage => FeaturePageFactory is not null;
}

/// <summary>清单条目 + 本地安装态的聚合视图（设置页插件工坊渲染用）。</summary>
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

            if (Installed.LoadError is not null)
            {
                return PluginInstallState.LoadFailed;
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
    /// <summary>文件已安装但程序集加载失败（ABI 不匹配、依赖缺失等），工具页不会有入口。</summary>
    LoadFailed,
}

/// <summary>
/// 本机已安装插件的元数据（来自 plugin.json）。
/// LoadError 非空表示加载失败，插件工坊据此显示原因并提供「重试加载」。
/// </summary>
public sealed record InstalledPluginInfo(
    string Id,
    string DisplayName,
    string Version,
    int Abi,
    string Directory,
    bool IsLoaded,
    string? LoadError = null);

/// <summary>插件包内的 plugin.json。</summary>
public sealed class PluginPackageManifest
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string Version { get; init; } = "";

    public int Abi { get; init; } = HyclOnlineDefaults.CurrentAbi;

    /// <summary>入口程序集文件名（相对插件目录），默认 plugin.dll。</summary>
    public string Entry { get; init; } = "plugin.dll";

    /// <summary>
    /// 入口类型全名（可选）。指定后宿主只解析这一个类型，避免 GetExportedTypes()
    /// 为枚举所有导出类型而强制解析无关依赖（单文件发布下尤其脆弱）。
    /// 旧包留空时按 ABI 约定反射查找 IHyclPlugin 实现。
    /// </summary>
    public string Type { get; init; } = "";

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
    private readonly Dictionary<string, PluginEntryInfo> _entries = new(StringComparer.Ordinal);
    private readonly HashSet<string> _disabled = new(StringComparer.Ordinal);

    /// <summary>已启用插件的工具页入口（按加载顺序；绑定到工具页 ItemsControl）。</summary>
    public ObservableCollection<PluginEntryInfo> EnabledPlugins { get; } = [];

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
        LoadState();
    }

    public PluginManifestClient Manifests => _manifests;

    public HyclOnlineManifest? Manifest => _manifests.Cached;

    public IReadOnlyDictionary<string, InstalledPluginInfo> Installed => _installed;

    /// <summary>需要重启启动器才能完成更新的插件（已加载插件被覆盖安装时标记）。</summary>
    public bool IsRestartRequired(string pluginId) => _restartRequired.Contains(pluginId);

    // ---------- 启用 / 禁用 ----------

    /// <summary>插件是否启用（默认启用，仅显式禁用的 id 落盘）。</summary>
    public bool IsEnabled(string pluginId) => !_disabled.Contains(pluginId);

    /// <summary>插件实例是否仍在进程中（卸载后为 false；禁用不影响）。</summary>
    public bool IsLoaded(string pluginId) => _loaded.ContainsKey(pluginId);

    /// <summary>
    /// 启用/禁用插件：只控制工具页入口的显示，不卸载程序集、不停止插件内部服务
    /// （避免反复切换破坏插件状态；彻底停止请用卸载）。
    /// </summary>
    public void SetEnabled(string pluginId, bool enabled)
    {
        var changed = enabled ? _disabled.Remove(pluginId) : _disabled.Add(pluginId);
        if (!changed)
        {
            return;
        }

        SaveState();
        RefreshEnabledPlugins();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
        _logger.Info($"[插件] {pluginId} 已{(enabled ? "启用" : "禁用")}");
    }

    private string StateFilePath => Path.Combine(_paths.DataDirectory, "plugins-state.json");

    private void LoadState()
    {
        try
        {
            if (!File.Exists(StateFilePath))
            {
                return;
            }

            var state = JsonSerializer.Deserialize(
                File.ReadAllText(StateFilePath), NetJsonContext.Default.PluginState);
            foreach (var id in state?.Disabled ?? [])
            {
                if (!string.IsNullOrWhiteSpace(id))
                {
                    _disabled.Add(id);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.Warn("[插件] 启用状态读取失败，按全部启用处理：" + ex.Message);
        }
    }

    private void SaveState()
    {
        try
        {
            var state = new PluginState { Disabled = _disabled.Order(StringComparer.Ordinal).ToList() };
            File.WriteAllText(StateFilePath,
                JsonSerializer.Serialize(state, NetJsonContext.Default.PluginState));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Warn("[插件] 启用状态保存失败：" + ex.Message);
        }
    }

    /// <summary>按当前启用状态重建工具页入口集合（UI 线程调用）。</summary>
    private void RefreshEnabledPlugins()
    {
        EnabledPlugins.Clear();
        foreach (var entry in _entries.Values)
        {
            if (IsEnabled(entry.Id))
            {
                EnabledPlugins.Add(entry);
            }
        }
    }

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
                var id = Path.GetFileName(directory);
                _logger.Error("[插件] 加载失败：" + id, ex);
                MarkLoadFailed(id, directory, ex.Message);
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

        // 以下校验全部抛异常而不是静默 return：文件已经装到本机，若不能加载，
        // 必须让工坊显示「加载失败 + 原因」，否则用户只会看到「启用后工具页没有入口」。
        if (package.Abi != HyclOnlineDefaults.CurrentAbi)
        {
            throw new InvalidOperationException(
                $"插件 ABI {package.Abi} 与启动器 {HyclOnlineDefaults.CurrentAbi} 不匹配");
        }

        if (!SatisfiesMinLauncher(package.MinLauncherVersion))
        {
            throw new InvalidOperationException($"插件需要启动器 {package.MinLauncherVersion} 及以上");
        }

        var entryFile = Path.Combine(directory, string.IsNullOrWhiteSpace(package.Entry) ? "plugin.dll" : package.Entry);
        if (!IsInsideDirectory(directory, entryFile) || !File.Exists(entryFile))
        {
            throw new InvalidOperationException("入口程序集缺失：" + package.Entry);
        }

        // 单文件发布下框架/宿主程序集被嵌入 bundle，没有独立文件可探测；
        // 注册回退后插件的 AssemblyRef 可以复用进程内已加载的同名程序集。
        EnsurePluginAssemblyResolver();
        var assembly = Assembly.LoadFrom(entryFile);
        var pluginType = FindPluginType(assembly, package.Type);
        if (pluginType is null)
        {
            throw new InvalidOperationException("未找到 IHyclPlugin 实现（或入口类型无法解析）");
        }

        if (Activator.CreateInstance(pluginType) is not IHyclPlugin plugin)
        {
            throw new InvalidOperationException("插件实例化失败");
        }

        if (plugin.AbiVersion != HyclOnlineDefaults.CurrentAbi)
        {
            throw new InvalidOperationException($"运行时 ABI {plugin.AbiVersion} 与启动器不匹配");
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
        // 功能页工厂：新插件通过可选接口显式声明；旧包（ABI v1）回退到 CreateToolCard()，
        // 这样即使线上还是 v1.0.0 插件包，工具页也能「点进去」看到页面。
        Func<object?> featurePageFactory = plugin is IHyclPluginFeaturePage feature
            ? feature.CreateFeaturePage
            : plugin.CreateToolCard;
        var description = (_manifests.Cached?.Plugins ?? [])
            .FirstOrDefault(item => item.Id == package.Id)?.Description ?? "";
        _entries[package.Id] = new PluginEntryInfo(
            package.Id,
            string.IsNullOrWhiteSpace(package.Name) ? plugin.DisplayName : package.Name,
            description,
            package.Version,
            featurePageFactory);
        RefreshEnabledPlugins();
        _logger.Info($"[插件] 已加载：{package.Id} v{package.Version}");
    }

    // ---------- 加载辅助 ----------

    private static int _resolverRegistered;

    /// <summary>
    /// 单文件发布时框架/宿主程序集被嵌入 bundle，默认 ALC 无法按路径探测到它们；
    /// 插件的 AssemblyRef 解析失败时复用进程内已加载的同名程序集（契约、Avalonia、框架程序集）。
    /// </summary>
    private static void EnsurePluginAssemblyResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0)
        {
            return;
        }

        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            foreach (var loaded in context.Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return loaded;
                }
            }

            return null;
        };
    }

    /// <summary>
    /// 查找插件入口类型。优先用 plugin.json 的 type 精确解析（只碰一个类型）；
    /// 旧包回退到导出类型枚举，并对 ReflectionTypeLoadException 取已成功加载的部分。
    /// </summary>
    private static Type? FindPluginType(Assembly assembly, string? declaredType)
    {
        if (!string.IsNullOrWhiteSpace(declaredType))
        {
            var exact = assembly.GetType(declaredType.Trim(), throwOnError: false, ignoreCase: false);
            if (exact is not null && !exact.IsAbstract && typeof(IHyclPlugin).IsAssignableFrom(exact))
            {
                return exact;
            }
        }

        try
        {
            return assembly.GetExportedTypes()
                .FirstOrDefault(type => !type.IsAbstract && typeof(IHyclPlugin).IsAssignableFrom(type));
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.FirstOrDefault(type => type is not null
                && !type.IsAbstract
                && typeof(IHyclPlugin).IsAssignableFrom(type));
        }
    }

    /// <summary>记录「文件已安装但加载失败」，让插件工坊能显示原因并提供重试。</summary>
    private void MarkLoadFailed(string pluginId, string directory, string error)
    {
        var package = ReadPackageManifest(directory);
        _installed[pluginId] = new InstalledPluginInfo(
            pluginId,
            string.IsNullOrWhiteSpace(package?.Name) ? pluginId : package!.Name,
            package?.Version ?? "",
            package?.Abi ?? HyclOnlineDefaults.CurrentAbi,
            directory,
            IsLoaded: false,
            LoadError: error);
        _entries.Remove(pluginId);
        if (_loaded.Remove(pluginId, out var stale))
        {
            // 加载流程中途失败时可能已经完成 InitializeAsync，顺手关闭，避免后台服务残留。
            try
            {
                _ = stale.ShutdownAsync();
            }
            catch
            {
                // 清理失败不能覆盖真正的加载错误
            }
        }

        RefreshEnabledPlugins();
    }

    /// <summary>工坊「重试加载」：不改文件，只重新走一次加载流程（修复依赖后无需重装）。</summary>
    public async Task<bool> RetryLoadAsync(string pluginId)
    {
        var directory = Path.Combine(_paths.PluginsDirectory, pluginId);
        try
        {
            await TryLoadAsync(directory).ConfigureAwait(true);
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            _logger.Info("[插件] 重试加载成功：" + pluginId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error("[插件] 重试加载失败：" + pluginId, ex);
            MarkLoadFailed(pluginId, directory, ex.Message);
            PluginsChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
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
                    // 文件已安装：加载失败不回滚安装，但必须把原因落到安装态，
                    // 否则工坊只显示「已安装」而工具页没有入口，用户无从排查。
                    _logger.Error("[插件] 安装后加载失败：" + pluginId, ex);
                    MarkLoadFailed(pluginId, finalDirectory, ex.Message);
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

        // 同步移除工具页入口与功能页，否则卸载后界面会残留已失效的控件。
        _entries.Remove(pluginId);
        if (_disabled.Remove(pluginId))
        {
            SaveState();
        }

        RefreshEnabledPlugins();

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
