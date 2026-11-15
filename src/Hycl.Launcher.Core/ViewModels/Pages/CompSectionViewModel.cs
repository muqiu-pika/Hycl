using System.Collections.ObjectModel;
using System.Globalization;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>搜索结果条目（对应 PCL-CE MyCompItem）。</summary>
public sealed class CompProjectItemViewModel
{
    public CompProjectItemViewModel(CompProject project)
    {
        Project = project;
        Title = CompLocalization.DisplayTitle(project);
        Description = CompLocalization.DisplayDescription(project);
        Author = CompLocalization.DisplayAuthor(project);
        CategoryText = CompLocalization.DisplayCategoryText(project);
        UpdateText = CompLocalization.DisplayUpdateTime(project);
        UpdateRelativeText = CompLocalization.DisplayRelativeUpdateTime(project);
        IconUrl = project.IconUrl;
        SourceText = project.FromCurseForge ? "CurseForge" : "Modrinth";
        DownloadText = project.DownloadCount >= 100_000_000
            ? $"{project.DownloadCount / 100_000_000.0:0.##} 亿下载"
            : project.DownloadCount >= 10000
                ? $"{project.DownloadCount / 10000.0:0.#} 万下载"
                : $"{project.DownloadCount} 下载";
        Tags = CompLocalization.DisplayTags(project);
        GameVersionText = CompLocalization.DisplayGameVersions(project);
        LoaderText = CompLocalization.DisplayLoaderText(project);
    }

    public CompProject Project { get; }

    public string Title { get; }

    public string Description { get; }

    public string Author { get; }

    public string CategoryText { get; }

    public string UpdateText { get; }

    /// <summary>相对更新时间（详情页顶卡用，例如「2 天前」）。</summary>
    public string UpdateRelativeText { get; }

    public string GameVersionText { get; }

    public string LoaderText { get; }

    public string? IconUrl { get; }

    public string SourceText { get; }

    public string DownloadText { get; }

    public IReadOnlyList<string> Tags { get; }
}

/// <summary>资源版本条目（对应 PCL-CE CompFile.ToListItem）。</summary>
public sealed partial class CompFileItemViewModel : ObservableObject
{
    public CompFileItemViewModel(CompFile file, CompSectionViewModel owner)
    {
        File = file;
        _owner = owner;
        Title = CompLocalization.DisplayFileName(file);
        StatusText = file.StatusText;
        DateText = file.ReleaseDate?.ToLocalTime().ToString("yyyy-MM-dd") ?? "";
        SizeText = file.Size >= 1024 * 1024
            ? $"{file.Size / 1024.0 / 1024.0:0.#} MB"
            : $"{file.Size / 1024.0:0} KB";
        SourceText = file.FromCurseForge ? "CurseForge" : "Modrinth";
        var loaders = file.ModLoaders
            .Select(loader => loader.Trim().ToLowerInvariant())
            .Where(loader => loader.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Select(loader => loader switch
            {
                "forge" => "Forge",
                "neoforge" => "NeoForge",
                "fabric" => "Fabric",
                "quilt" => "Quilt",
                "liteloader" => "LiteLoader",
                "optifine" => "OptiFine",
                "vanilla" => "原版",
                _ => loader
            })
            .ToList();
        LoaderText = loaders.Count > 0 ? string.Join(" / ", loaders) : "—";
    }

    private readonly CompSectionViewModel _owner;

    public CompFile File { get; }

    public string Title { get; }

    public string StatusText { get; }

    public string DateText { get; }

    public string SizeText { get; }

    public string SourceText { get; }

    /// <summary>文件声明的加载器（Forge / NeoForge / Fabric / Quilt…），无约束时为 “—”。</summary>
    public string LoaderText { get; }

    public string GameVersionText
    {
        get
        {
            var versions = File.GameVersions
                .Select(CompLocalization.GameVersionName)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(name => name, CompLocalization.VersionComparer)
                .ToList();
            if (versions.Count == 0)
            {
                return "";
            }

            // 多版本时优先展示版本号最大的一个，按 最大 ~ 最小 表示覆盖范围
            return versions.Count == 1 ? versions[0] : $"{versions[0]} ~ {versions[^1]}";
        }
    }

    [ObservableProperty]
    private bool _isInstalling;

    [RelayCommand]
    private async Task InstallAsync() => await _owner.InstallFileAsync(this).ConfigureAwait(true);
}

/// <summary>详情页的胶囊筛选项（版本或加载器），选中态由父视图模型维护。</summary>
public sealed partial class DetailChipViewModel : ObservableObject
{
    public DetailChipViewModel(string text, string value)
    {
        Text = text;
        Value = value;
    }

    public string Text { get; }

    /// <summary>筛选值（"全部版本"/"全部加载器"/"远古版" 或具体版本号/加载器名）。</summary>
    public string Value { get; }

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>
/// 详情页的文件分组（对应 PCL-CE 的「NeoForge 26.3」「Fabric 26.3」折叠组）：
/// 标题 + 可展开的文件行，默认折叠、点标题展开。
/// </summary>
public sealed partial class CompFileGroupViewModel : ObservableObject
{
    public CompFileGroupViewModel(string header, IReadOnlyList<CompFileItemViewModel> files)
    {
        Header = header;
        Files = files;
    }

    public string Header { get; }

    public IReadOnlyList<CompFileItemViewModel> Files { get; }

    public string CountText => $"{Files.Count} 个文件";

    [ObservableProperty]
    private bool _isExpanded;

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>前置弹窗中的一行（项目、来源、版本或未解析原因）。</summary>
public sealed class DependencyDialogItemViewModel
{
    public DependencyDialogItemViewModel(string title, string detail, bool isError)
    {
        Title = title;
        Detail = detail;
        IsError = isError;
    }

    public string Title { get; }

    public string Detail { get; }

    public bool IsError { get; }

    public bool IsNormal => !IsError;
}

/// <summary>
/// 下载页「社区资源」分区（对应 PCL-CE PageComp）：搜索 + 筛选 + 结果列表 + 版本列表 + 分页。
/// </summary>
public partial class CompSectionViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> SortOptions = ["综合排序", "相关性", "下载量", "关注数", "最新发布", "最近更新"];

    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private readonly PropertyChangedEventHandler _installWatcher;
    private CompSearchResult? _lastResult;
    private int _pageIndex;
    private string? _lastQuerySignature;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _detailCts;
    private int _searchGeneration;
    private int _detailGeneration;
    // 关键：初始为 false——此前初始为 true 会让首次 Attach 空跑，
    // 筛选增强（版本全量/分类补全）与自动续搜在首次进入下载页时从未执行
    private bool _attached;
    private bool _searchInterrupted;
    private bool _suppressFilterSearch;
    private string? _preferredInstanceId;

    public CompSectionViewModel(
        LauncherServices services,
        InstallTaskViewModel install,
        CompResourceType type,
        string title,
        string iconKey,
        string description,
        Action<string>? toast)
    {
        _services = services;
        _install = install;
        _toast = toast;
        Type = type;
        Title = title;
        IconKey = iconKey;
        Description = description;
        // 安装进度监听改由 Attach/Detach 订阅与退订（ctor 不订阅，避免与首次 Attach 重复）
        _installWatcher = OnInstallPropertyChanged;

        LoaderOptions = ["全部加载器", "Forge", "NeoForge", "Fabric", "Quilt"];
        CategoryOptions.Add("全部分类");
        foreach (var category in CompLocalization.FilterCategories(type))
        {
            CategoryOptions.Add(category.Label);
        }

        // 只建静态基线；实例扫描 + 正式版清单的网络拉取推迟到 Attach()（首次进入下载页），
        // 避免启动期后台跑一次完整版本清单请求（1100+ 条 + UVMC 合并，实测 2~3s）。
        RebuildGameVersionOptions([], null);
    }

