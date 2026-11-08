using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Services;

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
        Func<Task<IReadOnlyList<VersionItemViewModel>>> loader,
        CancellationToken token = default)
    {
        Title = title;
        IconKey = iconKey;
        Description = description;
        Kind = kind;
        IsAccessory = isAccessory;
        _loader = loader;
        Token = token;
        Items = new ObservableCollection<VersionItemViewModel>();
    }

    public CancellationToken Token { get; }

    public string Title { get; }

    public string IconKey { get; }

    public string Description { get; }

    public string Kind { get; }

    public bool IsAccessory { get; }

    public ObservableCollection<VersionItemViewModel> Items { get; }

    [ObservableProperty]
    private bool _isExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowRetryButton))]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _hasSelection;

    /// <summary>附属卡在当前主加载器组合下是否可选（互斥规则）。</summary>
    [ObservableProperty]
    private bool _isAvailable = true;

    /// <summary>列表加载总超时：超时按网络失败处理，避免不支持的版本长时间转圈。</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(20);

    /// <summary>状态字：加载中。</summary>
    public const string StatusLoadingText = "获取配置中";

    /// <summary>状态字：获取成功且有内容。</summary>
    public const string StatusReadyText = "可下载";

    /// <summary>状态字：请求成功但没有对应资源（不是网络失败）。</summary>
    public const string StatusEmptyText = "暂无对应资源";

    /// <summary>状态字：网络失败（超时/连接失败/HTTP 错误/解析失败）。</summary>
    public const string StatusFailedText = "配置获取失败，请刷新重试";

    /// <summary>卡片右侧状态字（对应 PCL-CE GetLoaderError 的展示位）：加载中灰字、就绪/空结果/失败黑字。</summary>
    [ObservableProperty]
    private string _statusText = StatusLoadingText;

    /// <summary>状态字是否使用深色（可下载/暂无对应资源/获取失败为黑，获取配置中为灰）。</summary>
    [ObservableProperty]
    private bool _isStatusDark;

    /// <summary>上一次获取成功但没有任何条目：用于展示"暂无对应资源"。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowRetryButton))]
    private bool _isEmpty;

    /// <summary>上一次获取因网络原因失败：卡片与子页面据此给出"配置获取失败，请刷新重试"。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState))]
    [NotifyPropertyChangedFor(nameof(ShowRetryButton))]
    private bool _isFailed;

    /// <summary>子页面空状态：加载完成且没有对应资源。</summary>
    public bool ShowEmptyState => IsEmpty && !IsLoading && !IsFailed;

    /// <summary>子页面失败状态：展示"刷新重试"按钮。</summary>
    public bool ShowRetryButton => IsFailed && !IsLoading;

    /// <summary>与其他已选主加载器冲突：状态字显示"与 X 冲突"并禁止进入版本选择。</summary>
    [ObservableProperty]
    private bool _isConflicted;

    /// <summary>版本列表是否已成功加载过（用于区分"加载中"与"可下载"）。</summary>
    public bool IsLoaded => _loaded;

    /// <summary>直接设置状态字文本与颜色。</summary>
    public void SetStatus(string text, bool dark)
    {
        StatusText = text;
        IsStatusDark = dark;
    }

    /// <summary>标记为加载中（灰字）。</summary>
    public void MarkLoading()
    {
        IsFailed = false;
        IsEmpty = false;
        SetStatus(StatusLoadingText, false);
    }

    /// <summary>标记为可下载（黑字）。</summary>
    public void MarkReady()
    {
        IsFailed = false;
        IsEmpty = false;
        SetStatus(StatusReadyText, true);
    }

    /// <summary>标记为"请求成功但没有对应资源"（黑字，不是网络失败）。</summary>
    public void MarkEmpty()
    {
        IsFailed = false;
        IsEmpty = true;
        SetStatus(StatusEmptyText, true);
    }

    /// <summary>标记为网络失败（黑字，子页面提供"刷新重试"）。</summary>
    public void MarkFailed()
    {
        IsFailed = true;
        IsEmpty = false;
        SetStatus(StatusFailedText, true);
    }

    /// <summary>重试前清空上一次的条目与状态，让 <see cref="EnsureLoadedAsync"/> 重新发起请求。</summary>
    public void PrepareReload()
    {
        _loaded = false;
        Items.Clear();
        ErrorText = "";
        MarkLoading();
    }

    public async Task EnsureLoadedAsync()
    {
        if (_loaded)
        {
            return;
        }

        IsLoading = true;
        ErrorText = "";
        MarkLoading();
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
            if (items.Count == 0)
            {
                // 请求成功但没有任何条目：空结果，不是网络失败
                MarkEmpty();
            }
            else if (!IsConflicted)
            {
                MarkReady();
            }
        }
        catch (Hycl.Launcher.Core.Game.Loaders.LoaderUnsupportedException)
        {
            // 源明确声明不支持该版本：属于"没有对应资源"，同样不是网络失败
            _loaded = true;
            Items.Clear();
            MarkEmpty();
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested)
        {
            // 页面离开/切换版本导致取消：不写错误，重新进入后可再次加载
        }
        catch (Exception)
        {
            // 超时 / 连接被拒 / HTTP 错误 / 解析失败：统一按网络失败处理
            ErrorText = StatusFailedText;
            MarkFailed();
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

    /// <summary>实例名校验错误（非法字符/保留字等），非空时阻止开始安装；重名由安装流程自动追加序号。</summary>
    [ObservableProperty]
    private string _targetNameError = "";

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

    /// <summary>已选主加载器的名称（如 Forge），供冲突状态字使用（SelectedLoaderName 是所选版本号）。</summary>
    private string _selectedLoaderCardTitle = "";

    private VersionItemViewModel? _selectedOptiFineItem;

    private VersionItemViewModel? _selectedLiteLoaderItem;

    private LoaderCardViewModel? _fabricApiCard;

    private LoaderCardViewModel? _legacyFabricApiCard;

    private LoaderCardViewModel? _optiFabricCard;

    private LoaderCardViewModel? _optiFineCard;

    private LoaderCardViewModel? _liteLoaderCard;

    /// <summary>向导代际：切换 MC 版本或返回时自增，使陈旧的加载器列表请求作废。</summary>
    private int _wizardGeneration;

    /// <summary>在途的 MC 版本列表请求。</summary>
    private CancellationTokenSource? _mcListCts;

    /// <summary>本次向导会话（选中某个 MC 版本后）的加载器请求令牌。</summary>
    private CancellationTokenSource? _loaderSessionCts;

    private CancellationToken _loaderSessionToken;

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

        CancelMcList();
        var cts = new CancellationTokenSource();
        _mcListCts = cts;
        IsMcListLoading = true;
        McListError = "";
        try
        {
            var versions = await _services.Installer
                .GetVersionListAsync(DownloadPolicy.FromConfig(config), cts.Token)
                .ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            PerfTrace.Mark("mc_list_fetched");
            _cachedVersions = versions;
            _cachedSource = (config.VersionListSource, config.FileSource);
            // 1116 条版本的分组与条目 VM 构造实测占 UI 线程 230~280ms：移到后台线程，
            // 只把最终 4 个分组对象在 UI 线程加入集合。
            var groups = await Task.Run(() => BuildGroups(versions), cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            McGroups.Clear();
            foreach (var group in groups)
            {
                McGroups.Add(group);
            }

            PerfTrace.Mark("mc_groups_built");
            PerfTrace.Mark($"version_list_done:{versions.Count}:{McGroups.Count}");
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // 切页/返回导致取消：保留已有列表，回到可再次发起的状态
        }
        catch (Exception ex)
        {
            McListError = $"获取版本列表失败：{ex.Message}";
            _services.Logger.Error("获取 Minecraft 版本列表失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_mcListCts, cts))
            {
                _mcListCts = null;
                IsMcListLoading = false;
            }

            cts.Dispose();
        }
    }

    /// <summary>取消在途的 MC 版本列表请求。</summary>
    private void CancelMcList()
    {
        var current = _mcListCts;
        _mcListCts = null;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被其它路径释放
        }

        current.Dispose();
        IsMcListLoading = false;
    }

    /// <summary>离开下载页：取消在途的版本列表与加载器列表请求，并作废旧会话。</summary>
    public void CancelPending()
    {
        CancelMcList();
        CancelLoaderSession();
        _wizardGeneration++;
        IsLoaderLoading = false;
        ActiveLoaderCard = null;
    }

    private void CancelLoaderSession()
    {
        var current = _loaderSessionCts;
        _loaderSessionCts = null;
        _loaderSessionToken = default;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被其它路径释放
        }

        current.Dispose();
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

        // 新的向导会话：作废所有陈旧的加载器列表请求，并重建本次会话的取消令牌
        _wizardGeneration++;
        CancelLoaderSession();
        _loaderSessionCts = new CancellationTokenSource();
        _loaderSessionToken = _loaderSessionCts.Token;
        // 版本列表单选：只有被点中的条目保持选中，切换时清除上一条
        foreach (var candidate in McGroups.SelectMany(group => group.Items))
        {
            candidate.IsSelected = ReferenceEquals(candidate, item);
        }

        _selectedMc = summary;
        SelectedMcId = summary.Id;
        TargetName = SuggestAvailableName(summary.Id);
        SelectedLoaderName = "原版";
        _selectedLoaderItem = null;
        _selectedLoaderCardTitle = "";
        _selectedOptiFineItem = null;
        _selectedLiteLoaderItem = null;
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
        var token = _loaderSessionToken;
        _ = WarmOneLoaderAsync(mcId, token, "forge", policy => _services.LoaderProviders.Forge.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "neoforge", policy => _services.LoaderProviders.NeoForge.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "cleanroom", policy => _services.LoaderProviders.Cleanroom.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "fabric", policy => _services.LoaderProviders.Fabric.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "quilt", policy => _services.LoaderProviders.Quilt.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "legacyfabric", policy => _services.LoaderProviders.LegacyFabric.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "labymod", policy => _services.LoaderProviders.LabyMod.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "optifine", policy => _services.LoaderProviders.OptiFine.GetVersionsForAsync(mcId, policy, token));
        _ = WarmOneLoaderAsync(mcId, token, "liteloader", policy => _services.LoaderProviders.LiteLoader.GetVersionsForAsync(mcId, policy, token));
    }

    private async Task WarmOneLoaderAsync(
        string mcId,
        CancellationToken token,
        string kind,
        Func<DownloadPolicy, Task<IReadOnlyList<LoaderVersionEntry>>> loader)
    {
        Exception? error = null;
        var unsupported = false;
        try
        {
            await GetOrLoadLoaderEntriesAsync(mcId, kind, () => loader(DownloadPolicy.FromConfig(_services.Config)))
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 会话已取消：静默退出，不点亮失败状态
            return;
        }
        catch (LoaderUnsupportedException ex)
        {
            // 源声明不支持该版本：按空结果处理，不显示"获取失败"
            unsupported = true;
            _services.Logger.Warn($"预热 {kind} 版本列表：{ex.Message}");
        }
        catch (Exception ex)
        {
            error = ex;
            _services.Logger.Error($"预热 {kind} 版本列表失败", ex);
        }

        // 预热完成即点亮卡片右侧状态字（可下载/暂无对应资源/获取失败）；会话已切换则忽略本次结果
        if (token.IsCancellationRequested || mcId != SelectedMcId)
        {
            return;
        }

        var card = LoaderCards.FirstOrDefault(c => c.Kind == kind)
                   ?? (kind == "optifine" ? _optiFineCard : kind == "liteloader" ? _liteLoaderCard : null);
        if (card is null)
        {
            return;
        }

        if (error is not null)
        {
            card.MarkFailed();
        }
        else if (unsupported)
        {
            card.MarkEmpty();
        }
        else
        {
            RefreshCardStatus(card);
        }
    }

    private async Task LoadLoaderCardsAsync()
    {
        LoaderCards.Clear();
        CompanionCards.Clear();
        _selectedLoaderItem = null;
        _selectedOptiFineItem = null;
        _selectedLiteLoaderItem = null;
        var token = _loaderSessionToken;

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
                    ).ToList(),
                token);
            LoaderCards.Add(card);
        }

        AddLoader(
            "forge", "Forge", "forge",
            "经典模组加载器",
            policy => _services.LoaderProviders.Forge.GetVersionsForAsync(SelectedMcId, policy, token));
        AddLoader(
            "neoforge", "NeoForge", "neoforge",
            "新一代 Forge 分支",
            policy => _services.LoaderProviders.NeoForge.GetVersionsForAsync(SelectedMcId, policy, token));
        AddLoader(
            "cleanroom", "Cleanroom", "cleanroom",
            "1.12.2 的现代 Forge 分支",
            policy => _services.LoaderProviders.Cleanroom.GetVersionsForAsync(SelectedMcId, policy, token));
        AddLoader(
            "fabric", "Fabric", "fabric",
            "轻量模组加载器，可搭配 Fabric API",
            policy => _services.LoaderProviders.Fabric.GetVersionsForAsync(SelectedMcId, policy, token));
        AddLoader(
            "quilt", "Quilt", "quilt",
            "Fabric 兼容的社区加载器",
            policy => _services.LoaderProviders.Quilt.GetVersionsForAsync(SelectedMcId, policy, token));
        AddLoader(
            "legacyfabric", "Legacy Fabric", "fabric",
            "面向旧版本的 Fabric 分支",
            policy => _services.LoaderProviders.LegacyFabric.GetVersionsForAsync(SelectedMcId, policy, token));
        AddLoader(
            "labymod", "LabyMod", "labymod",
            "LabyMod 4 客户端",
            policy => _services.LoaderProviders.LabyMod.GetVersionsForAsync(SelectedMcId, policy, token));

        // 附属安装包（原始列表走缓存，OptiFine 的 Forge 适配过滤在取用时动态应用）
        var companionMcId = SelectedMcId;
        _optiFineCard = new LoaderCardViewModel(
            "OptiFine",
            "optifine",
            "光影前置与性能优化",
            "optifine",
            true,
            async () => (await GetOrLoadLoaderEntriesAsync(
                    companionMcId, "optifine", () => _services.LoaderProviders.OptiFine
                        .GetVersionsForAsync(companionMcId, DownloadPolicy.FromConfig(_services.Config), token))
                .ConfigureAwait(true))
                .Where(entry => _selectedLoaderItem?.Payload is not LoaderChoice { Payload: ForgeEntry forge }
                                || LoaderCompat.IsOptiFineSuitableForForge((OptiFineEntry)entry.Payload!, forge))
                .Select(entry => new VersionItemViewModel(
                    entry.VersionName,
                    BuildLoaderEntryInfo(entry),
                    "optifine",
                    entry.IsStable ? [] : ["测试版"],
                    entry.Payload))
                .ToList(),
            token);
        _liteLoaderCard = new LoaderCardViewModel(
            "LiteLoader",
            "liteloader",
            "旧版轻量加载器（1.7–1.12）",
            "liteloader",
            true,
            async () => (await GetOrLoadLoaderEntriesAsync(
                    companionMcId, "liteloader", () => _services.LoaderProviders.LiteLoader
                        .GetVersionsForAsync(companionMcId, DownloadPolicy.FromConfig(_services.Config), token))
                .ConfigureAwait(true))
                .Select(entry => new VersionItemViewModel(
                    entry.VersionName,
                    BuildLoaderEntryInfo(entry),
                    "liteloader",
                    entry.IsStable ? [] : ["测试版"],
                    entry.Payload))
                .ToList(),
            token);
        CompanionCards.Add(_optiFineCard);
        CompanionCards.Add(_liteLoaderCard);

        // 前置 Mod 卡（Fabric / Legacy Fabric 选中后推荐对应 API）
        _fabricApiCard = new LoaderCardViewModel(
            "Fabric API",
            "fabric",
            "绝大多数 Fabric 模组的前置库",
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
            "Legacy Fabric 模组的前置库",
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
        // OptiFabric：仅 Fabric + OptiFine 组合下出现（对应 PCL-CE LoadOptiFabric）
        var optiFabricItem = new VersionItemViewModel(
            "推荐最新稳定版",
            "安装时从 Modrinth 自动匹配",
            "optifine",
            ["推荐"],
            CompanionModPayload("optifabric"));
        _optiFabricCard = new LoaderCardViewModel(
            "OptiFabric",
            "optifine",
            "让 OptiFine 与 Fabric 共存的前置 Mod",
            "optifabric",
            true,
            () => Task.FromResult<IReadOnlyList<VersionItemViewModel>>([optiFabricItem]));
        // 预置条目用于自动选中；点开卡片后 EnsureLoaded 会复用同一实例
        _optiFabricCard.Items.Add(optiFabricItem);
        CompanionCards.Add(_fabricApiCard);
        CompanionCards.Add(_legacyFabricApiCard);
        CompanionCards.Add(_optiFabricCard);
        _fabricApiCard.IsAvailable = false;
        _legacyFabricApiCard.IsAvailable = false;
        _optiFabricCard.IsAvailable = false;
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

        // 与已选主加载器冲突的卡片不可进入版本选择（对应 PCL-CE PreviewSwap 拦截）
        if (card.IsConflicted)
        {
            _toast?.Invoke($"已选择 {SelectedLoaderName}，与 {card.Title} 冲突，无法同时安装");
            return;
        }

        // 记录代际：等待期间用户切换版本/返回时，本次结果作废（防止陈旧列表页劫持界面）
        var generation = _wizardGeneration;
        IsLoaderLoading = true;
        LoaderError = "";
        try
        {
            await card.EnsureLoadedAsync().ConfigureAwait(true);
            // 主加载器与附属卡（Fabric API / LiteLoader / OptiFine …）都进入同一个子页面
            if (generation != _wizardGeneration
                || (!LoaderCards.Contains(card) && !CompanionCards.Contains(card)))
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

        // 附属卡子页面：选择即同步摘要，并返回向导上一步（与主加载器页一致）
        if (CompanionCards.Contains(ActiveLoaderCard))
        {
            SelectCompanionItem(item);
            ActiveLoaderCard = null;
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
        _selectedLoaderCardTitle = ActiveLoaderCard.Title;
        SelectedLoaderName = item.Title;
        var choice = item.Payload as LoaderChoice;
        var kind = choice?.Kind ?? "";
        TargetName = SuggestAvailableName($"{SelectedMcId}-{kind}");

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
        RefreshCardStatuses();
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
            _selectedLoaderCardTitle = "";
            SelectedLoaderName = "原版";
            TargetName = SuggestAvailableName(SelectedMcId);
        }

        RefreshCompanionAvailability();
        RefreshCardStatuses();
    }

    /// <summary>
    /// 按当前选择刷新主加载器卡片右侧状态字（对应 PCL-CE LoadXXGetError 的展示）：
    /// 冲突 → "与 X 冲突"（黑），已选 → 空，获取成功 → "可下载"（黑），
    /// 空结果 → "暂无对应资源"（黑），网络失败 → "配置获取失败，请刷新重试"（黑），其余 → "获取配置中"（灰）。
    /// </summary>
    private void RefreshCardStatuses()
    {
        foreach (var card in LoaderCards)
        {
            RefreshCardStatus(card);
        }
    }

    private void RefreshCardStatus(LoaderCardViewModel card)
    {
        var selectedKind = (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind;
        var isSelf = selectedKind is not null
                     && string.Equals(card.Kind, selectedKind, StringComparison.OrdinalIgnoreCase);
        card.IsConflicted = selectedKind is not null && !isSelf;

        if (card.IsConflicted)
        {
            card.SetStatus($"与 {_selectedLoaderCardTitle} 冲突", true);
        }
        else if (card.HasSelection)
        {
            // 已选中的卡在中间列展示所选版本，右侧留空
            card.SetStatus("", true);
        }
        else if (card.IsLoaded)
        {
            if (card.Items.Count > 0)
            {
                card.MarkReady();
            }
            else
            {
                card.MarkEmpty();
            }
        }
        else if (_loaderEntryCache.TryGetValue((SelectedMcId, card.Kind), out var cachedEntries))
        {
            // 预热只填充数据缓存（卡片 Items 在点开时才构建）；按缓存条数区分"可下载"与"暂无对应资源"。
            // 缓存命中说明后续预热已成功，必须优先于失败标记，否则早期失败会一直留在卡片上
            if (cachedEntries.Count > 0)
            {
                card.MarkReady();
            }
            else
            {
                card.MarkEmpty();
            }
        }
        else if (card.IsFailed)
        {
            card.MarkFailed();
        }
        else if (card.IsEmpty)
        {
            card.MarkEmpty();
        }
        else if (card.ErrorText.Length == 0)
        {
            card.MarkLoading();
        }
    }

    /// <summary>
    /// 子页面「刷新重试」：清掉本卡的列表缓存与加载器元数据负缓存后重新请求，
    /// 避免 30 秒负缓存让重试看似无效。
    /// </summary>
    [RelayCommand]
    private async Task RetryLoaderCardAsync(LoaderCardViewModel? card)
    {
        if (card is null || card.IsLoading || card.IsConflicted)
        {
            return;
        }

        var mcId = SelectedMcId;
        _services.LoaderProviders.Cache.Clear();
        _loaderEntryCache.Remove((mcId, card.Kind));
        _loaderEntryInFlight.Remove((mcId, card.Kind));
        card.PrepareReload();
        await card.EnsureLoadedAsync().ConfigureAwait(true);
        if (mcId != SelectedMcId)
        {
            return;
        }

        if (LoaderCards.Contains(card))
        {
            RefreshCardStatus(card);
        }
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
        // 选完 OptiFine 后重新评估 OptiFabric 卡是否需要出现并自动勾选
        RefreshCompanionAvailability();
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

        NotifyCompanionHints();
    }

    /// <summary>附属选择页里的「取消选择」：清空该卡选择并返回向导上一步。</summary>
    [RelayCommand]
    private void CancelCompanionSelection()
    {
        if (ActiveLoaderCard is null || !CompanionCards.Contains(ActiveLoaderCard))
        {
            return;
        }

        ClearCompanionCard(ActiveLoaderCard);
        ActiveLoaderCard = null;
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
                // PCL-CE 只在 Fabric 下展示 Fabric API；Quilt 默认内置 Fabric API 语义
                "fabric-api" => kind is "fabric",
                "legacyfabric-api" => kind is "legacyfabric",
                "optifabric" => kind is "fabric"
                                && _selectedOptiFineItem is not null
                                && LoaderCompat.IsOptiFabricCompatible(SelectedMcId),
                _ => card.IsAvailable
            };

            if (!card.IsAvailable)
            {
                ClearCompanionCard(card);
                // 正在该附属卡的选择页时被规则禁用：直接退回向导，避免看到已失效的入口
                if (ReferenceEquals(ActiveLoaderCard, card))
                {
                    ActiveLoaderCard = null;
                }
            }
        }

        // 对应 PCL-CE LoadOptiFabric：可用时默认勾选（用户仍可手动取消）
        if (_optiFabricCard is { IsAvailable: true, HasSelection: false, Items.Count: > 0 })
        {
            var item = _optiFabricCard.Items[0];
            item.IsSelected = true;
            _optiFabricCard.HasSelection = true;
            _optiFabricCard.SelectedTitle = item.Title;
        }

        NotifyCompanionHints();
    }

    /// <summary>Fabric/Legacy Fabric 缺少对应 API、Fabric 缺少 OptiFabric 时的提示（对应 PCL-CE ShowHint*）。</summary>
    public bool ShowFabricApiWarning =>
        (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind is "fabric"
        && _fabricApiCard?.HasSelection != true;

    public bool ShowLegacyFabricApiWarning =>
        (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind is "legacyfabric"
        && _legacyFabricApiCard?.HasSelection != true;

    public bool ShowOptiFabricWarning =>
        (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind is "fabric"
        && _selectedOptiFineItem is not null
        && _optiFabricCard?.HasSelection != true;

    public bool ShowLegacyOptiFabricWarning =>
        (_selectedLoaderItem?.Payload as LoaderChoice)?.Kind is "legacyfabric"
        && _selectedOptiFineItem is not null;

    private void NotifyCompanionHints()
    {
        OnPropertyChanged(nameof(ShowFabricApiWarning));
        OnPropertyChanged(nameof(ShowLegacyFabricApiWarning));
        OnPropertyChanged(nameof(ShowOptiFabricWarning));
        OnPropertyChanged(nameof(ShowLegacyOptiFabricWarning));
    }

    [RelayCommand]
    private void BackToMcList()
    {
        _wizardGeneration++;
        // 返回版本列表：取消本次会话仍在进行的加载器列表请求
        CancelLoaderSession();
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

    /// <summary>预填名称：直接给出最终可用的编号名（26.3 已存在则显示 26.3-1）。</summary>
    private string SuggestAvailableName(string baseName)
        => InstanceNameValidator.GetAvailableName(_services.Config.EffectiveGameDirectory, baseName);

    /// <summary>用户改名后立即清掉旧的校验提示，重新点击安装时再校验。</summary>
    partial void OnTargetNameChanged(string value)
    {
        TargetNameError = "";
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
        var targetName = string.IsNullOrWhiteSpace(TargetName) ? SelectedMcId : TargetName.Trim();

        // 非法名称仍阻断安装；同名实例已存在时自动追加序号（26.3 → 26.3-1 → 26.3-2 …），绝不覆盖旧实例。
        TargetNameError = "";
        var nameError = InstanceNameValidator.ValidateName(targetName);
        if (nameError is not null)
        {
            TargetNameError = nameError;
            _toast?.Invoke(nameError);
            return;
        }

        var effectiveName = InstanceNameValidator.GetAvailableName(
            _services.Config.EffectiveGameDirectory, targetName);
        if (!effectiveName.Equals(targetName, StringComparison.Ordinal))
        {
            // 把最终名称写回输入框：界面上看到的名字与实际创建的实例必须一致；
            // GetAvailableName 会剥离尾部 -N，因此再次安装不会叠成 26.3-1-1。
            TargetName = effectiveName;
            _toast?.Invoke($"已存在同名实例 {targetName}，将安装为 {effectiveName}");
        }

        var companionModSlugs = new List<string>();
        if (kind == "fabric" && _fabricApiCard?.HasSelection == true)
        {
            companionModSlugs.Add("fabric-api");
        }

        if (kind == "legacyfabric" && _legacyFabricApiCard?.HasSelection == true)
        {
            companionModSlugs.Add("legacy-fabric-api");
        }

        // PCL-CE 在 Fabric + OptiFine 时自动附带 OptiFabric
        if (kind == "fabric" && _optiFabricCard?.HasSelection == true)
        {
            companionModSlugs.Add("optifabric");
        }

        var plan = new LoaderInstallPlan
        {
            Kind = kind,
            GameDirectory = _services.Config.EffectiveGameDirectory,
            GameVersion = SelectedMcId,
            TargetName = effectiveName,
            // 加载器安装沿用该 MC 版本在版本列表中的图标
            IconKey = InstallTaskIcons.FromVersionType(_selectedMc?.Type),
            LoaderVersion = choice?.DisplayName is { } loaderVersion
                            && !loaderVersion.Contains("自动", StringComparison.Ordinal)
                ? loaderVersion
                : null,
            OptiFine = _selectedOptiFineItem?.Payload as OptiFineEntry,
            LiteLoader = _selectedLiteLoaderItem?.Payload as LiteLoaderEntry,
            CompanionModSlugs = companionModSlugs,
            Policy = DownloadPolicy.FromConfig(_services.Config),
            VersionIsolation = _services.Config.IsInstanceIsolated(effectiveName),
            Threads = _services.Config.DownloadThreads,
            JavaPath = _services.Config.JavaPath
        };
        var displayName = $"{effectiveName} · {SelectedLoaderName}";
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
        // 用户主动刷新时一并清掉加载器元数据缓存（含 30 秒负缓存），否则失败卡片点刷新仍会瞬间复用旧失败结果
        _services.LoaderProviders.Cache.Clear();
        if (_selectedMc is null)
        {
            return;
        }

        var failedCards = LoaderCards.Concat(CompanionCards).Where(card => card.IsFailed).ToList();
        if (failedCards.Count == 0)
        {
            return;
        }

        foreach (var card in failedCards)
        {
            card.PrepareReload();
        }

        // 重新预热失败过的卡片：仍然失败会再次落到"配置获取失败，请刷新重试"
        WarmLoaderEntries();
    }
}
