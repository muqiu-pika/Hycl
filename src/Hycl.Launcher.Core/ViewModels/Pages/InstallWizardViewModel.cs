using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>下载页的版本分组（对应 Hycl (2) 的可折叠版本组）。</summary>
public partial class VersionGroupViewModel : ObservableObject
{
    public VersionGroupViewModel(string title, IReadOnlyList<VersionItemViewModel> items, bool isExpanded = false)
    {
        Title = title;
        Items = items;
        _isExpanded = isExpanded;
    }

    public string Title { get; }

    public IReadOnlyList<VersionItemViewModel> Items { get; }

    /// <summary>折叠时不渲染条目，避免一次性创建大量控件。</summary>
    public IReadOnlyList<VersionItemViewModel> VisibleItems => IsExpanded ? Items : [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleItems))]
    private bool _isExpanded;

    [ObservableProperty]
    private string _childErrorText = "";

    public string CountText => $"{Items.Count}";

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>版本列表或加载器列表中的单个条目。</summary>
public partial class VersionItemViewModel : ObservableObject
{
    public VersionItemViewModel(
        string title,
        string info,
        string iconKey,
        IReadOnlyList<string>? tags = null,
        object? payload = null,
        bool isSelected = false)
    {
        Title = title;
        Info = info;
        IconKey = iconKey;
        Tags = tags ?? [];
        Payload = payload;
        _isSelected = isSelected;
    }

    public string Title { get; }

    public string Info { get; }

    public string IconKey { get; }

    public IReadOnlyList<string> Tags { get; }

    /// <summary>业务载荷：Minecraft 版本为 <see cref="VersionSummary"/>，Fabric 为 <see cref="LoaderChoice"/>。</summary>
    public object? Payload { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>安装向导中选择的 Mod 加载器。</summary>
public sealed record LoaderChoice(string Kind, string DisplayName, object? Payload = null);

/// <summary>安装向导中的加载器卡片（对应 Hycl (2) 的 LoaderCardViewModel）。</summary>
public partial class LoaderCardViewModel : ObservableObject
{
    private readonly Func<Task<IReadOnlyList<VersionItemViewModel>>> _loader;
    private bool _loaded;

    public LoaderCardViewModel(
        string title,
        string iconKey,
        string description,
        string kind,
        bool isAccessory,
        Func<Task<IReadOnlyList<VersionItemViewModel>>> loader)
    {
        Title = title;
        IconKey = iconKey;
        Description = description;
        Kind = kind;
        IsAccessory = isAccessory;
        _loader = loader;
        Items = new ObservableCollection<VersionItemViewModel>();
    }

    public string Title { get; }

    public string IconKey { get; }

    public string Description { get; }

    public string Kind { get; }

    public bool IsAccessory { get; }