    private void OnInstallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstallTaskViewModel.IsInstalling)
            or nameof(InstallTaskViewModel.InstallingName)
            or nameof(InstallTaskViewModel.StageText)
            or nameof(InstallTaskViewModel.ProgressPercent)
            or nameof(InstallTaskViewModel.ErrorText))
        {
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(DownloadPercent));
            OnPropertyChanged(nameof(DownloadStateText));
            OnPropertyChanged(nameof(DownloadErrorText));
        }
    }

    /// <summary>页面离开时挂接安装进度监听；与 <see cref="Detach"/> 成对调用，避免切页累积回调。</summary>
    public void Attach()
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        _install.PropertyChanged += _installWatcher;

        // 进入下载页才做筛选全集的网络增强（版本清单全量 + 分类动态补全，进程内只取一次，失败回退静态基线）
        _ = RefreshGameVersionOptionsAsync();
        _ = RefreshCategoryOptionsAsync();

        // 上次离开页面时搜索结果被取消且列表仍为空：重新发起一次，保证再次进入能自动加载
        if (_searchInterrupted && Results.Count == 0)
        {
            _ = SearchAsync();
        }
    }

    /// <summary>
    /// 由实例资源列表跳转进入时调用：套用该实例的游戏版本/加载器筛选，
    /// 并让保存位置弹窗优先选择该实例（仅当该实例与本资源兼容）。
    /// </summary>
    public void PrepareTarget(string? gameVersion, string? loader, string? preferredInstanceId)
    {
        _preferredInstanceId = preferredInstanceId;
        // 记下目标版本/加载器：打开任意资源详情时，版本与加载器胶囊会自动选到这一组
        _targetGameVersion = string.IsNullOrWhiteSpace(gameVersion) ? null : gameVersion.Trim();
        _targetLoader = string.IsNullOrWhiteSpace(loader) ? null : loader.Trim();
        _suppressFilterSearch = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(gameVersion) && !GameVersionOptions.Contains(gameVersion))
            {
                GameVersionOptions.Add(gameVersion);
            }

            SelectedGameVersion = string.IsNullOrWhiteSpace(gameVersion) ? "全部版本" : gameVersion;
            SelectedLoader = SupportsLoaderFilter
                             && loader is not null
                             && LoaderOptions.Contains(loader)
                ? loader
                : "全部加载器";
            SelectedCategory = "全部分类";
            SelectedSort = "综合排序";
            SearchText = "";
            SelectedProject = null;
        }
        finally
        {
            _suppressFilterSearch = false;
        }
    }

    /// <summary>页面离开时取消在途搜索/详情请求并退订安装进度监听。</summary>
    public void Detach()
    {
        if (_attached)
        {
            _attached = false;
            _install.PropertyChanged -= _installWatcher;
        }

        CancelPending();
    }

    /// <summary>取消本分区所有在途的页面级请求：搜索、详情文件列表与简介翻译。</summary>
    public void CancelPending()
    {
        _searchInterrupted |= IsSearching;
        CancelAndDispose(ref _searchCts);
        CancelAndDispose(ref _detailCts);
        _searchGeneration++;
        _detailGeneration++;
        IsSearching = false;
        IsLoadingVersions = false;
        // 保存位置/前置确认框随页面一起关闭：尚未开始下载，不产生任何文件
        IsSaveDialogVisible = false;
        _pendingFile = null;
        SaveHint = "";
        CloseDependencyDialog();
    }

    private static void CancelAndDispose(ref CancellationTokenSource? source)
    {
        var current = source;
        source = null;
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

    private readonly InstallTaskViewModel _install;

    private FavoritesStore? _favorites;
    private EventHandler? _favoritesChanged;

    /// <summary>收藏夹存储（由下载页注入；为 null 时不显示收藏入口）。</summary>
    public FavoritesStore? Favorites
    {
        get => _favorites;
        set
        {
            if (ReferenceEquals(_favorites, value))
            {
                return;
            }

            if (_favorites is not null && _favoritesChanged is not null)
            {
                _favorites.Changed -= _favoritesChanged;
            }

            _favorites = value;
            _favoritesChanged = (_, _) => RefreshFavoriteFolders();
            if (_favorites is not null)
            {
                _favorites.Changed += _favoritesChanged;
            }

            RefreshFavoriteFolders();
        }
    }

    /// <summary>收藏夹名称列表（下拉框按名称选择，收藏夹名称在本地保持唯一）。</summary>
    [ObservableProperty]
    private IReadOnlyList<string> _favoriteFolderNames = [];

    private string _selectedFavoriteFolder = "";

    public string SelectedFavoriteFolder
    {
        get => _selectedFavoriteFolder;
        set
        {
            if (!SetProperty(ref _selectedFavoriteFolder, value))
            {
                return;
            }

            NotifyFavoriteState();
        }
    }

    /// <summary>当前项目是否已收藏在选中的收藏夹里。</summary>
    public bool IsFavoriteInSelectedFolder =>
        SelectedProject is not null
        && Favorites?.ContainsInFolder(CurrentFavoriteFolder?.Id ?? "", SelectedProject.Project.Id) == true;

    public bool IsFavoriteAnywhere =>
        SelectedProject is not null && Favorites?.Contains(SelectedProject.Project.Id) == true;

    public string FavoriteActionText => IsFavoriteInSelectedFolder ? "已收藏" : "收藏";

    /// <summary>新建收藏夹内嵌输入框。</summary>
    [ObservableProperty]
    private bool _isNewFavoriteFolderVisible;

    [ObservableProperty]
    private string _newFavoriteFolderName = "";

    private FavoriteFolder? CurrentFavoriteFolder =>
        Favorites?.Folders.FirstOrDefault(folder => folder.Name == SelectedFavoriteFolder)
        ?? Favorites?.Folders.FirstOrDefault();

    public CompResourceType Type { get; }

    public string Title { get; }

    public string IconKey { get; }

    public string Description { get; }

    public ObservableCollection<CompProjectItemViewModel> Results { get; } = [];

    // ---------- 详情页版本/加载器筛选（对应 PCL-CE PageDownloadCompDetail：胶囊行 + 分组列表） ----------

    private const string AllDetailVersions = "全部版本";
    private const string AllDetailLoaders = "全部加载器";
    private const string LegacyDetailVersions = "远古版";

    /// <summary>当前项目拉取到的全部文件；筛选基于它，DetailGroups 只是筛选后的展示结果。</summary>
    private IReadOnlyList<CompFile> _detailFiles = [];

    /// <summary>文件 Id → 条目 VM；重建列表时复用，保留「下载中」等行内状态。
    /// 计算可能发生在后台线程（详情分组重建），用并发容器。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, CompFileItemViewModel> _detailItems = new(StringComparer.Ordinal);

    private bool _detailVersionGrouped;
    private string _detailVersionScope = AllDetailVersions;
    private string _detailLoaderScope = AllDetailLoaders;

    /// <summary>由实例资源列表带来的目标版本/加载器：文件列表加载完成后自动选中对应胶囊。</summary>
    private string? _targetGameVersion;
    private string? _targetLoader;

    /// <summary>详情页顶部的版本胶囊（全部 / 各正式版本 / 远古版）。</summary>
    public ObservableCollection<DetailChipViewModel> DetailVersionChips { get; } = [];

    /// <summary>详情页的加载器胶囊（仅模组生成，选项来自该资源文件出现过的加载器）。</summary>
    public ObservableCollection<DetailChipViewModel> DetailLoaderChips { get; } = [];

    /// <summary>按「加载器 + 版本」分组的可展开文件列表（对齐 PCL-CE 的 NeoForge 26.3 / Fabric 26.3 分组）。</summary>
    public ObservableCollection<CompFileGroupViewModel> DetailGroups { get; } = [];

    [ObservableProperty]
    private string _detailEmptyText = "";

    /// <summary>版本胶囊仅在至少两个版本时展示（PCL-CE 同）。</summary>
    public bool HasDetailVersionFilter => DetailVersionChips.Count >= 2;

    /// <summary>加载器胶囊仅在模组且至少两个加载器时展示。</summary>
    public bool HasDetailLoaderFilter => DetailLoaderChips.Count >= 2;

    /// <summary>当前筛选下可安装文件数（同一文件出现在多个加载器分组时只算一次）。</summary>
    public int DetailFileCount => DetailGroups
        .SelectMany(group => group.Files)
        .Select(item => item.File.Id)
        .Distinct(StringComparer.Ordinal)
        .Count();

    public string DetailFileCountText => DetailFileCount == 0 ? "" : $"共 {DetailFileCount} 个文件";

    /// <summary>筛选后没有任何可安装文件（无错误时展示空状态，安装入口随之为空）。</summary>
    public bool IsDetailEmpty =>
        SelectedProject is not null
        && !IsLoadingVersions
        && DetailFileCount == 0
        && VersionErrorText.Length == 0;

    partial void OnIsLoadingVersionsChanged(bool value) => OnPropertyChanged(nameof(IsDetailEmpty));

    partial void OnVersionErrorTextChanged(string value) => OnPropertyChanged(nameof(IsDetailEmpty));

    /// <summary>点选版本胶囊：后台重算分组列表（万级文件的筛选分桶不卡 UI）。</summary>
    [RelayCommand]
    private async Task SelectDetailVersion(DetailChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        _detailVersionScope = chip.Value;
        foreach (var item in DetailVersionChips)
        {
            item.IsSelected = ReferenceEquals(item, chip);
        }

        await RebuildDetailGroupsAsync().ConfigureAwait(true);
    }

    /// <summary>点选加载器胶囊：后台重算分组列表。</summary>
    [RelayCommand]
    private async Task SelectDetailLoader(DetailChipViewModel? chip)
    {
        if (chip is null)
        {
            return;
        }

        _detailLoaderScope = chip.Value;
        foreach (var item in DetailLoaderChips)
        {
            item.IsSelected = ReferenceEquals(item, chip);
        }

        await RebuildDetailGroupsAsync().ConfigureAwait(true);
    }

    /// <summary>
    /// 从当前资源自身文件的 GameVersions / ModLoaders 生成胶囊行（不使用全局版本全集）。
    /// 版本超过 8 个时按 PCL-CE 的做法折叠大版本（1.21.11 → 1.21），否则使用精确版本；
    /// 存在快照/早期版本文件时追加「远古版」胶囊。由实例进入时自动选中该实例的版本与加载器。
    /// </summary>
    private sealed record DetailChipsData(
        List<string> Versions,
        bool Grouped,
        bool HasLegacy,
        List<string> Loaders);

    /// <summary>胶囊数据的纯计算部分：对上万条文件做 SelectMany/去重/排序，放到后台线程跑，避免卡 UI。</summary>
    private static DetailChipsData ComputeDetailChips(IReadOnlyList<CompFile> files, CompResourceType type)
    {
        var versions = files
            .SelectMany(file => file.GameVersions)
            .Select(CompLocalization.GameVersionName)
            .Where(CompLocalization.IsReleaseGameVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, CompLocalization.VersionComparer)
            .ToList();

        var grouped = versions.Count >= 9;
        if (grouped)
        {
            versions = versions
                .Select(DetailVersionGroup)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => version, CompLocalization.VersionComparer)
                .ToList();
        }

        // 「远古版」：文件里出现过快照/旧版本（非纯数字正式版号）时才有意义
        var hasLegacy = files.Any(file => file.GameVersions
            .Select(CompLocalization.GameVersionName)
            .Any(name => name.Length > 0 && !CompLocalization.IsReleaseGameVersion(name)));

        var loaders = new List<string>();
        if (type == CompResourceType.Mod)
        {
            loaders = files
                .SelectMany(file => file.ModLoaders)
                .Select(CompLocalization.LoaderName)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(LoaderOrder)
                .ToList();
        }

        return new DetailChipsData(versions, grouped, hasLegacy, loaders);
    }

    /// <summary>胶囊行构建（UI 线程）：条目数量只有个位数，直接构建。</summary>
    private void ApplyDetailChips(DetailChipsData data)
    {
        _detailVersionGrouped = data.Grouped;

        DetailVersionChips.Clear();
        DetailVersionChips.Add(new DetailChipViewModel("全部", AllDetailVersions));
        foreach (var version in data.Versions)
        {
            DetailVersionChips.Add(new DetailChipViewModel(version, version));
        }

        if (data.HasLegacy)
        {
            DetailVersionChips.Add(new DetailChipViewModel(LegacyDetailVersions, LegacyDetailVersions));
        }

        DetailLoaderChips.Clear();
        if (Type == CompResourceType.Mod)
        {
            DetailLoaderChips.Add(new DetailChipViewModel(AllDetailLoaders, AllDetailLoaders));
            foreach (var loader in data.Loaders)
            {
                DetailLoaderChips.Add(new DetailChipViewModel(loader, loader));
            }
        }

        // 自动选中：从实例资源列表进入时，版本胶囊落到该实例的游戏版本，
        // 加载器胶囊落到该实例的加载器（存在才选中，否则回退「全部」）。
        var versionChip = FindVersionChip(_targetGameVersion) ?? DetailVersionChips[0];
        _detailVersionScope = versionChip.Value;
        foreach (var chip in DetailVersionChips)
        {
            chip.IsSelected = ReferenceEquals(chip, versionChip);
        }

        var loaderChip = FindLoaderChip(_targetLoader) ?? DetailLoaderChips.FirstOrDefault();
        _detailLoaderScope = loaderChip?.Value ?? AllDetailLoaders;
        foreach (var chip in DetailLoaderChips)
        {
            chip.IsSelected = ReferenceEquals(chip, loaderChip);
        }

        OnPropertyChanged(nameof(HasDetailVersionFilter));
        OnPropertyChanged(nameof(HasDetailLoaderFilter));
    }

    private sealed record DetailGroupData(string Loader, string Version, List<CompFileItemViewModel> Files);

    /// <summary>
    /// 分组的纯计算部分（后台线程）：同一文件可能同时支持多个加载器（如 Forge + NeoForge）：
    /// 在每个加载器分组里都要出现，否则只过滤到某个加载器时该加载器分组会莫名缺项。
    /// 非 Mod 类型不做加载器分组（GroupLoaders 内部已处理）。
    /// </summary>
    private List<DetailGroupData> ComputeDetailGroups(IReadOnlyList<CompFile> files)
    {
        var buckets = new Dictionary<(string Loader, string Version), List<CompFile>>();
        foreach (var file in files.Where(MatchesDetailScope))
        {
            var version = GroupVersionLabel(file);
            foreach (var loader in GroupLoaders(file))
            {
                if (!buckets.TryGetValue((loader, version), out var list))
                {
                    buckets[(loader, version)] = list = [];
                }

                if (list.All(existing => existing.Id != file.Id))
                {
                    list.Add(file);
                }
            }
        }

        return buckets
            .OrderByDescending(pair => pair.Key.Version, CompLocalization.VersionComparer)
            .ThenBy(pair => LoaderOrder(pair.Key.Loader))
            .Select(pair => new DetailGroupData(
                pair.Key.Loader,
                pair.Key.Version,
                pair.Value
                    .OrderByDescending(file => file.ReleaseDate ?? DateTimeOffset.MinValue)
                    .Select(ToDetailItem)
                    .ToList()))
            .ToList();
    }

    /// <summary>分组结果落到 UI 集合（UI 线程）：分组数远小于文件数，直接构建。</summary>
    private void ApplyDetailGroups(IReadOnlyList<DetailGroupData> grouped)
    {
        DetailGroups.Clear();
        foreach (var group in grouped)
        {
            var header = group.Loader.Length > 0 ? $"{group.Loader} {group.Version}" : group.Version;
            var vm = new CompFileGroupViewModel(header, group.Files)
            {
                // 首个分组默认展开；从实例进入/选中胶囊后，展开匹配的分组
                IsExpanded = DetailGroups.Count == 0 || IsAutoExpandedGroup(group.Loader, group.Version)
            };
            DetailGroups.Add(vm);
        }

        DetailEmptyText = DetailGroups.Count == 0
            ? _detailVersionScope == AllDetailVersions && _detailLoaderScope == AllDetailLoaders
                ? "该项目没有可安装的文件"
                : "当前筛选条件下没有可安装的文件"
            : "";
        NotifyDetailState();
    }

    /// <summary>后台重算分组并回 UI：项目切换/胶囊连点时按代数丢弃过期结果。</summary>
    private async Task RebuildDetailGroupsAsync()
    {
        var generation = _detailGeneration;
        var files = _detailFiles;
        var grouped = await Task.Run(() => ComputeDetailGroups(files)).ConfigureAwait(true);
        if (generation != _detailGeneration || !ReferenceEquals(_detailFiles, files))
        {
            return;
        }

        ApplyDetailGroups(grouped);
    }

    private void NotifyDetailState()
    {
        OnPropertyChanged(nameof(DetailFileCount));
        OnPropertyChanged(nameof(DetailFileCountText));
        OnPropertyChanged(nameof(IsDetailEmpty));
    }

    /// <summary>取（或复用）文件条目 VM。</summary>
    private CompFileItemViewModel ToDetailItem(CompFile file)
    {
        if (_detailItems.TryGetValue(file.Id, out var item))
        {
            return item;
        }

        item = new CompFileItemViewModel(file, this);
        _detailItems[file.Id] = item;
        return item;
    }

    /// <summary>按当前胶囊过滤文件。</summary>
    private bool MatchesDetailScope(CompFile file)
    {
        if (_detailLoaderScope != AllDetailLoaders
            && !file.ModLoaders
                .Select(CompLocalization.LoaderName)
                .Any(name => name.Equals(_detailLoaderScope, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (_detailVersionScope == AllDetailVersions)
        {
            return true;
        }

        if (_detailVersionScope == LegacyDetailVersions)
        {
            return file.GameVersions
                .Select(CompLocalization.GameVersionName)
                .Any(name => name.Length > 0 && !CompLocalization.IsReleaseGameVersion(name));
        }

        return file.GameVersions.Any(version =>
        {
            var name = CompLocalization.GameVersionName(version);
            if (name.Length == 0)
            {
                return false;
            }

            var candidate = _detailVersionGrouped ? DetailVersionGroup(name) : name;
            return candidate.Equals(_detailVersionScope, StringComparison.OrdinalIgnoreCase);
        });
    }

    /// <summary>分组标题里的版本段：选了具体版本就用该版本，否则取文件支持的最高正式版。</summary>
    private string GroupVersionLabel(CompFile file)
    {
        if (_detailVersionScope is not AllDetailVersions and not LegacyDetailVersions)
        {
            return _detailVersionScope;
        }

        if (_detailVersionScope == LegacyDetailVersions)
        {
            var legacy = file.GameVersions
                .Select(CompLocalization.GameVersionName)
                .FirstOrDefault(name => name.Length > 0 && !CompLocalization.IsReleaseGameVersion(name));
            return legacy ?? LegacyDetailVersions;
        }

        var newest = file.GameVersions
            .Select(CompLocalization.GameVersionName)
            .Where(CompLocalization.IsReleaseGameVersion)
            .OrderByDescending(name => name, CompLocalization.VersionComparer)
            .FirstOrDefault();
        if (newest is null || newest.Length == 0)
        {
            return "其它版本";
        }

        return _detailVersionGrouped ? DetailVersionGroup(newest) : newest;
    }

    /// <summary>
    /// 文件应归属的加载器分组：模组按文件声明的加载器逐个分组；选了具体加载器胶囊时只归到该加载器；
    /// 非模组类型（资源包/光影/数据包）不参与加载器分组，统一空串。
    /// </summary>
    private IReadOnlyList<string> GroupLoaders(CompFile file)
    {
        if (Type != CompResourceType.Mod)
        {
            return [""];
        }

        if (_detailLoaderScope != AllDetailLoaders)
        {
            return [_detailLoaderScope];
        }

        var loaders = file.ModLoaders
            .Select(CompLocalization.LoaderName)
            .Where(name => name.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(LoaderOrder)
            .ToList();
        return loaders.Count > 0 ? loaders : [""];
    }

    /// <summary>加载器展示顺序：NeoForge → Forge → Fabric → Quilt → 其它（对齐 PCL-CE 的分组顺序）。</summary>
    private static int LoaderOrder(string loader) => loader.ToLowerInvariant() switch
    {
        "neoforge" => 0,
        "forge" => 1,
        "fabric" => 2,
        "quilt" => 3,
        "liteloader" => 4,
        "optifine" => 5,
        "" => 9,
        _ => 6
    };

    /// <summary>按胶囊选中的版本/加载器找对应的版本胶囊（含大版本折叠后的匹配）。</summary>
    private DetailChipViewModel? FindVersionChip(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var direct = DetailVersionChips.FirstOrDefault(chip =>
            chip.Value.Equals(version, StringComparison.OrdinalIgnoreCase));
        if (direct is not null)
        {
            return direct;
        }

        var grouped = DetailVersionGroup(version);
        return DetailVersionChips.FirstOrDefault(chip =>
            chip.Value.Equals(grouped, StringComparison.OrdinalIgnoreCase));
    }

    private DetailChipViewModel? FindLoaderChip(string? loader)
        => string.IsNullOrWhiteSpace(loader)
            ? null
            : DetailLoaderChips.FirstOrDefault(chip =>
                chip.Value.Equals(loader, StringComparison.OrdinalIgnoreCase));

    /// <summary>是否展开该分组：只在胶囊收窄了范围（从实例进入或手动选了版本/加载器）时展开匹配项。</summary>
    private bool IsAutoExpandedGroup(string loader, string version)
    {
        if (_detailVersionScope == AllDetailVersions && _detailLoaderScope == AllDetailLoaders)
        {
            return false;
        }

        var loaderMatch = _detailLoaderScope == AllDetailLoaders
                          || loader.Equals(_detailLoaderScope, StringComparison.OrdinalIgnoreCase);
        var versionMatch = _detailVersionScope == AllDetailVersions
                           || version.Equals(_detailVersionScope, StringComparison.OrdinalIgnoreCase);
        return loaderMatch && versionMatch;
    }

    /// <summary>折叠为大版本：1.21.11 / 1.21.1 → 1.21；快照等非标准版本保持原样。</summary>
    private static string DetailVersionGroup(string version)
    {
        var parts = version.Split('.');
        return parts.Length >= 3
               && parts[0].All(char.IsDigit)
               && parts[1].All(char.IsDigit)
               && parts[0].Length > 0
               && parts[1].Length > 0
            ? $"{parts[0]}.{parts[1]}"
            : version;
    }

    /// <summary>切项目/关闭详情时清空详情筛选状态（实例带来的目标版本/加载器保留，直到返回列表）。</summary>
    private void ResetDetailFilters()
    {
        _detailFiles = [];
        _detailItems.Clear();
        DetailVersionChips.Clear();
        DetailLoaderChips.Clear();
        DetailGroups.Clear();
        _detailVersionScope = AllDetailVersions;
        _detailLoaderScope = AllDetailLoaders;
        DetailEmptyText = "";

        OnPropertyChanged(nameof(HasDetailVersionFilter));
        OnPropertyChanged(nameof(HasDetailLoaderFilter));
        NotifyDetailState();
    }

    /// <summary>版本筛选选项：静态版本全集 + 本机已安装版本，按版本号降序；不随搜索结果变化。</summary>
    public ObservableCollection<string> GameVersionOptions { get; } = [];

    /// <summary>分类筛选选项：静态中文常用表起步，进入分区后由双 API 动态补全（不随搜索结果变化）。</summary>
    public ObservableCollection<string> CategoryOptions { get; } = [];

    /// <summary>当前分区的合并分类选项（静态中文表 + 双 API 动态补全）；null = 尚未补全，回退静态表。</summary>
    private IReadOnlyList<CompCategoryOption>? _categoryOptions;

    /// <summary>加载器筛选选项（仅模组/整合包使用）。</summary>
    public IReadOnlyList<string> LoaderOptions { get; }

    /// <summary>只有模组/整合包有加载器概念，其它类型不展示该筛选，避免出现点了不生效的选项。</summary>
    public bool SupportsLoaderFilter => Type is CompResourceType.Mod or CompResourceType.ModPack;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedGameVersion = "全部版本";

    [ObservableProperty]
    private string _selectedCategory = "全部分类";

    [ObservableProperty]
    private string _selectedLoader = "全部加载器";

    [ObservableProperty]
    private string _selectedSort = "综合排序";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private string _resultSummary = "";

    [ObservableProperty]
    private bool _canGoPrevious;

    [ObservableProperty]
    private bool _canGoNext;

    /// <summary>选中的项目（非空时下方展示版本列表）。</summary>
    [ObservableProperty]
    private CompProjectItemViewModel? _selectedProject;

    /// <summary>选中项目的中文简介（在线翻译可用时替换原文，失败回退原文）。</summary>
    [ObservableProperty]
    private string _selectedProjectDescription = "";

    [ObservableProperty]
    private string _selectedProjectAuthor = "";

    [ObservableProperty]
    private string _selectedProjectCategoryText = "";

    [ObservableProperty]
    private string _selectedProjectUpdateText = "";

    [ObservableProperty]
    private string _selectedProjectDownloadText = "";

    [ObservableProperty]
    private string _selectedProjectGameVersionText = "";

    [ObservableProperty]
    private string _selectedProjectLoaderText = "";

    /// <summary>详情页标题下的原文名（中文译名与源站标题不同时展示，例如「Just Enough Items」）。</summary>
    [ObservableProperty]
    private string _selectedProjectSubTitle = "";

    /// <summary>详情页信息行的分类文本（PCL-CE 顶卡里的「信息显示 支持库」）。</summary>
    [ObservableProperty]
    private string _selectedProjectInfoText = "";

    /// <summary>来源站名（CurseForge / Modrinth）。</summary>
    [ObservableProperty]
    private string _selectedProjectSourceText = "";

    /// <summary>「转到 CurseForge / 转到 Modrinth」按钮文案。</summary>
    public string WebsiteActionText => SelectedProject?.Project.FromCurseForge == true
        ? "转到 CurseForge"
        : "转到 Modrinth";

    /// <summary>详情页是否有原始标题可复制。</summary>
    public bool CanCopyProjectName => SelectedProject is not null;

    /// <summary>详情页下载进度：直接映射底部安装任务状态，供详情内联展示。</summary>
    public bool IsDownloading => _install.IsInstalling;

    public double DownloadPercent => _install.ProgressPercent;

    public string DownloadStateText => _install.IsInstalling
        ? $"{_install.InstallingName} · {_install.StageText} · {_install.ProgressPercent:0}%"
        : "";

    public string DownloadErrorText => _install.ErrorText;

    [ObservableProperty]
    private bool _isLoadingVersions;

    [ObservableProperty]
    private string _versionErrorText = "";

    private CompRequester Requester => _services.CompRequester;

    private ModrinthClient Modrinth => new(Requester);

    private CurseForgeClient CurseForge => new(Requester);

    private int Offset => _pageIndex * 40;

    // ---------- 下载前选择保存位置（对应 PCL-CE Save_Click / Install_Click） ----------

    private readonly List<MinecraftInstance> _dialogInstances = [];
    private CompFileItemViewModel? _pendingFile;

    /// <summary>UI 注入的文件夹选择器（返回 null 表示用户取消）。</summary>
    public Func<string, Task<string?>>? FolderPicker { get; set; }

    /// <summary>UI 注入的剪贴板写入（详情页「复制名称」按钮用）。</summary>
    public Func<string, Task>? CopyText { get; set; }

    public ObservableCollection<string> TargetInstanceOptions { get; } = [];

    /// <summary>
    /// 是否存在可安装该资源的实例：false 时保存位置弹窗不显示实例下拉，
    /// 默认落到系统「下载」文件夹，只允许用户手动指定文件夹。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstanceSelectable))]
    [NotifyPropertyChangedFor(nameof(ShowNoCompatibleInstanceHint))]
    private bool _hasCompatibleInstances = true;

    /// <summary>「暂无可安装该资源的实例」提示（无兼容实例时显示在弹窗里）。</summary>
    public string NoCompatibleInstanceText => "暂无可安装该资源的实例";

    /// <summary>无兼容实例时的补充说明（例如该资源只支持哪些加载器）。</summary>
    [ObservableProperty]
    private string _noCompatibleInstanceDetail = "";

    /// <summary>普通资源且没有任何兼容实例：保存位置弹窗改为只允许手动选目录。</summary>
    public bool ShowNoCompatibleInstanceHint => !IsModpackSave && !HasCompatibleInstances;

    [ObservableProperty]
    private bool _isSaveDialogVisible;

    /// <summary>整合包：只选择实例名，安装到 versions 下（对应 PCL-CE Install_Click 的实例名输入）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveDialogTitle))]
    [NotifyPropertyChangedFor(nameof(SaveDialogConfirmText))]
    [NotifyPropertyChangedFor(nameof(SaveDialogBody))]
    [NotifyPropertyChangedFor(nameof(IsInstanceSelectable))]
    [NotifyPropertyChangedFor(nameof(ShowNoCompatibleInstanceHint))]
    private bool _isModpackSave;

    [ObservableProperty]
    private string _selectedTargetInstance = "";

    [ObservableProperty]
    private string _saveDirectory = "";

    [ObservableProperty]
    private string _saveFileName = "";

    [ObservableProperty]
    private string _instanceName = "";

    /// <summary>选择框内的校验提示（非法路径、名称冲突等），确认失败时保留弹窗。</summary>
    [ObservableProperty]
    private string _saveHint = "";

    public bool IsInstanceSelectable => !IsModpackSave && HasCompatibleInstances;

    // ---------- 模组前置解析与确认（对应 PCL-CE CompDependency.ConfirmDependencyInstall） ----------

    /// <summary>前置弹窗状态：解析中 / 待确认 / 部分未解析 / 解析失败。</summary>
    public enum DependencyDialogMode
    {
        None,
        Resolving,
        Confirm,
        Unresolved,
        Failed
    }

    private CancellationTokenSource? _dependencyCts;
    private CompDependencyResolution? _dependencyResolution;
    private CompFileItemViewModel? _dependencyItem;
    private CompFile? _dependencyMainFile;
    private string _dependencyTargetDirectory = "";
    private int _dependencyGeneration;

    public ObservableCollection<DependencyDialogItemViewModel> DependencyItems { get; } = [];

    [ObservableProperty]
    private bool _isDependencyDialogVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDependencyResolving))]
    [NotifyPropertyChangedFor(nameof(IsDependencyListVisible))]
    [NotifyPropertyChangedFor(nameof(IsDependencyInstallVisible))]
    [NotifyPropertyChangedFor(nameof(IsDependencyContinueVisible))]
    [NotifyPropertyChangedFor(nameof(DependencyConfirmText))]
    private DependencyDialogMode _dependencyMode = DependencyDialogMode.None;

    [ObservableProperty]
    private string _dependencyDialogTitle = "";

    [ObservableProperty]
    private string _dependencyDialogBody = "";

    [ObservableProperty]
    private string _dependencyDialogError = "";

    public bool IsDependencyResolving => DependencyMode == DependencyDialogMode.Resolving;

    public bool IsDependencyListVisible =>
        DependencyMode is DependencyDialogMode.Confirm or DependencyDialogMode.Unresolved;

    /// <summary>可一并安装：显示「一起安装」。</summary>
    public bool IsDependencyInstallVisible => DependencyMode == DependencyDialogMode.Confirm;

    /// <summary>存在未解析/解析失败：显示「继续下载」。</summary>
    public bool IsDependencyContinueVisible =>
        DependencyMode is DependencyDialogMode.Unresolved or DependencyDialogMode.Failed;

    public string DependencyConfirmText => DependencyMode == DependencyDialogMode.Confirm ? "一起安装" : "继续下载";

    public string SaveDialogTitle => IsModpackSave ? "安装整合包" : "选择保存位置";

    public string SaveDialogConfirmText => IsModpackSave ? "开始安装" : "下载";

    public string SaveDialogBody => IsModpackSave
        ? "整合包会安装到游戏目录的版本文件夹中，请输入实例名称。"
        : _pendingFile?.File.Type == CompResourceType.DataPack
            ? "数据包会保存到 datapacks 目录（隔离时在该实例内，对实例全部存档生效；未隔离时在游戏目录，对全部实例存档生效）。单个存档想让数据包只在该存档生效时，需放入 saves/<世界>/datapacks 才会被加载。"
            : "文件将下载到下面的目录；目录不存在时会自动创建。";


    /// <summary>执行搜索（对应 PCL-CE CompProjectsGet；默认综合排序合并双源按相关度+下载量排序）。</summary>
    [RelayCommand]
    public async Task SearchAsync(bool nextPage = false)
    {
        CancelAndDispose(ref _searchCts);
        var cts = new CancellationTokenSource();
        _searchCts = cts;
        var token = cts.Token;
        var generation = ++_searchGeneration;
        _searchInterrupted = false;
        IsSearching = true;
        ErrorText = "";
        try
        {
            var request = BuildRequest();
            if (!nextPage)
            {
                _pageIndex = 0;
                request = request with { Offset = 0 };
            }
            else
            {
                request = request with { Offset = Offset };
            }

            _lastQuerySignature = Signature(request);
            var source = SourceFlags();

            // 社区资源缓存（参考 PCL-CE 的 compProjectCache 思路并落盘）：
            // 同一 请求词/筛选/来源/页码 6 小时内直接复用，打开分区不再每次全量请求
            var cacheKey = $"{Signature(request)}|{source}|offset {_pageIndex}";
            var cachedSearch = CompApiCache.TryGetSearch(_services.Paths.CacheDirectory, cacheKey);
            if (cachedSearch is not null)
            {
                ApplySearchResult(cachedSearch);
                ResultSummary = $"共 {cachedSearch.Projects.Count} 条结果（缓存）";
                return;
            }

            var projects = new List<CompProject>();
            int cfTotal = -1, mrTotal = -1;
            var tasks = new List<Task<CompSearchResult>>();
            if (source.HasFlag(CompSourceType.CurseForge))
            {
                tasks.Add(CurseForge.SearchAsync(request, token));
            }

            if (source.HasFlag(CompSourceType.Modrinth))
            {
                tasks.Add(Modrinth.SearchAsync(request, token));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _searchGeneration)
            {
                return;
            }

            foreach (var result in results)
            {
                projects.AddRange(result.Projects);
                cfTotal = Math.Max(cfTotal, result.CurseForgeTotal);
                mrTotal = Math.Max(mrTotal, result.ModrinthTotal);
            }

            // 综合排序：下载量优先（对应 PCL-CE 的相关度+下载量混合排序）
            if (request.Sort == CompSortType.Default)
            {
                projects = projects.OrderByDescending(p => Math.Log10(Math.Max(1, p.DownloadCount))).ToList();
            }
            else
            {
                projects = projects.OrderBy(p => p.FromCurseForge).ToList();
            }

            var merged = new CompSearchResult { Projects = projects, CurseForgeTotal = cfTotal, ModrinthTotal = mrTotal };
            CompApiCache.SaveSearch(_services.Paths.CacheDirectory, cacheKey, merged);
            ApplySearchResult(merged);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 正常取消（切页/重新搜索）：不写错误、不阻塞页面，返回后可直接再次发起
        }
        catch (Exception ex)
        {
            if (generation == _searchGeneration)
            {
                ErrorText = $"搜索失败：{ex.Message}";
                _services.Logger.Error("社区资源搜索失败", ex);
            }
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                _searchCts = null;
                IsSearching = false;
                OnPropertyChanged(nameof(CanGoPrevious));
                OnPropertyChanged(nameof(CanGoNext));
            }

            cts.Dispose();
        }
    }

    /// <summary>把搜索结果落到结果列表与分页状态（缓存命中与网络请求共用同一渲染路径）。</summary>
    private void ApplySearchResult(CompSearchResult result)
    {
        var projects = result.Projects.ToList();
        _lastResult = result;
        Results.Clear();
        foreach (var project in projects)
        {
            Results.Add(new CompProjectItemViewModel(project));
        }

        ResultSummary = $"共 {projects.Count} 条结果";
        CanGoPrevious = _pageIndex > 0;
        CanGoNext = projects.Count >= 40
                    && (result.CurseForgeTotal > (_pageIndex + 1) * 40 || result.ModrinthTotal > (_pageIndex + 1) * 40);
        if (SelectedProject is not null && !projects.Any(p => p.Id == SelectedProject.Project.Id))
        {
            SelectedProject = null;
        }
    }

    [RelayCommand]
    private Task SearchFromStartAsync() => SearchAsync(false);

    [RelayCommand]
    private Task GoNextPageAsync() => SearchAsync(true);

    [RelayCommand]
    private Task GoPreviousPageAsync() => SearchAsync(false);

    [RelayCommand]
    private async Task SelectProjectAsync(CompProjectItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        CancelAndDispose(ref _detailCts);
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        var token = cts.Token;
        var generation = ++_detailGeneration;

        SelectedProject = item;
        NotifyFavoriteState();
        SelectedProjectDescription = CompLocalization.DisplayDescription(item.Project);
        SelectedProjectAuthor = item.Author;
        SelectedProjectCategoryText = item.CategoryText;
        SelectedProjectUpdateText = item.UpdateRelativeText;
        SelectedProjectDownloadText = item.DownloadText;
        SelectedProjectGameVersionText = item.GameVersionText;
        SelectedProjectLoaderText = item.LoaderText;
        SelectedProjectSubTitle = item.Project.Title.Trim().Equals(item.Title.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ""
            : item.Project.Title.Trim();
        SelectedProjectInfoText = item.CategoryText;
        SelectedProjectSourceText = item.SourceText;
        OnPropertyChanged(nameof(WebsiteActionText));
        OnPropertyChanged(nameof(CanCopyProjectName));
        VersionErrorText = "";
        ResetDetailFilters();
        IsLoadingVersions = true;
        var translation = CompLocalization.TranslateDescriptionAsync(_services.DownloadClient, item.Project, token);
        try
        {
            // 文件/版本列表：内存层（零反序列化）→ 磁盘层（后台线程读盘解析）→ 在线加载。
            // CurseForge 的全量文件列表可达数 MB，任何一步在 UI 线程同步做都会让详情页卡顿。
            var cacheSource = item.Project.FromCurseForge ? "curseforge" : "modrinth";
            var files = await CompApiCache.GetOrLoadFilesAsync(
                _services.Paths.CacheDirectory,
                cacheSource,
                item.Project.Id,
                ct => item.Project.FromCurseForge
                    ? CurseForge.GetFilesAsync(item.Project.Id, Type, ct)
                    : Modrinth.GetFilesAsync(item.Project.Id, Type, ct),
                token).ConfigureAwait(true);

            token.ThrowIfCancellationRequested();
            if (generation != _detailGeneration)
            {
                return;
            }

            _detailFiles = files;
            // 胶囊与分组的纯计算（上万条文件的 SelectMany/分桶/排序）放后台线程，
            // UI 线程只做少量 VM 创建与集合更新
            var chips = await Task.Run(() => ComputeDetailChips(files, Type), token).ConfigureAwait(true);
            if (generation != _detailGeneration)
            {
                return;
            }

            ApplyDetailChips(chips);
            var grouped = await Task.Run(() => ComputeDetailGroups(files), token).ConfigureAwait(true);
            if (generation != _detailGeneration || !ReferenceEquals(_detailFiles, files))
            {
                return;
            }

            ApplyDetailGroups(grouped);

            var translated = await translation.ConfigureAwait(true);
            if (generation == _detailGeneration && ReferenceEquals(SelectedProject, item))
            {
                SelectedProjectDescription = translated;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 返回/切换项目/切页导致取消：保持页面可再次发起，不写入错误
        }
        catch (Exception ex)
        {
            if (generation == _detailGeneration)
            {
                VersionErrorText = $"获取版本列表失败：{ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                _detailCts = null;
                IsLoadingVersions = false;
            }

            cts.Dispose();
        }
    }

    /// <summary>由本分区发起、仍在进行的安装项（用于「返回」时只取消自己发起的下载）。</summary>
    private CompFileItemViewModel? _installingItem;

    [RelayCommand]
    private void CloseDetail()
    {
        // 选择保存位置时点返回：尚未开始下载，直接收起并丢弃待下载项
        if (IsSaveDialogVisible)
        {
            IsSaveDialogVisible = false;
            _pendingFile = null;
            SaveHint = "";
            _toast?.Invoke("已取消下载，未保存任何文件");
        }

        if (IsDependencyDialogVisible)
        {
            CloseDependencyDialog();
            _toast?.Invoke("已取消下载，未保存任何文件");
        }

        if (_installingItem is not null && _install.IsInstalling)
        {
            if (_install.CancelIfDownloading())
            {
                _toast?.Invoke($"已取消下载：{_install.InstallingName}");
            }
            else
            {
                // 解压/安装原版等不可逆阶段：不取消，继续完成，避免留下半成品实例
                _toast?.Invoke($"当前阶段（{_install.StageText}）无法安全取消，安装将继续完成");
            }
        }

        CancelAndDispose(ref _detailCts);
        _detailGeneration++;
        IsLoadingVersions = false;
        SelectedProject = null;
        NotifyFavoriteState();
        SelectedProjectDescription = "";
        SelectedProjectAuthor = "";
        SelectedProjectCategoryText = "";
        SelectedProjectUpdateText = "";
        SelectedProjectDownloadText = "";
        SelectedProjectGameVersionText = "";
        SelectedProjectLoaderText = "";
        SelectedProjectSubTitle = "";
        SelectedProjectInfoText = "";
        SelectedProjectSourceText = "";
        // 返回搜索结果：实例带来的目标版本/加载器不再约束后续浏览
        _targetGameVersion = null;
        _targetLoader = null;
        OnPropertyChanged(nameof(WebsiteActionText));
        OnPropertyChanged(nameof(CanCopyProjectName));
        ResetDetailFilters();
        VersionErrorText = "";
    }

    /// <summary>「转到 CurseForge / 转到 Modrinth」：优先用接口返回的项目主页，缺失时按 slug 拼装。</summary>
    [RelayCommand]
    private void OpenProjectWebsite()
    {
        var project = SelectedProject?.Project;
        if (project is null)
        {
            return;
        }

        var url = project.WebsiteUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            var slug = project.Slug.Length > 0 ? project.Slug : project.Id;
            var section = TypeSlug(project.Type);
            url = project.FromCurseForge
                ? $"https://www.curseforge.com/minecraft/{section}/{slug}"
                : $"https://modrinth.com/{section}/{slug}";
        }

        if (!_services.System.OpenUrl(url))
        {
            _toast?.Invoke("无法打开浏览器，请检查系统默认程序设置");
        }
    }

    /// <summary>「转到 MC 百科」：按资源名在 MC 百科搜索（中文译名命中率最高）。</summary>
    [RelayCommand]
    private void OpenProjectWiki()
    {
        var title = SelectedProject?.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        var url = $"https://search.mcmod.cn/s?key={Uri.EscapeDataString(title.Trim())}";
        if (!_services.System.OpenUrl(url))
        {
            _toast?.Invoke("无法打开浏览器，请检查系统默认程序设置");
        }
    }

    /// <summary>「复制名称」：把资源标题写入剪贴板（剪贴板由 UI 层注入）。</summary>
    [RelayCommand]
    private async Task CopyProjectNameAsync()
    {
        var name = SelectedProject?.Title;
        if (string.IsNullOrWhiteSpace(name) || CopyText is null)
        {
            return;
        }

        try
        {
            await CopyText(name.Trim()).ConfigureAwait(true);
            _toast?.Invoke($"已复制名称：{name.Trim()}");
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"复制失败：{ex.Message}");
        }
    }

    /// <summary>资源类型的站点路径段（用于拼装项目主页地址）。</summary>
    private static string TypeSlug(CompResourceType type) => type switch
    {
        CompResourceType.Mod => "mods",
        CompResourceType.ModPack => "modpacks",
        CompResourceType.ResourcePack => "resourcepacks",
        CompResourceType.Shader => "shaders",
        CompResourceType.DataPack => "datapacks",
        CompResourceType.World => "worlds",
        _ => "mods"
    };

    /// <summary>收藏 / 取消收藏当前项目到选中的收藏夹（写入本地收藏夹，不依赖社区接口）。</summary>
    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (SelectedProject is null || Favorites is null)
        {
            return;
        }

        var folder = CurrentFavoriteFolder;
        if (folder is null)
        {
            return;
        }

        var wasFavorite = IsFavoriteInSelectedFolder;
        await Favorites.ToggleAsync(folder.Id, SelectedProject.Project, Type).ConfigureAwait(true);
        NotifyFavoriteState();
        _toast?.Invoke(wasFavorite
            ? $"已从「{folder.Name}」取消收藏：{SelectedProject.Title}"
            : $"已收藏到「{folder.Name}」：{SelectedProject.Title}");
    }

    [RelayCommand]
    private void BeginCreateFavoriteFolder()
    {
        NewFavoriteFolderName = "";
        IsNewFavoriteFolderVisible = true;
    }

    [RelayCommand]
    private void CancelCreateFavoriteFolder()
    {
        IsNewFavoriteFolderVisible = false;
        NewFavoriteFolderName = "";
    }

    [RelayCommand]
    private async Task ConfirmCreateFavoriteFolderAsync()
    {
        if (Favorites is null)
        {
            return;
        }

        var name = NewFavoriteFolderName.Trim();
        if (name.Length == 0)
        {
            _toast?.Invoke("收藏夹名称不能为空");
            return;
        }

        var folder = await Favorites.CreateFolderAsync(name).ConfigureAwait(true);
        IsNewFavoriteFolderVisible = false;
        NewFavoriteFolderName = "";
        _toast?.Invoke($"已新建收藏夹：{folder.Name}");
    }

    /// <summary>收藏夹列表变化后同步下拉框，尽量保留当前选择。</summary>
    private void RefreshFavoriteFolders()
    {
        var names = Favorites?.Folders.Select(folder => folder.Name).ToList() ?? [];
        FavoriteFolderNames = names;
        if (!names.Contains(SelectedFavoriteFolder))
        {
            _selectedFavoriteFolder = names.FirstOrDefault() ?? "";
            OnPropertyChanged(nameof(SelectedFavoriteFolder));
        }

        NotifyFavoriteState();
    }

    /// <summary>从收藏夹打开项目详情：把项目补进结果列表后按常规流程加载版本文件。</summary>
    public async Task OpenProjectAsync(CompProject project)
    {
        var item = new CompProjectItemViewModel(project);
        if (!Results.Any(existing => existing.Project.Id == item.Project.Id
                                     && existing.Project.FromCurseForge == item.Project.FromCurseForge))
        {
            Results.Insert(0, item);
        }

        await SelectProjectAsync(item).ConfigureAwait(true);
    }

    private void NotifyFavoriteState()
    {
        OnPropertyChanged(nameof(IsFavoriteInSelectedFolder));
        OnPropertyChanged(nameof(IsFavoriteAnywhere));
        OnPropertyChanged(nameof(FavoriteActionText));
    }

    /// <summary>
    /// 下载资源前的保存位置选择（对应 PCL-CE Save_Click / Install_Click）：
    /// 普通资源选择目标实例与保存目录，整合包输入实例名；此时不发起任何下载。
    /// </summary>
    public async Task InstallFileAsync(CompFileItemViewModel item)
    {
        if (item.IsInstalling || _install.IsInstalling || IsSaveDialogVisible || IsDependencyDialogVisible)
        {
            return;
        }

        _pendingFile = item;
        _installingItem = null;
        SaveFileName = item.File.FileName;
        SaveHint = "";
        OnPropertyChanged(nameof(SaveDialogBody));
        if (item.File.Type == CompResourceType.ModPack)
        {
            IsModpackSave = true;
            // 预填最终可用的实例名（已存在同名整合包实例时直接显示 -1/-2）
            InstanceName = InstanceNameValidator.GetAvailableName(
                _services.Config.EffectiveGameDirectory, SuggestedInstanceName(item));
            SaveDirectory = Path.Combine(_services.Config.EffectiveGameDirectory, "versions", InstanceName.Trim());
            IsSaveDialogVisible = true;
            return;
        }

        IsModpackSave = false;
        await LoadSaveDialogInstancesAsync(item.File).ConfigureAwait(true);
        IsSaveDialogVisible = true;
    }

    /// <summary>列举实例并按「当前实例 → 兼容且较新 → 其余」排序，默认落到兼容实例的对应子目录。</summary>
    /// <summary>mod 类资源声明的加载器集合（只保留真正的模组加载器；optifine/vanilla 视为无约束）。</summary>
    private static HashSet<string> NormalizeFileLoaders(CompFile file)
        => file.ModLoaders
            .Select(loader => loader.Trim().ToLowerInvariant())
            .Where(loader => loader is "forge" or "neoforge" or "fabric" or "quilt" or "liteloader")
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>把实例的加载器标签归一到资源声明的命名空间；原版返回 "vanilla"。</summary>
    private static string NormalizeInstanceLoader(MinecraftInstance instance)
        => instance.Loader?.Trim().ToLowerInvariant() switch
        {
            "forge" or "cleanroom" => "forge",
            "neoforge" => "neoforge",
            "fabric" or "legacy fabric" => "fabric",
            "quilt" => "quilt",
            "liteloader" => "liteloader",
            "原版" => "vanilla",
            "" => "vanilla",
            _ => ""
        };

    private static string LoaderDisplayName(string loader) => loader switch
    {
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "liteloader" => "LiteLoader",
        _ => loader
    };

    /// <summary>
    /// 保存位置对话框的实例兼容判定：游戏版本必须匹配，mod 类资源还要求加载器一致
    /// （Forge 资源只给 Forge 实例、Fabric 只给 Fabric/Quilt，原版与其它加载器实例被过滤掉）。
    /// </summary>
    private static bool IsInstanceCompatible(MinecraftInstance instance, CompFile file, HashSet<string> requiredLoaders)
    {
        if (file.GameVersions.Count > 0
            && !file.GameVersions.Any(v => v.Equals(instance.GameVersion, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (requiredLoaders.Count == 0)
        {
            return true;
        }

        var instanceLoader = NormalizeInstanceLoader(instance);
        if (instanceLoader.Length == 0)
        {
            return false;
        }

        // Quilt 运行时兼容 Fabric 生态，Fabric 资源也适用于 Quilt 实例
        return requiredLoaders.Contains(instanceLoader)
               || (requiredLoaders.Contains("fabric") && instanceLoader == "quilt");
    }

    private async Task LoadSaveDialogInstancesAsync(CompFile file)
    {
        _dialogInstances.Clear();
        TargetInstanceOptions.Clear();
        SaveHint = "";
        NoCompatibleInstanceDetail = "";
        var gameDirectory = _services.Config.EffectiveGameDirectory;
        var subFolder = CompInstallService.GetSubFolder(file.Type);
        List<MinecraftInstance> instances;
        try
        {
            instances = (await _services.Instances.ScanAsync(gameDirectory).ConfigureAwait(true)).ToList();
        }
        catch (Exception ex)
        {
            HasCompatibleInstances = false;
            SelectedTargetInstance = "";
            SaveDirectory = PlatformInfo.GetDownloadsDirectory();
            NoCompatibleInstanceDetail = $"扫描实例失败：{ex.Message}";
            return;
        }

        var requiredLoaders = NormalizeFileLoaders(file);
        var compatible = instances
            .Where(instance => IsInstanceCompatible(instance, file, requiredLoaders))
            .OrderByDescending(instance => instance.LastWriteTime)
            .ToList();

        // 只列出「能装下这个资源」的实例：版本要匹配，模组类还要加载器匹配。
        // 一个都没有时不展示实例下拉，直接回退到系统下载文件夹（用户仍可手动指定目录）。
        HasCompatibleInstances = compatible.Count > 0;
        if (!HasCompatibleInstances)
        {
            SelectedTargetInstance = "";
            SaveDirectory = PlatformInfo.GetDownloadsDirectory();
            NoCompatibleInstanceDetail = requiredLoaders.Count > 0
                ? $"该资源仅支持 {string.Join(" / ", requiredLoaders.Select(LoaderDisplayName))} 实例。"
                : "没有实例的游戏版本与该资源匹配。";
            return;
        }

        var preferred = _preferredInstanceId is { Length: > 0 }
            ? compatible.FirstOrDefault(instance =>
                instance.Id.Equals(_preferredInstanceId, StringComparison.OrdinalIgnoreCase))
            : null;
        var current = compatible.FirstOrDefault(instance =>
            instance.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase));
        var target = preferred ?? current ?? compatible[0];

        foreach (var instance in compatible)
        {
            _dialogInstances.Add(instance);
            TargetInstanceOptions.Add($"{instance.Name} · {instance.Description}");
        }

        SelectedTargetInstance = TargetInstanceOptions[_dialogInstances.IndexOf(target)];
        var contentRoot = InstanceManager.GetContentRoot(
            gameDirectory,
            target.Id,
            _services.Config.IsInstanceIsolated(target.Id));
        SaveDirectory = string.IsNullOrEmpty(subFolder)
            ? contentRoot
            : Path.Combine(contentRoot, subFolder);
    }

    /// <summary>实例选择变化时同步默认保存目录（用户仍可再手动修改）。</summary>
    partial void OnSelectedTargetInstanceChanged(string value)
    {
        if (IsModpackSave)
        {
            return;
        }

        var index = TargetInstanceOptions.IndexOf(value);
        if (index < 0 || index >= _dialogInstances.Count || _pendingFile is null)
        {
            return;
        }

        var subFolder = CompInstallService.GetSubFolder(_pendingFile.File.Type);
        var instance = _dialogInstances[index];
        var contentRoot = InstanceManager.GetContentRoot(
            _services.Config.EffectiveGameDirectory,
            instance.Id,
            _services.Config.IsInstanceIsolated(instance.Id));
        SaveDirectory = string.IsNullOrEmpty(subFolder) ? contentRoot : Path.Combine(contentRoot, subFolder);
        SaveHint = "";
    }

    /// <summary>整合包实例名变化时同步预览路径。</summary>
    partial void OnInstanceNameChanged(string value)
    {
        if (IsModpackSave)
        {
            SaveDirectory = Path.Combine(
                _services.Config.EffectiveGameDirectory,
                "versions",
                value.Trim());
            SaveHint = "";
        }
    }

    [RelayCommand]
    private async Task BrowseSaveDirectoryAsync()
    {
        if (FolderPicker is null)
        {
            return;
        }

        try
        {
            var picked = await FolderPicker(SaveDirectory).ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(picked))
            {
                SaveDirectory = picked;
                SaveHint = "";
            }
        }
        catch (Exception ex)
        {
            SaveHint = $"无法打开文件夹选择器：{ex.Message}";
        }
    }

    [RelayCommand]
    private void CancelSaveDialog()
    {
        IsSaveDialogVisible = false;
        _pendingFile = null;
        SaveHint = "";
        _toast?.Invoke("已取消下载，未保存任何文件");
    }

    /// <summary>确认保存：校验路径/名称后关闭弹窗并开始下载；校验不通过时保留弹窗并就地提示。</summary>
    [RelayCommand]
    private async Task ConfirmSaveAsync()
    {
        var item = _pendingFile;
        if (item is null)
        {
            return;
        }

        if (IsModpackSave)
        {
            await ConfirmModpackSaveAsync(item).ConfigureAwait(true);
            return;
        }

        string directory;
        try
        {
            if (string.IsNullOrWhiteSpace(SaveDirectory))
            {
                SaveHint = "请输入保存目录";
                return;
            }

            directory = Path.GetFullPath(Environment.ExpandEnvironmentVariables(SaveDirectory.Trim()));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            SaveHint = $"保存目录无效：{ex.Message}";
            return;
        }

        if (File.Exists(directory))
        {
            SaveHint = "保存位置是一个文件，请选择文件夹";
            return;
        }

        IsSaveDialogVisible = false;
        _pendingFile = null;
        SaveHint = "";

        // 模组且开启自动安装前置时，先解析必需前置并让用户确认（对应 PCL-CE Save_Click 的依赖确认）
        if (await TryStartDependencyCheckAsync(item, directory).ConfigureAwait(true))
        {
            return;
        }

        await StartCompDownloadAsync(item, directory, []).ConfigureAwait(true);
    }

    /// <summary>开始下载（可带前置文件；前置失败不阻断本体）。</summary>
    private async Task StartCompDownloadAsync(
        CompFileItemViewModel item,
        string directory,
        IReadOnlyList<CompFile> dependencies)
    {
        if (item.IsInstalling || _install.IsInstalling)
        {
            return;
        }

        item.IsInstalling = true;
        _installingItem = item;
        try
        {
            if (dependencies.Count > 0)
            {
                await _install
                    .RunCompFileWithDependenciesAsync(
                        dependencies,
                        item.File,
                        directory,
                        Title,
                        iconUrl: SelectedProject?.IconUrl,
                        iconKey: IconKey)
                    .ConfigureAwait(true);
            }
            else
            {
                await _install
                    .RunCompFileToDirectoryAsync(
                        item.File,
                        directory,
                        Title,
                        iconUrl: SelectedProject?.IconUrl,
                        iconKey: IconKey)
                    .ConfigureAwait(true);
            }
        }
        finally
        {
            item.IsInstalling = false;
            if (ReferenceEquals(_installingItem, item))
            {
                _installingItem = null;
            }
        }
    }

    /// <summary>
    /// 解析模组必需前置；返回 true 表示已打开确认弹窗（调用方不要立即开始下载），
    /// 返回 false 表示无需确认、可以直接下载。
    /// </summary>
    private async Task<bool> TryStartDependencyCheckAsync(CompFileItemViewModel item, string directory)
    {
        if (item.File.Type != CompResourceType.Mod
            || !_services.Config.CompAutoInstallDependencies
            || item.File.Dependencies.Count == 0)
        {
            return false;
        }

        var target = ResolveDependencyTarget(item.File, directory);
        _dependencyItem = item;
        _dependencyMainFile = item.File;
        _dependencyTargetDirectory = directory;
        _dependencyResolution = null;
        DependencyItems.Clear();
        DependencyDialogError = "";
        DependencyDialogTitle = "正在解析前置内容";
        DependencyDialogBody = "正在查找该 Mod 所需的必需前置，请稍候…";
        DependencyMode = DependencyDialogMode.Resolving;
        IsDependencyDialogVisible = true;

        var generation = ++_dependencyGeneration;
        CancelAndDispose(ref _dependencyCts);
        // 多源串行等待设总时长上限，避免长时间无反馈
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _dependencyCts = cts;
        try
        {
            var result = await _services.CompDependencies
                .ResolveAsync(item.File, target.GameVersion, target.Loaders, directory, cts.Token)
                .ConfigureAwait(true);
            if (generation != _dependencyGeneration || !IsDependencyDialogVisible)
            {
                return true;
            }

            _dependencyResolution = result;
            if (result.ToInstall.Count == 0 && result.Unresolved.Count == 0)
            {
                CloseDependencyDialog();
                return false;
            }

            FillDependencyItems(result);
            if (result.Unresolved.Count > 0)
            {
                DependencyMode = DependencyDialogMode.Unresolved;
                DependencyDialogTitle = "安装模组前置确认";
                DependencyDialogBody = "以下前置内容未能自动解析，继续下载将仅安装所选 Mod。";
                return true;
            }

            DependencyMode = DependencyDialogMode.Confirm;
            DependencyDialogTitle = "安装模组前置确认";
            DependencyDialogBody = "该 Mod 需要以下前置内容。是否一并安装？确认后将先下载前置，再下载所选 Mod。";
            return true;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            if (generation != _dependencyGeneration || !IsDependencyDialogVisible)
            {
                return true;
            }

            // 30 秒超时按解析失败处理，保证弹窗仍可继续或关闭
            DependencyMode = DependencyDialogMode.Failed;
            DependencyDialogTitle = "前置解析失败";
            DependencyDialogBody = "获取前置信息超时，是否继续下载所选 Mod？";
            DependencyDialogError = "前置解析超时";
            return true;
        }
        catch (Exception ex)
        {
            if (generation != _dependencyGeneration || !IsDependencyDialogVisible)
            {
                return true;
            }

            _services.Logger.Error("前置解析失败", ex);
            DependencyMode = DependencyDialogMode.Failed;
            DependencyDialogTitle = "前置解析失败";
            DependencyDialogBody = "无法解析该 Mod 的前置内容，是否继续下载？";
            DependencyDialogError = ex.Message;
            return true;
        }
        finally
        {
            if (ReferenceEquals(_dependencyCts, cts))
            {
                _dependencyCts = null;
            }

            cts.Dispose();
        }
    }

    private void FillDependencyItems(CompDependencyResolution result)
    {
        DependencyItems.Clear();
        if (result.Unresolved.Count > 0)
        {
            // 未解析弹窗只列失败项，避免让用户误以为这些内容会被安装
            foreach (var unresolved in result.Unresolved)
            {
                var source = unresolved.FromCurseForge ? "CurseForge" : "Modrinth";
                DependencyItems.Add(new DependencyDialogItemViewModel(
                    $"未解析的前置：{unresolved.ProjectId}",
                    $"{source} · {unresolved.Reason}",
                    isError: true));
            }

            return;
        }

        foreach (var install in result.ToInstall)
        {
            var detail = install.File.DisplayName.Length > 0
                ? install.File.DisplayName
                : install.File.Version;
            if (detail.Length == 0)
            {
                detail = install.File.FileName;
            }

            var source = install.Project.FromCurseForge ? "CurseForge" : "Modrinth";
            DependencyItems.Add(new DependencyDialogItemViewModel(
                install.Project.Title,
                $"{source} · {detail}",
                isError: false));
        }
    }

    /// <summary>取目标实例的原版版本与加载器；找不到实例时回退到资源文件自身信息。</summary>
    private (string GameVersion, IReadOnlyList<string> Loaders) ResolveDependencyTarget(
        CompFile file,
        string directory)
    {
        MinecraftInstance? instance = null;
        var index = TargetInstanceOptions.IndexOf(SelectedTargetInstance);
        if (index >= 0 && index < _dialogInstances.Count)
        {
            instance = _dialogInstances[index];
        }
        else
        {
            // 用户手动改了保存目录：按目录匹配实例内容根，仍能拿到准确的版本/加载器
            var fullPath = Path.GetFullPath(directory);
            instance = _dialogInstances.FirstOrDefault(candidate =>
            {
                var root = InstanceManager.GetContentRoot(
                    _services.Config.EffectiveGameDirectory,
                    candidate.Id,
                    _services.Config.IsInstanceIsolated(candidate.Id));
                return fullPath.StartsWith(
                    Path.GetFullPath(root) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            });
        }

        var gameVersion = instance?.GameVersion;
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            gameVersion = file.GameVersions.FirstOrDefault() ?? "";
        }

        var loaders = instance is null
            ? file.ModLoaders
            : MapInstanceLoaders(instance.Loader, file.ModLoaders);
        return (gameVersion, loaders);
    }

    private static IReadOnlyList<string> MapInstanceLoaders(
        string instanceLoader,
        IReadOnlyList<string> fileLoaders)
        => instanceLoader switch
        {
            "Forge" or "Cleanroom" => ["forge"],
            "NeoForge" => ["neoforge"],
            "Fabric" or "Legacy Fabric" => ["fabric"],
            "Quilt" => ["quilt"],
            "LiteLoader" => ["liteloader"],
            _ => fileLoaders
        };


    /// <summary>切换下载分区时收起本分区的保存/前置弹窗，避免返回后弹窗残留。</summary>
    public void DismissDialogs()
    {
        if (IsSaveDialogVisible)
        {
            IsSaveDialogVisible = false;
            _pendingFile = null;
            SaveHint = "";
        }

        if (IsDependencyDialogVisible)
        {
            CloseDependencyDialog();
        }
    }

    /// <summary>关闭前置弹窗并丢弃解析结果（取消/切页/下载结束时统一走这里）。</summary>
    private void CloseDependencyDialog()
    {
        _dependencyGeneration++;
        CancelAndDispose(ref _dependencyCts);
        IsDependencyDialogVisible = false;
        DependencyMode = DependencyDialogMode.None;
        DependencyItems.Clear();
        _dependencyResolution = null;
        _dependencyItem = null;
        _dependencyMainFile = null;
        _dependencyTargetDirectory = "";
    }

    [RelayCommand]
    private void CancelDependencyDialog()
    {
        CloseDependencyDialog();
        _toast?.Invoke("已取消下载，未保存任何文件");
    }

    [RelayCommand]
    private async Task ConfirmDependencyWithDependenciesAsync()
    {
        var item = _dependencyItem;
        var mainFile = _dependencyMainFile;
        var directory = _dependencyTargetDirectory;
        var resolution = _dependencyResolution;
        if (item is null || mainFile is null || resolution is null)
        {
            CloseDependencyDialog();
            return;
        }

        var dependencies = resolution.ToInstall.Select(install => install.File).ToList();
        CloseDependencyDialog();
        await StartCompDownloadAsync(item, directory, dependencies).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ConfirmDependencyWithoutDependenciesAsync()
    {
        var item = _dependencyItem;
        var directory = _dependencyTargetDirectory;
        if (item is null)
        {
            CloseDependencyDialog();
            return;
        }

        CloseDependencyDialog();
        await StartCompDownloadAsync(item, directory, []).ConfigureAwait(true);
    }

    private async Task ConfirmModpackSaveAsync(CompFileItemViewModel item)
    {
        var nameError = InstanceNameValidator.ValidateName(InstanceName);
        if (nameError is not null)
        {
            SaveHint = nameError;
            return;
        }

        var name = InstanceName.Trim();
        // 与其它入口同一套规则：重名时按最小可用序号落盘，并把最终名称回显到输入框，避免显示名与实例名不一致。
        var effectiveName = InstanceNameValidator.GetAvailableName(
            _services.Config.EffectiveGameDirectory, name);
        if (!effectiveName.Equals(name, StringComparison.Ordinal))
        {
            InstanceName = effectiveName;
            _toast?.Invoke($"已存在同名实例 {name}，将安装为 {effectiveName}");
        }

        IsSaveDialogVisible = false;
        _pendingFile = null;
        SaveHint = "";
        item.IsInstalling = true;
        _installingItem = item;
        try
        {
            await _install
                .RunCompModpackAsync(
                    item.File,
                    effectiveName,
                    iconUrl: SelectedProject?.IconUrl)
                .ConfigureAwait(true);
        }
        finally
        {
            item.IsInstalling = false;
            if (ReferenceEquals(_installingItem, item))
            {
                _installingItem = null;
            }
        }
    }

    private static string SuggestedInstanceName(CompFileItemViewModel item)
    {
        var raw = item.File.DisplayName.Length > 0 ? item.File.DisplayName : item.Title;
        var trimmed = Path.GetFileNameWithoutExtension(raw);
        var name = string.Join("", trimmed.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        return name.Length == 0 ? "Modpack" : name;
    }


    /// <summary>重建版本筛选选项：版本清单全量（正式版/快照/远古版，按发布日期降序）+ 本机实例版本，保持当前选中项。</summary>
    private void RebuildGameVersionOptions(IReadOnlyList<(string Id, DateTimeOffset? Released)> manifestVersions, IEnumerable<string>? instanceExtras = null)
    {
        var selected = SelectedGameVersion;
        var entries = new List<(string Id, DateTimeOffset? Released)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 版本清单全量（含快照与远古版：对齐其它启动器的「最完整」筛选语义）
        foreach (var version in manifestVersions)
        {
            if (seen.Add(version.Id))
            {
                entries.Add(version);
            }
        }

        // 静态基线兜底（清单不可达时仍有最近几年的完整可选集）
        foreach (var id in CompLocalization.ReleaseGameVersions)
        {
            if (seen.Add(id))
            {
                entries.Add((id, null));
            }
        }

        // 本机实例版本（规整成纯 MC 版本号，加载器维度由「加载器」下拉负责）
        foreach (var raw in instanceExtras ?? [])
        {
            var id = MinecraftVersionGuesser.Normalize(raw) ?? raw;
            if (seen.Add(id))
            {
                entries.Add((id, null));
            }
        }

        // 从实例资源列表跳转时可能带入快照版本；异步重建选项时保留它，避免筛选被重置为「全部版本」
        if (selected != "全部版本" && !seen.Contains(selected))
        {
            entries.Insert(0, (selected, null));
        }

        // 有发布日期的按日期降序（最新优先，快照与正式版按官方时间线交错）；
        // 无日期的静态/实例项按版本号降序排在日期序列之后
        var versions = entries
            .OrderByDescending(entry => entry.Released ?? DateTimeOffset.MinValue)
            .ThenByDescending(entry => entry.Id, CompLocalization.VersionComparer)
            .Select(entry => entry.Id)
            .ToList();

        _suppressFilterSearch = true;
        try
        {
            GameVersionOptions.Clear();
            GameVersionOptions.Add("全部版本");
            foreach (var version in versions)
            {
                GameVersionOptions.Add(version);
            }

            SelectedGameVersion = GameVersionOptions.Contains(selected) ? selected : "全部版本";
        }
        finally
        {
            _suppressFilterSearch = false;
        }
    }

    /// <summary>把本机已安装实例的游戏版本并入筛选全集；扫描失败时保留静态全集。</summary>
    private async Task RefreshGameVersionOptionsAsync()
    {
        try
        {
            var instances = await _services.Instances
                .ScanAsync(_services.Config.EffectiveGameDirectory)
                .ConfigureAwait(true);
            var manifestVersions = await LoadManifestVersionsAsync().ConfigureAwait(true);
            RebuildGameVersionOptions(
                manifestVersions,
                instances.Select(instance => MinecraftVersionGuesser.Normalize(instance.GameVersion) ?? instance.GameVersion));
        }
        catch (Exception ex)
        {
            // 筛选选项只是增强信息，扫描失败不应影响搜索——但必须留痕，否则选项异常无从排查
            _services.Logger.Warn("游戏版本筛选选项重建失败：" + ex.Message);
        }
    }

    /// <summary>分类选项动态补全（Modrinth tags + CF categories 双 API，7 天缓存；失败回退静态表）。</summary>
    private async Task RefreshCategoryOptionsAsync()
    {
        try
        {
            var options = await CompCategoryService
                .GetMergedOptionsAsync(Type, _services.CompRequester, _services.Paths.CacheDirectory)
                .ConfigureAwait(true);
            _categoryOptions = options;
            var selected = SelectedCategory;
            _suppressFilterSearch = true;
            try
            {
                CategoryOptions.Clear();
                CategoryOptions.Add("全部分类");
                foreach (var option in options)
                {
                    CategoryOptions.Add(option.Label);
                }

                SelectedCategory = CategoryOptions.Contains(selected) ? selected : "全部分类";
            }
            finally
            {
                _suppressFilterSearch = false;
            }
        }
        catch (OperationCanceledException)
        {
            // 页面切换取消：下次进入重试
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"分类选项动态补全失败（使用静态表）：{ex.Message}");
        }
    }

    /// <summary>版本清单只取一次（多个分区共用同一份全量列表，24h 磁盘缓存 + 双源竞速由 Installer 承担）。</summary>
    private static Task<IReadOnlyList<(string Id, DateTimeOffset? Released)>>? _manifestVersionsTask;

    private Task<IReadOnlyList<(string Id, DateTimeOffset? Released)>> LoadManifestVersionsAsync()
        => _manifestVersionsTask ??= FetchManifestVersionsAsync();

    /// <summary>
    /// 从版本清单取<b>全量</b>版本（正式版/快照/远古版，供筛选选项自动化补全）；
    /// 同时把正式版并入静态基线（MinecraftVersionGuesser 的已知版本集合）；
    /// 失败时返回空列表，筛选回退静态基线，不影响搜索。
    /// </summary>
    private async Task<IReadOnlyList<(string Id, DateTimeOffset? Released)>> FetchManifestVersionsAsync()
    {
        try
        {
            var versions = await _services.Installer
                .GetVersionListAsync(DownloadPolicy.FromConfig(_services.Config))
                .ConfigureAwait(true);
            var releases = versions
                .Where(version => version.Type == "release" && CompLocalization.IsReleaseGameVersion(version.Id))
                .Select(version => version.Id)
                .ToList();
            CompLocalization.MergeReleaseGameVersions(releases);
            return versions
                .Select(version => (version.Id, ParseReleaseTime(version.ReleaseTime)))
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Logger.Warn($"获取版本清单失败（筛选仅使用静态基线）：{ex.Message}");
            return [];
        }
    }

    private static DateTimeOffset? ParseReleaseTime(string releaseTime)
        => DateTimeOffset.TryParse(releaseTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;

    partial void OnSelectedGameVersionChanged(string value) => QueueFilterSearch();

    partial void OnSelectedCategoryChanged(string value) => QueueFilterSearch();

    partial void OnSelectedLoaderChanged(string value) => QueueFilterSearch();

    partial void OnSelectedSortChanged(string value) => QueueFilterSearch();

    /// <summary>筛选项变化后自动重搜（对应 PCL-CE 筛选项 CheckedChanged → SearchStart）。</summary>
    private void QueueFilterSearch()
    {
        if (_suppressFilterSearch || !_attached)
        {
            return;
        }

        _ = SearchFromStartAsync();
    }

    private string? SelectedCategoryTag =>
        SelectedCategory == "全部分类"
            ? null
            : CompLocalization.FilterCategories(Type)
                .FirstOrDefault(option => option.Label == SelectedCategory)?.Tag;

    private CompSearchRequest BuildRequest()
    {
        var loader = SelectedLoader switch
        {
            "Forge" => CompLoaderFilter.Forge,
            "NeoForge" => CompLoaderFilter.NeoForge,
            "Fabric" => CompLoaderFilter.Fabric,
            "Quilt" => CompLoaderFilter.Quilt,
            _ => CompLoaderFilter.Any
        };
        var sort = SelectedSort switch
        {
            "相关性" => CompSortType.Relevance,
            "下载量" => CompSortType.Downloads,
            "关注数" => CompSortType.Follows,
            "最新发布" => CompSortType.Newest,
            "最近更新" => CompSortType.Updated,
            _ => CompSortType.Default
        };
        return new CompSearchRequest
        {
            Type = Type,
            Query = SearchText.Trim(),
            GameVersion = SelectedGameVersion == "全部版本" ? null : SelectedGameVersion,
            Loader = loader,
            Tag = SelectedCategoryTag,
            Sort = sort,
            Limit = 40
        };
    }

    private CompSourceType SourceFlags()
    {
        // 世界只有 CurseForge 提供（Modrinth 无对应项目类型）；其余双源并行。
        // CurseForge 走镜像（Key 留空时官方 403 由镜像兜底）。
        return Type == CompResourceType.World ? CompSourceType.CurseForge : CompSourceType.Any;
    }

    private static string Signature(CompSearchRequest request)
        => $"{request.Type}:{request.Query}:{request.GameVersion}:{request.Loader}:{request.Tag}:{request.Sort}:{request.Offset}";
}
