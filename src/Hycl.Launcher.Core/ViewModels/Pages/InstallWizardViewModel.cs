using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
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
public sealed record LoaderChoice(string Kind, string DisplayName);

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
        IsLoaderLoading = true;
        LoaderError = "";
        try
        {
            await LoadLoaderCardsAsync().ConfigureAwait(true);
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
        var fabricCard = new LoaderCardViewModel(
            "Fabric",
            "fabric",
            "安装 Fabric 加载器（自动选择支持该版本的最新稳定版）",
            "fabric",
            false,
            () => Task.FromResult<IReadOnlyList<VersionItemViewModel>>(
            [
                new VersionItemViewModel(
                    "Fabric 最新稳定版",
                    "安装时自动解析可用的稳定加载器",
                    "fabric",
                    ["推荐"],
                    new LoaderChoice("fabric", "Fabric 最新稳定版"))
            ]));
        LoaderCards.Add(fabricCard);
        await fabricCard.EnsureLoadedAsync().ConfigureAwait(true);
    }

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
        TargetName = $"{SelectedMcId}-fabric";
        ActiveLoaderCard = null;
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
    }

    /// <summary>附属安装包（如 OptiFine）的选择；当前 Core 未提供对应安装实现。</summary>
    [RelayCommand]
    private void SelectCompanionItem(VersionItemViewModel? item)
    {
        _toast?.Invoke(item is null ? "未选择附属安装包" : $"尚未支持安装 {item.Title}");
    }

    [RelayCommand]
    private void ClearCompanionCard(LoaderCardViewModel? card) => card?.ResetSelection();

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

        _toast?.Invoke($"开始安装 {SelectedMcId}…");
        if (_selectedLoaderItem?.Payload is LoaderChoice { Kind: "fabric" })
        {
            await _install.RunFabricAsync(SelectedMcId, $"{SelectedMcId} · Fabric 最新稳定版")
                .ConfigureAwait(true);
            return;
        }

        await _install.RunVanillaAsync(_selectedMc, SelectedMcId).ConfigureAwait(true);
    }

    public void Invalidate() => _cachedVersions = [];
}