    public ObservableCollection<VersionItemViewModel> Items { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>附属卡在当前主加载器组合下是否可选（互斥规则）。</summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>列表加载总超时：超时后给出错误提示，避免不支持的版本长时间转圈。</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        IsLoading = true;
        ErrorText = "";
        try
        {
            var loadTask = _loader();
            var completed = await Task.WhenAny(loadTask, Task.Delay(LoadTimeout)).ConfigureAwait(true);
            if (completed != loadTask)
            {
                throw new Hycl.Launcher.Core.Game.Loaders.LoaderMetadataException(
                    "获取超时：该加载器可能不支持此版本，或当前网络不佳");
            }

            var items = await loadTask.ConfigureAwait(true);
            Items.Clear();
            foreach (var item in items)
            {
                Items.Add(item);
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [ObservableProperty]
    private string _selectedTitle = "";

    public string CountText => $"{Items.Count}";

    public void ResetSelection()
    {
        foreach (var item in Items)
        {
            item.IsSelected = false;
        }

        HasSelection = false;
        SelectedTitle = "";
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// 下载页安装向导：选择 Minecraft 版本与加载器。
/// 界面来自 Hycl (2)，安装动作改接现有 Core 的 <see cref="Game.VersionInstaller"/>。
/// </summary>
public partial class InstallWizardViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly InstallTaskViewModel _install;
    private readonly Action<string>? _toast;

    public InstallWizardViewModel(
        LauncherServices services,
        InstallTaskViewModel install,
        Action<string>? toast)
    {
        _services = services;
        _install = install;
        _toast = toast;
        McGroups = new ObservableCollection<VersionGroupViewModel>();
        LoaderCards = new ObservableCollection<LoaderCardViewModel>();
        CompanionCards = new ObservableCollection<LoaderCardViewModel>();
    }

    public ObservableCollection<VersionGroupViewModel> McGroups { get; }

    public ObservableCollection<LoaderCardViewModel> LoaderCards { get; }

    public ObservableCollection<LoaderCardViewModel> CompanionCards { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStep1))]
    [NotifyPropertyChangedFor(nameof(IsStep2))]
    [NotifyPropertyChangedFor(nameof(IsStep1Visible))]
    [NotifyPropertyChangedFor(nameof(IsStep2Visible))]
    [NotifyPropertyChangedFor(nameof(StepTitle))]
    private int _step = 1;

    public bool IsStep1 => Step == 1;

    public bool IsStep2 => Step == 2;

    /// <summary>步骤一的可见性（以显式属性名供 XAML 编译绑定使用）。</summary>
    public bool IsStep1Visible => IsStep1;

    public bool IsStep2Visible => IsStep2;

    public string StepTitle => IsStep1 ? "选择 Minecraft 版本" : "选择安装内容";

    [ObservableProperty]
    private bool _isMcListLoading;

    [ObservableProperty]
    private string _mcListError = "";

    [ObservableProperty]
    private string _selectedMcId = "";

    [ObservableProperty]
    private string _targetName = "";

    [ObservableProperty]
    private string _selectedLoaderName = "原版";

    [ObservableProperty]
    private bool _isLoaderLoading;

    [ObservableProperty]
    private string _loaderError = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaderPage))]
    private LoaderCardViewModel? _activeLoaderCard;

    public bool IsLoaderPage => ActiveLoaderCard is not null;

    private VersionSummary? _selectedMc;

    private IReadOnlyList<VersionSummary> _cachedVersions = [];

    private (int VersionList, int File) _cachedSource = (-1, -1);

    private VersionItemViewModel? _selectedLoaderItem;

    private VersionItemViewModel? _selectedOptiFineItem;

    private VersionItemViewModel? _selectedLiteLoaderItem;

    private LoaderCardViewModel? _fabricApiCard;

    private LoaderCardViewModel? _legacyFabricApiCard;

    private LoaderCardViewModel? _optiFineCard;

    private LoaderCardViewModel? _liteLoaderCard;

    private string? _companionModSlug;

    /// <summary>向导代际：切换 MC 版本或返回时自增，使陈旧的加载器列表请求作废。</summary>
    private int _wizardGeneration;

    /// <summary>加载器版本列表缓存（按 MC 版本 + 加载器种类）：重进向导时直接复用，不再重复请求。</summary>
    private readonly Dictionary<(string McId, string Kind), IReadOnlyList<LoaderVersionEntry>> _loaderEntryCache = new();

    /// <summary>在途的版本列表请求：同一键并发时复用同一任务，防止重复请求（对应 PCL-CE 的加载防抖）。</summary>
    private readonly Dictionary<(string McId, string Kind), Task<IReadOnlyList<LoaderVersionEntry>>> _loaderEntryInFlight = new();

    /// <summary>缓存纪元：下载源切换（Invalidate）时自增，使在途请求的结果不再写入旧缓存。</summary>
    private int _loaderCacheEpoch;

