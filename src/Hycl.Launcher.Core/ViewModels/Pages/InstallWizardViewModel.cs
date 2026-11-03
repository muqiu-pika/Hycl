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

    [ObservableProperty]
    private string _selectedTitle = "";

    public string CountText => $"{Items.Count}";

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
            var items = await _loader().ConfigureAwait(true);
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

    private string? _companionModSlug;

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

        _selectedMc = summary;
        SelectedMcId = summary.Id;
        TargetName = summary.Id;
        SelectedLoaderName = "原版";
        _selectedLoaderItem = null;
        _selectedOptiFineItem = null;
        _selectedLiteLoaderItem = null;
        _companionModSlug = null;
        IsLoaderLoading = true;
        LoaderError = "";
        try
        {
            await LoadLoaderCardsAsync().ConfigureAwait(true);
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

    private async Task LoadLoaderCardsAsync()
    {
        LoaderCards.Clear();
        CompanionCards.Clear();
        _selectedLoaderItem = null;
        _selectedOptiFineItem = null;
        _selectedLiteLoaderItem = null;

        // 主加载器（对应 PCL-CE 下载页安装包分区）
        void AddLoader(string kind, string title, string iconKey, string description, Func<Task<IReadOnlyList<LoaderVersionEntry>>> loader)
        {
            var card = new LoaderCardViewModel(
                title,
                iconKey,
                description,
                kind,
                false,
                async () => (await loader().ConfigureAwait(true))
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
            "forge", "Forge", "anvil",
            "经典模组加载器；1.7–1.12 走旧版安装器，1.13+ 走 headless 引导器",
            () => _services.LoaderProviders.Forge.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));
        AddLoader(
            "neoforge", "NeoForge", "neoforge",
            "新一代 Forge 分支；1.20.1 走旧包名特判",
            () => _services.LoaderProviders.NeoForge.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));
        AddLoader(
            "cleanroom", "Cleanroom", "cleanroom",
            "1.12.2 的现代 Forge 分支（低版本模组容器）",
            () => _services.LoaderProviders.Cleanroom.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));
        AddLoader(
            "fabric", "Fabric", "fabric",
            "轻量模组加载器；可搭配 Fabric API 前置",
            () => _services.LoaderProviders.Fabric.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));
        AddLoader(
            "quilt", "Quilt", "quilt",
            "Fabric 兼容的社区加载器",
            () => _services.LoaderProviders.Quilt.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));
        AddLoader(
            "legacyfabric", "Legacy Fabric", "fabric",
            "面向旧版本的 Fabric 分支",
            () => _services.LoaderProviders.LegacyFabric.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));
        AddLoader(
            "labymod", "LabyMod", "labymod",
            "LabyMod 4 客户端（production / snapshot）",
            () => _services.LoaderProviders.LabyMod.GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config)));

        // 附属安装包
        var optiFineCard = new LoaderCardViewModel(
            "OptiFine",
            "optifine",
            "光影前置与性能优化；与 Forge 共存时以 Mod 形式装入 mods",
            "optifine",
            true,
            async () => (await _services.LoaderProviders.OptiFine
                    .GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config))
                    .ConfigureAwait(true))
                .Select(entry => new VersionItemViewModel(
                    entry.VersionName,
                    BuildLoaderEntryInfo(entry),
                    "optifine",
                    entry.IsStable ? [] : ["测试版"],
                    entry.Payload))
                .ToList());
        var liteLoaderCard = new LoaderCardViewModel(
            "LiteLoader",
            "liteloader",
            "旧版轻量加载器（1.7–1.12）；仅可与原版或 Forge 搭配",
            "liteloader",
            true,
            async () => (await _services.LoaderProviders.LiteLoader
                    .GetVersionsForAsync(SelectedMcId, DownloadPolicy.FromConfig(_services.Config))
                    .ConfigureAwait(true))
                .Select(entry => new VersionItemViewModel(
                    entry.VersionName,
                    BuildLoaderEntryInfo(entry),
                    "liteloader",
                    entry.IsStable ? [] : ["测试版"],
                    entry.Payload))
                .ToList());
        CompanionCards.Add(optiFineCard);
        CompanionCards.Add(liteLoaderCard);

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

    [RelayCommand]
    private async Task OpenLoaderCardAsync(LoaderCardViewModel? card)
    {
        if (card is null)
        {
            return;
        }

        IsLoaderLoading = true;
        LoaderError = "";
        try
        {
            await card.EnsureLoadedAsync().ConfigureAwait(true);
            ActiveLoaderCard = card;
        }
        catch (Exception ex)
        {
            LoaderError = ex.Message;
        }
        finally
        {
            IsLoaderLoading = false;
        }
    }

    [RelayCommand]
    private void SelectLoaderItem(VersionItemViewModel? item)
    {
        if (item is null || ActiveLoaderCard is null)
        {
            return;
        }

        ActiveLoaderCard.ResetSelection();
        item.IsSelected = true;
        ActiveLoaderCard.HasSelection = true;
        ActiveLoaderCard.SelectedTitle = item.Title;
        _selectedLoaderItem = item;
        SelectedLoaderName = item.Title;
        var kind = (item.Payload as LoaderChoice)?.Kind ?? "";
        TargetName = $"{SelectedMcId}-{kind}";
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

    /// <summary>按互斥规则刷新附属卡可用性（对应 PCL-CE：NeoForge/Cleanroom/LabyMod 与 OptiFine 互斥）。</summary>
    private void RefreshCompanionAvailability()
    {
        var kind = (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind?.ToLowerInvariant();
        var optiFineAllowed = kind is null or "vanilla" or "forge";
        var liteLoaderAllowed = kind is null or "vanilla" or "forge";

        foreach (var card in CompanionCards)
        {
            card.IsAvailable = card.Kind switch
            {
                "optifine" => optiFineAllowed,
                "liteloader" => liteLoaderAllowed,
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
        Step = 1;
        ActiveLoaderCard = null;
    }

    [RelayCommand]
    private void BackToLoaderCards() => ActiveLoaderCard = null;

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

    public void Invalidate() => _cachedVersions = [];
}