    /// <summary>加载 Minecraft 版本列表；已加载且下载源未变化时直接复用缓存。</summary>
    public async Task LoadVersionListAsync(bool force = false)
    {
        var config = _services.Config;
        if (!force && _cachedVersions.Count > 0 && _cachedSource == (config.VersionListSource, config.FileSource))
        {
            return;
        }

        IsMcListLoading = true;
        McListError = "";
        try
        {
            var versions = await _services.Installer
                .GetVersionListAsync(DownloadPolicy.FromConfig(config))
                .ConfigureAwait(true);
            _cachedVersions = versions;
            _cachedSource = (config.VersionListSource, config.FileSource);
            McGroups.Clear();
            foreach (var group in BuildGroups(versions))
            {
                McGroups.Add(group);
            }
        }
        catch (Exception ex)
        {
            McListError = $"获取版本列表失败：{ex.Message}";
            _services.Logger.Error("获取 Minecraft 版本列表失败", ex);
        }
        finally
        {
            IsMcListLoading = false;
        }
    }

    internal static IReadOnlyList<VersionGroupViewModel> BuildGroups(IReadOnlyList<VersionSummary> versions)
    {
        var ordered = versions
            .OrderByDescending(VersionTime)
            .ToList();

        var releases = ordered.Where(x => x.Type == "release").ToList();
        var snapshots = ordered.Where(x => x.Type == "snapshot").ToList();
        var olds = ordered.Where(x => x.Type is "old_beta" or "old_alpha").ToList();

        var groups = new List<VersionGroupViewModel>();
        var latestItems = new List<VersionItemViewModel>();
        if (releases.Count > 0)
        {
            latestItems.Add(Item(releases[0], "正式版", "grass-block"));
        }

        if (snapshots.Count > 0)
        {
            latestItems.Add(Item(snapshots[0], "快照版", "command-block"));
        }

        if (latestItems.Count > 0)
        {
            groups.Add(new VersionGroupViewModel("最新版本", latestItems, isExpanded: true));
        }

        if (releases.Count > 0)
        {
            groups.Add(new VersionGroupViewModel(
                "正式版",
                releases.Select(x => Item(x, "正式版", "grass-block")).ToList()));
        }

        if (snapshots.Count > 0)
        {
            groups.Add(new VersionGroupViewModel(
                "快照版",
                snapshots.Select(x => Item(x, "快照版", "command-block")).ToList()));
        }

        if (olds.Count > 0)
        {
            groups.Add(new VersionGroupViewModel(
                "远古版本",
                olds.Select(x => Item(x, "远古版", "cobblestone")).ToList()));
        }

        return groups;
    }

    private static DateTime VersionTime(VersionSummary version)
    {
        var text = version.ReleaseTime ?? version.Time;
        return DateTime.TryParse(text, out var value) ? value : DateTime.MinValue;
    }

    /// <summary>把加载器支持的 MC 版本 id 列表按主版本分组（从新到旧；对应分区页展示）。</summary>
    internal static IReadOnlyList<VersionGroupViewModel> BuildMcVersionGroups(
        IReadOnlyList<string> versionIds,
        string iconKey)
    {
        var ordered = versionIds
            .OrderByDescending(id => VersionSortKey(id))
            .ToList();
        var groups = new List<VersionGroupViewModel>();
        foreach (var group in ordered.GroupBy(id => GetMajorLine(id), StringComparer.Ordinal))
        {
            groups.Add(new VersionGroupViewModel(
                group.Key,
                group.Select(id => new VersionItemViewModel(id, id, iconKey, [], new VersionSummary { Id = id }))
                    .ToList()));
        }

        return groups;
    }

    private static string GetMajorLine(string versionId)
    {
        var core = versionId.Split('-')[0];
        var parts = core.Split('.');
        return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : core;
    }

    private static (int Major, int Minor, int Patch) VersionSortKey(string versionId)
    {
        var parts = versionId.Split('-')[0].Split('.');
        static int ParseOrZero(string text)
            => int.TryParse(new string(text.TakeWhile(char.IsDigit).ToArray()), out var value) ? value : 0;
        return (
            parts.Length > 0 ? ParseOrZero(parts[0]) : 0,
            parts.Length > 1 ? ParseOrZero(parts[1]) : 0,
            parts.Length > 2 ? ParseOrZero(parts[2]) : 0);
    }

    private static VersionItemViewModel Item(VersionSummary version, string tag, string iconKey)
    {
        var time = VersionTime(version);
        var info = time == DateTime.MinValue
            ? version.Id
            : $"{version.Id} · {time:yyyy-MM-dd}";
        return new VersionItemViewModel(version.Id, info, iconKey, [tag], version);
    }

    [RelayCommand]
    private async Task SelectMcAsync(VersionItemViewModel? item)
    {
        if (item?.Payload is not VersionSummary summary)
        {
            return;
        }

        // 新的向导会话：作废所有陈旧的加载器列表请求
        _wizardGeneration++;
        _selectedMc = summary;
        SelectedMcId = summary.Id;
        TargetName = summary.Id;
        SelectedLoaderName = "原版";
        _selectedLoaderItem = null;
        _selectedOptiFineItem = null;
        _selectedLiteLoaderItem = null;
        _companionModSlug = null;
        ActiveLoaderCard = null;
        IsLoaderLoading = true;
        LoaderError = "";
        try
        {
            await LoadLoaderCardsAsync().ConfigureAwait(true);
            // 并发预热全部加载器的版本列表（对应 PCL-CE 进入安装页即并发拉取）：
            // 用户点开卡片时列表通常已就绪，不再出现长时间变灰等待
            WarmLoaderEntries();
            RefreshCompanionAvailability();
            Step = 2;
        }
        catch (Exception ex)
        {
            LoaderError = $"加载安装选项失败：{ex.Message}";
        }
        finally
        {
            IsLoaderLoading = false;
        }
    }

    /// <summary>
    /// 获取加载器版本列表：带缓存与在途去重（对应 PCL-CE 的版本列表缓存语义）。
    /// 同一 (MC 版本, 加载器) 只发一次网络请求；重进向导或并发打开卡片时复用结果，
    /// 避免慢网下反复拉取导致页面长时间变灰。
    /// </summary>
    private async Task<IReadOnlyList<LoaderVersionEntry>> GetOrLoadLoaderEntriesAsync(
        string mcId,
        string kind,
        Func<Task<IReadOnlyList<LoaderVersionEntry>>> loader)
    {
        var key = (mcId, kind);
        if (_loaderEntryCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_loaderEntryInFlight.TryGetValue(key, out var inFlight))
        {
            return await inFlight.ConfigureAwait(true);
        }

        var epoch = _loaderCacheEpoch;
        var task = loader();
        _loaderEntryInFlight[key] = task;
        try
        {
            var entries = await task.ConfigureAwait(true);
            if (epoch == _loaderCacheEpoch)
            {
                _loaderEntryCache[key] = entries;
            }

            return entries;
        }
        finally
        {
            // 仅当在途条目仍是本次任务时才移除（Invalidate 清空后可能已开始新请求）
            if (_loaderEntryInFlight.TryGetValue(key, out var current) && ReferenceEquals(current, task))
            {
                _loaderEntryInFlight.Remove(key);
            }
        }
    }

    /// <summary>选中 MC 版本后并发预热全部加载器的版本列表；失败静默，点开卡片时会重新请求并提示。</summary>
    private void WarmLoaderEntries()
    {
        var mcId = SelectedMcId;
        _ = WarmOneLoaderAsync(mcId, "forge", policy => _services.LoaderProviders.Forge.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "neoforge", policy => _services.LoaderProviders.NeoForge.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "cleanroom", policy => _services.LoaderProviders.Cleanroom.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "fabric", policy => _services.LoaderProviders.Fabric.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "quilt", policy => _services.LoaderProviders.Quilt.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "legacyfabric", policy => _services.LoaderProviders.LegacyFabric.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "labymod", policy => _services.LoaderProviders.LabyMod.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "optifine", policy => _services.LoaderProviders.OptiFine.GetVersionsForAsync(mcId, policy));
        _ = WarmOneLoaderAsync(mcId, "liteloader", policy => _services.LoaderProviders.LiteLoader.GetVersionsForAsync(mcId, policy));
    }

    private async Task WarmOneLoaderAsync(
        string mcId,
        string kind,
        Func<DownloadPolicy, Task<IReadOnlyList<LoaderVersionEntry>>> loader)
    {
        try
        {
            await GetOrLoadLoaderEntriesAsync(mcId, kind, () => loader(DownloadPolicy.FromConfig(_services.Config)))
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _services.Logger.Error($"预热 {kind} 版本列表失败", ex);
        }
    }

    private async Task LoadLoaderCardsAsync()
    {
        LoaderCards.Clear();
        CompanionCards.Clear();
        _selectedLoaderItem = null;
        _selectedOptiFineItem = null;
        _selectedLiteLoaderItem = null;

        // 主加载器（对应 PCL-CE 下载页安装包分区）；卡片随版本重建，构建时固定 MC 版本
        void AddLoader(string kind, string title, string iconKey, string description, Func<DownloadPolicy, Task<IReadOnlyList<LoaderVersionEntry>>> loader)
        {
            var mcId = SelectedMcId;
            var card = new LoaderCardViewModel(
                title,
                iconKey,
                description,
                kind,
                false,
                async () => (await GetOrLoadLoaderEntriesAsync(
                        mcId, kind, () => loader(DownloadPolicy.FromConfig(_services.Config)))
                    .ConfigureAwait(true))
                    .Select(entry => new VersionItemViewModel(
                        entry.VersionName,
                        BuildLoaderEntryInfo(entry),
                        iconKey,
                        entry.IsRecommended ? ["推荐"] : (entry.IsStable ? [] : ["测试版"]),
                        new LoaderChoice(kind, entry.VersionName, entry.Payload))
                    ).ToList());
            LoaderCards.Add(card);
        }

        AddLoader(
            "forge", "Forge", "forge",
            "经典模组加载器；1.7–1.12 走旧版安装器，1.13+ 走 headless 引导器",
            policy => _services.LoaderProviders.Forge.GetVersionsForAsync(SelectedMcId, policy));
        AddLoader(
            "neoforge", "NeoForge", "neoforge",
            "新一代 Forge 分支；1.20.1 走旧包名特判",
            policy => _services.LoaderProviders.NeoForge.GetVersionsForAsync(SelectedMcId, policy));
        AddLoader(
            "cleanroom", "Cleanroom", "cleanroom",
            "1.12.2 的现代 Forge 分支（低版本模组容器）",
            policy => _services.LoaderProviders.Cleanroom.GetVersionsForAsync(SelectedMcId, policy));
        AddLoader(
            "fabric", "Fabric", "fabric",
            "轻量模组加载器；可搭配 Fabric API 前置",
            policy => _services.LoaderProviders.Fabric.GetVersionsForAsync(SelectedMcId, policy));
        AddLoader(
            "quilt", "Quilt", "quilt",
            "Fabric 兼容的社区加载器",
            policy => _services.LoaderProviders.Quilt.GetVersionsForAsync(SelectedMcId, policy));
        AddLoader(
            "legacyfabric", "Legacy Fabric", "fabric",
            "面向旧版本的 Fabric 分支",
            policy => _services.LoaderProviders.LegacyFabric.GetVersionsForAsync(SelectedMcId, policy));
        AddLoader(
            "labymod", "LabyMod", "labymod",
            "LabyMod 4 客户端（production / snapshot）",
            policy => _services.LoaderProviders.LabyMod.GetVersionsForAsync(SelectedMcId, policy));

        // 附属安装包（原始列表走缓存，OptiFine 的 Forge 适配过滤在取用时动态应用）
        var companionMcId = SelectedMcId;
        _optiFineCard = new LoaderCardViewModel(
            "OptiFine",
            "optifine",
            "光影前置与性能优化；与 Forge 共存时以 Mod 形式装入 mods",
            "optifine",
            true,
            async () => (await GetOrLoadLoaderEntriesAsync(
                    companionMcId, "optifine", () => _services.LoaderProviders.OptiFine
                        .GetVersionsForAsync(companionMcId, DownloadPolicy.FromConfig(_services.Config)))
                .ConfigureAwait(true))
                .Where(entry => _selectedLoaderItem?.Payload is not LoaderChoice { Payload: ForgeEntry forge }
                                || LoaderCompat.IsOptiFineSuitableForForge((OptiFineEntry)entry.Payload!, forge))
                .Select(entry => new VersionItemViewModel(
                    entry.VersionName,
                    BuildLoaderEntryInfo(entry),
                    "optifine",
                    entry.IsStable ? [] : ["测试版"],
                    entry.Payload))
                .ToList());
        _liteLoaderCard = new LoaderCardViewModel(
            "LiteLoader",
            "liteloader",
            "旧版轻量加载器（1.7–1.12）；可与原版、Forge 或 Fabric 搭配",
            "liteloader",
            true,
            async () => (await GetOrLoadLoaderEntriesAsync(
                    companionMcId, "liteloader", () => _services.LoaderProviders.LiteLoader
                        .GetVersionsForAsync(companionMcId, DownloadPolicy.FromConfig(_services.Config)))
                .ConfigureAwait(true))
                .Select(entry => new VersionItemViewModel(
                    entry.VersionName,
                    BuildLoaderEntryInfo(entry),
                    "liteloader",
                    entry.IsStable ? [] : ["测试版"],
                    entry.Payload))
                .ToList());
        CompanionCards.Add(_optiFineCard);
        CompanionCards.Add(_liteLoaderCard);

        // 前置 Mod 卡（Fabric / Legacy Fabric 选中后推荐对应 API）
        _fabricApiCard = new LoaderCardViewModel(
            "Fabric API",
            "fabric",
            "绝大多数 Fabric 模组的前置库（Modrinth：fabric-api）",
            "fabric-api",
            true,
            () => Task.FromResult<IReadOnlyList<VersionItemViewModel>>(
            [
                new VersionItemViewModel(
                    "推荐最新稳定版",
                    "安装时从 Modrinth 自动匹配",
                    "fabric",
                    ["推荐"],
                    CompanionModPayload("fabric-api"))
            ]));
        _legacyFabricApiCard = new LoaderCardViewModel(
            "Legacy Fabric API",
            "fabric",
            "Legacy Fabric 模组的前置库（Modrinth：legacy-fabric-api）",
            "legacyfabric-api",
            true,
            () => Task.FromResult<IReadOnlyList<VersionItemViewModel>>(
            [
                new VersionItemViewModel(
                    "推荐最新稳定版",
                    "安装时从 Modrinth 自动匹配",
                    "fabric",
                    ["推荐"],
                    CompanionModPayload("legacy-fabric-api"))
            ]));
        CompanionCards.Add(_fabricApiCard);
        CompanionCards.Add(_legacyFabricApiCard);
        _fabricApiCard.IsAvailable = false;
        _legacyFabricApiCard.IsAvailable = false;
    }

    private static string BuildLoaderEntryInfo(LoaderVersionEntry entry)
    {
        if (entry.ReleaseTime is { } releaseTime)
        {
            return $"{entry.Inherit} · {releaseTime.ToLocalTime():yyyy-MM-dd}";
        }

        return entry.Inherit;
    }

    private static ModrinthModPayload CompanionModPayload(string slug) => new(slug);

    /// <summary>前置 Mod 的选择载荷。</summary>
    public sealed record ModrinthModPayload(string Slug);

    // 允许并发执行：默认的 AsyncRelayCommand 在执行期间 CanExecute=false，
    // 会让绑定同一命令的全部加载器卡片一起变灰（慢网时最长 20 秒），返回重进也无法恢复；
    // 陈旧结果由代际令牌丢弃，重复请求由 GetOrLoadLoaderEntriesAsync 在数据层去重
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OpenLoaderCardAsync(LoaderCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        // 记录代际：等待期间用户切换版本/返回时，本次结果作废（防止陈旧列表页劫持界面）
        var generation = _wizardGeneration;
        IsLoaderLoading = true;
        LoaderError = "";
        try
        {
            await card.EnsureLoadedAsync().ConfigureAwait(true);
            if (generation != _wizardGeneration || !LoaderCards.Contains(card))
            {
                return;
            }

            ActiveLoaderCard = card;
        }
        catch (Exception ex)
        {
            if (generation == _wizardGeneration)
            {
                LoaderError = ex.Message;
            }
        }
        finally
        {
            if (generation == _wizardGeneration)
            {
                IsLoaderLoading = false;
            }
        }
    }

    [RelayCommand]
    private void SelectLoaderItem(VersionItemViewModel? item)
    {
        if (item is null || ActiveLoaderCard is null)
        {
            return;
        }

        // 主加载器单选（对应 PCL-CE _selectedLoaderType）：清除其它主卡的选择
        foreach (var other in LoaderCards)
        {
            if (!ReferenceEquals(other, ActiveLoaderCard))
            {
                other.ResetSelection();
            }
        }

        ActiveLoaderCard.ResetSelection();
        item.IsSelected = true;
        ActiveLoaderCard.HasSelection = true;
        ActiveLoaderCard.SelectedTitle = item.Title;
        _selectedLoaderItem = item;
        SelectedLoaderName = item.Title;
        var choice = item.Payload as LoaderChoice;
        var kind = choice?.Kind ?? "";
        TargetName = $"{SelectedMcId}-{kind}";

        // 已选 OptiFine 与新选的 Forge 不适配时清除 OptiFine（对应 PCL-CE Forge_Selected）
        if (choice?.Payload is ForgeEntry forge
            && _selectedOptiFineItem?.Payload is OptiFineEntry optiFine
            && !LoaderCompat.IsOptiFineSuitableForForge(optiFine, forge))
        {
            ClearCompanionCard(_optiFineCard);
            _toast?.Invoke($"已取消附属 OptiFine：与 Forge {forge.VersionName} 不适配");
        }

        ActiveLoaderCard = null;
        RefreshCompanionAvailability();
    }

    [RelayCommand]
    private void ClearCardSelection(LoaderCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        card.ResetSelection();
        if (_selectedLoaderItem is not null && card.Items.Contains(_selectedLoaderItem))
        {
            _selectedLoaderItem = null;
            SelectedLoaderName = "原版";
            TargetName = SelectedMcId;
        }

        RefreshCompanionAvailability();
    }

    /// <summary>附属安装包选择：OptiFine / LiteLoader / 前置 Mod。</summary>
    [RelayCommand]
    private void SelectCompanionItem(VersionItemViewModel? item)
    {
        if (item?.Payload is null)
        {
            return;
        }

        // OptiFine 与已选 Forge 不适配时清除 Forge（对应 PCL-CE OptiFine_Selected）
        if (item.Payload is OptiFineEntry optiFine
            && _selectedLoaderItem?.Payload is LoaderChoice { Payload: ForgeEntry forge }
            && !LoaderCompat.IsOptiFineSuitableForForge(optiFine, forge))
        {
            ClearCardSelection(LoaderCards.FirstOrDefault(c => c.Kind == "forge"));
            _toast?.Invoke($"已取消 Forge：与 OptiFine {optiFine.NameVersion} 不适配");
        }

        foreach (var card in CompanionCards)
        {
            if (card.Items.Contains(item))
            {
                card.ResetSelection();
                item.IsSelected = true;
                card.HasSelection = true;
                card.SelectedTitle = item.Title;
                break;
            }
        }

        _selectedOptiFineItem = item.Payload is OptiFineEntry ? item : null;
        _selectedLiteLoaderItem = item.Payload is LiteLoaderEntry ? item : null;
        _toast?.Invoke($"已选择附属：{item.Title}");
    }

    [RelayCommand]
    private void ClearCompanionCard(LoaderCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        card.ResetSelection();
        if (_selectedOptiFineItem is not null && card.Items.Contains(_selectedOptiFineItem))
        {
            _selectedOptiFineItem = null;
        }

        if (_selectedLiteLoaderItem is not null && card.Items.Contains(_selectedLiteLoaderItem))
        {
            _selectedLiteLoaderItem = null;
        }

        if (ReferenceEquals(card, _fabricApiCard) || ReferenceEquals(card, _legacyFabricApiCard))
        {
            _companionModSlug = null;
        }
    }

    /// <summary>
    /// 按兼容矩阵刷新附属卡可用性（移植自 PCL-CE Load*GetError）：
    /// OptiFine 与 NeoForge/LabyMod/Cleanroom 互斥、与 Fabric 需 MC ≤ 1.20.4、与 Forge 需 1.13 之外；
    /// LiteLoader 与 NeoForge/LegacyFabric/LabyMod/Cleanroom 互斥。
    /// </summary>
    private void RefreshCompanionAvailability()
    {
        var kind = (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind;

        foreach (var card in CompanionCards)
        {
            card.IsAvailable = card.Kind switch
            {
                "optifine" => LoaderCompat.IsOptiFineCompatibleWith(kind, SelectedMcId),
                "liteloader" => LoaderCompat.IsLiteLoaderCompatibleWith(kind),
                "fabric-api" => kind is "fabric" or "quilt",
                "legacyfabric-api" => kind is "legacyfabric",
                _ => card.IsAvailable
            };

            if (!card.IsAvailable)
            {
                ClearCompanionCard(card);
            }
        }
    }

    [RelayCommand]
    private void BackToMcList()
    {
        _wizardGeneration++;
        Step = 1;
        ActiveLoaderCard = null;
        // 陈旧的加载器请求收尾时会跳过重置（代际不符），这里显式清除，
        // 避免加载遮罩卡住导致返回后按钮全部不可点
        IsLoaderLoading = false;
    }

    [RelayCommand]
    private void BackToLoaderCards()
    {
        _wizardGeneration++;
        ActiveLoaderCard = null;
        IsLoaderLoading = false;
    }

    [RelayCommand]
    private async Task StartInstallAsync()
    {
        if (_install.IsInstalling || _selectedMc is null)
        {
            return;
        }

        var choice = _selectedLoaderItem?.Payload as LoaderChoice;
        var kind = choice?.Kind?.ToLowerInvariant() ?? "vanilla";
        var targetName = string.IsNullOrWhiteSpace(TargetName) ? SelectedMcId : TargetName;
        _companionModSlug = kind switch
        {
            "fabric" when _fabricApiCard?.HasSelection == true => "fabric-api",
            "legacyfabric" when _legacyFabricApiCard?.HasSelection == true => "legacy-fabric-api",
            _ => null
        };

        var plan = new LoaderInstallPlan
        {
            Kind = kind,
            GameDirectory = _services.Config.EffectiveGameDirectory,
            GameVersion = SelectedMcId,
            TargetName = targetName,
            LoaderVersion = choice?.DisplayName is { } loaderVersion
                            && !loaderVersion.Contains("自动", StringComparison.Ordinal)
                ? loaderVersion
                : null,
            OptiFine = _selectedOptiFineItem?.Payload as OptiFineEntry,
            LiteLoader = _selectedLiteLoaderItem?.Payload as LiteLoaderEntry,
            CompanionModSlug = _companionModSlug,
            Policy = DownloadPolicy.FromConfig(_services.Config),
            Threads = _services.Config.DownloadThreads,
            JavaPath = _services.Config.JavaPath
        };
        var displayName = $"{SelectedMcId} · {SelectedLoaderName}";
        _toast?.Invoke($"开始安装 {displayName}…");
        await _install.RunPlanAsync(plan, displayName).ConfigureAwait(true);
    }

    /// <summary>下载源等前提变化时使全部缓存失效：版本列表与加载器版本列表都会重新请求。</summary>
    public void Invalidate()
    {
        _cachedVersions = [];
        _loaderCacheEpoch++;
        _loaderEntryCache.Clear();
        _loaderEntryInFlight.Clear();
    }
}
