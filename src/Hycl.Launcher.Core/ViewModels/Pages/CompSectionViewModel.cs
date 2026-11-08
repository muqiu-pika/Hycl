using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;
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
        IconUrl = project.IconUrl;
        SourceText = project.FromCurseForge ? "CurseForge" : "Modrinth";
        DownloadText = project.DownloadCount >= 10000
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
    }

    private readonly CompSectionViewModel _owner;

    public CompFile File { get; }

    public string Title { get; }

    public string StatusText { get; }

    public string DateText { get; }

    public string SizeText { get; }

    public string SourceText { get; }

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
    private bool _attached = true;
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
        _installWatcher = OnInstallPropertyChanged;
        _install.PropertyChanged += _installWatcher;

        LoaderOptions = ["全部加载器", "Forge", "NeoForge", "Fabric", "Quilt"];
        CategoryOptions.Add("全部分类");
        foreach (var category in CompLocalization.FilterCategories(type))
        {
            CategoryOptions.Add(category.Label);
        }

        // 只建静态基线；实例扫描 + 正式版清单的网络拉取推迟到 Attach()（首次进入下载页），
        // 避免启动期后台跑一次完整版本清单请求（1100+ 条 + UVMC 合并，实测 2~3s）。
        RebuildGameVersionOptions();
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

        // 进入下载页才做筛选全集的网络增强（正式版清单进程内只取一次，失败回退静态基线）
        _ = RefreshGameVersionOptionsAsync();

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

    public ObservableCollection<CompFileItemViewModel> VersionFiles { get; } = [];

    // ---------- 详情页版本/加载器筛选（对应 PCL-CE PageDownloadCompDetail 的筛选项与 UpdateFilterResult） ----------

    private const string AllDetailVersions = "全部版本";
    private const string AllDetailLoaders = "全部加载器";

    /// <summary>当前项目拉取到的全部文件；筛选基于它，VersionFiles 只是筛选后的展示子集。</summary>
    private IReadOnlyList<CompFile> _detailFiles = [];

    /// <summary>文件 Id → 条目 VM；筛选重建列表时复用，保留「下载中」等行内状态。</summary>
    private readonly Dictionary<string, CompFileItemViewModel> _detailItems = new(StringComparer.Ordinal);

    private bool _detailVersionGrouped;
    private bool _isBuildingDetailFilters;

    /// <summary>详情页版本筛选项：全部 + 该资源文件出现过的 MC 版本（按版本号降序）。</summary>
    public ObservableCollection<string> DetailVersionOptions { get; } = [];

    /// <summary>详情页加载器筛选项：仅模组生成，来自该资源文件出现过的加载器。</summary>
    public ObservableCollection<string> DetailLoaderOptions { get; } = [];

    [ObservableProperty]
    private string _selectedDetailVersion = AllDetailVersions;

    [ObservableProperty]
    private string _selectedDetailLoader = AllDetailLoaders;

    [ObservableProperty]
    private string _detailEmptyText = "";

    /// <summary>版本筛选仅在该资源自身有 2 个及以上版本时展示（PCL-CE 同）。</summary>
    public bool HasDetailVersionFilter => DetailVersionOptions.Count >= 2;

    /// <summary>加载器筛选仅模组展示，且与版本筛选同时出现（PCL-CE 同）。</summary>
    public bool HasDetailLoaderFilter => Type == CompResourceType.Mod && HasDetailVersionFilter;

    /// <summary>筛选后没有任何可安装文件（无错误时展示空状态，安装入口随之为空）。</summary>
    public bool IsDetailEmpty =>
        SelectedProject is not null
        && !IsLoadingVersions
        && VersionFiles.Count == 0
        && VersionErrorText.Length == 0;

    partial void OnSelectedDetailVersionChanged(string value)
    {
        if (!_isBuildingDetailFilters)
        {
            ApplyDetailFilter();
        }
    }

    partial void OnSelectedDetailLoaderChanged(string value)
    {
        if (!_isBuildingDetailFilters)
        {
            ApplyDetailFilter();
        }
    }

    partial void OnIsLoadingVersionsChanged(bool value) => OnPropertyChanged(nameof(IsDetailEmpty));

    partial void OnVersionErrorTextChanged(string value) => OnPropertyChanged(nameof(IsDetailEmpty));

    /// <summary>
    /// 从当前资源自身文件的 GameVersions / ModLoaders 生成筛选项（不使用全局版本全集）。
    /// 版本超过 8 个时按 PCL-CE 的做法折叠大版本（1.21.11 → 1.21），否则使用精确版本。
    /// </summary>
    private void BuildDetailFilters()
    {
        _isBuildingDetailFilters = true;
        try
        {
            var versions = _detailFiles
                .SelectMany(file => file.GameVersions)
                .Select(CompLocalization.GameVersionName)
                .Where(CompLocalization.IsReleaseGameVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(version => version, CompLocalization.VersionComparer)
                .ToList();

            _detailVersionGrouped = versions.Count >= 9;
            if (_detailVersionGrouped)
            {
                versions = versions
                    .Select(DetailVersionGroup)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(version => version, CompLocalization.VersionComparer)
                    .ToList();
            }

            DetailVersionOptions.Clear();
            DetailVersionOptions.Add(AllDetailVersions);
            foreach (var version in versions)
            {
                DetailVersionOptions.Add(version);
            }

            DetailLoaderOptions.Clear();
            if (Type == CompResourceType.Mod)
            {
                DetailLoaderOptions.Add(AllDetailLoaders);
                foreach (var loader in _detailFiles
                             .SelectMany(file => file.ModLoaders)
                             .Select(CompLocalization.LoaderName)
                             .Where(name => name.Length > 0)
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
                {
                    DetailLoaderOptions.Add(loader);
                }
            }

            SelectedDetailVersion = AllDetailVersions;
            SelectedDetailLoader = AllDetailLoaders;
        }
        finally
        {
            _isBuildingDetailFilters = false;
        }

        OnPropertyChanged(nameof(HasDetailVersionFilter));
        OnPropertyChanged(nameof(HasDetailLoaderFilter));
    }

    /// <summary>按当前版本/加载器筛选重建 VersionFiles；条目 VM 复用，避免打断进行中的下载行状态。</summary>
    private void ApplyDetailFilter()
    {
        if (_detailFiles.Count == 0)
        {
            VersionFiles.Clear();
            DetailEmptyText = "";
            OnPropertyChanged(nameof(IsDetailEmpty));
            return;
        }

        var versionFilter = SelectedDetailVersion == AllDetailVersions ? null : SelectedDetailVersion;
        var loaderFilter = Type == CompResourceType.Mod && SelectedDetailLoader != AllDetailLoaders
            ? SelectedDetailLoader
            : null;

        var matches = _detailFiles
            .Where(file => MatchesDetailFilter(file, versionFilter, loaderFilter))
            .ToList();

        VersionFiles.Clear();
        foreach (var file in matches.Take(60))
        {
            if (!_detailItems.TryGetValue(file.Id, out var item))
            {
                item = new CompFileItemViewModel(file, this);
                _detailItems[file.Id] = item;
            }

            VersionFiles.Add(item);
        }

        DetailEmptyText = VersionFiles.Count == 0
            ? versionFilter is null && loaderFilter is null
                ? "该项目没有可安装的文件"
                : "当前筛选条件下没有可安装的文件"
            : "";
        OnPropertyChanged(nameof(IsDetailEmpty));
    }

    private bool MatchesDetailFilter(CompFile file, string? versionFilter, string? loaderFilter)
    {
        if (versionFilter is not null)
        {
            var matched = file.GameVersions.Any(version =>
            {
                var name = CompLocalization.GameVersionName(version);
                return name.Length > 0
                       && string.Equals(
                           _detailVersionGrouped ? DetailVersionGroup(name) : name,
                           versionFilter,
                           StringComparison.OrdinalIgnoreCase);
            });
            if (!matched)
            {
                return false;
            }
        }

        if (loaderFilter is not null)
        {
            return file.ModLoaders
                .Select(CompLocalization.LoaderName)
                .Any(name => string.Equals(name, loaderFilter, StringComparison.OrdinalIgnoreCase));
        }

        return true;
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

    /// <summary>切项目/关闭详情时清空详情筛选状态。</summary>
    private void ResetDetailFilters()
    {
        _isBuildingDetailFilters = true;
        try
        {
            _detailFiles = [];
            _detailItems.Clear();
            DetailVersionOptions.Clear();
            DetailLoaderOptions.Clear();
            SelectedDetailVersion = AllDetailVersions;
            SelectedDetailLoader = AllDetailLoaders;
            DetailEmptyText = "";
        }
        finally
        {
            _isBuildingDetailFilters = false;
        }

        OnPropertyChanged(nameof(HasDetailVersionFilter));
        OnPropertyChanged(nameof(HasDetailLoaderFilter));
        OnPropertyChanged(nameof(IsDetailEmpty));
    }

    /// <summary>版本筛选选项：静态版本全集 + 本机已安装版本，按版本号降序；不随搜索结果变化。</summary>
    public ObservableCollection<string> GameVersionOptions { get; } = [];

    /// <summary>分类筛选选项：按资源类型取 PCL-CE 同款固定全集；不随搜索结果变化。</summary>
    public ObservableCollection<string> CategoryOptions { get; } = [];

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

    public ObservableCollection<string> TargetInstanceOptions { get; } = [];

    [ObservableProperty]
    private bool _isSaveDialogVisible;

    /// <summary>整合包：只选择实例名，安装到 versions 下（对应 PCL-CE Install_Click 的实例名输入）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveDialogTitle))]
    [NotifyPropertyChangedFor(nameof(SaveDialogConfirmText))]
    [NotifyPropertyChangedFor(nameof(SaveDialogBody))]
    [NotifyPropertyChangedFor(nameof(IsInstanceSelectable))]
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

    public bool IsInstanceSelectable => !IsModpackSave;

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

            _lastResult = new CompSearchResult { Projects = projects, CurseForgeTotal = cfTotal, ModrinthTotal = mrTotal };
            Results.Clear();
            foreach (var project in projects)
            {
                Results.Add(new CompProjectItemViewModel(project));
            }

            ResultSummary = $"共 {projects.Count} 条结果";
            CanGoPrevious = _pageIndex > 0;
            CanGoNext = projects.Count >= 40
                        && (cfTotal > (_pageIndex + 1) * 40 || mrTotal > (_pageIndex + 1) * 40);
            if (SelectedProject is not null && !projects.Any(p => p.Id == SelectedProject.Project.Id))
            {
                SelectedProject = null;
            }
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
        SelectedProjectUpdateText = item.UpdateText;
        SelectedProjectDownloadText = item.DownloadText;
        SelectedProjectGameVersionText = item.GameVersionText;
        SelectedProjectLoaderText = item.LoaderText;
        VersionErrorText = "";
        ResetDetailFilters();
        VersionFiles.Clear();
        IsLoadingVersions = true;
        var translation = CompLocalization.TranslateDescriptionAsync(_services.DownloadClient, item.Project, token);
        try
        {
            var files = item.Project.FromCurseForge
                ? await CurseForge.GetFilesAsync(item.Project.Id, Type, token).ConfigureAwait(true)
                : await Modrinth.GetFilesAsync(item.Project.Id, Type, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _detailGeneration)
            {
                return;
            }

            _detailFiles = files;
            BuildDetailFilters();
            ApplyDetailFilter();

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
        ResetDetailFilters();
        VersionFiles.Clear();
        VersionErrorText = "";
    }

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
    private async Task LoadSaveDialogInstancesAsync(CompFile file)
    {
        _dialogInstances.Clear();
        TargetInstanceOptions.Clear();
        var gameDirectory = _services.Config.EffectiveGameDirectory;
        var subFolder = CompInstallService.GetSubFolder(file.Type);
        List<MinecraftInstance> instances;
        try
        {
            instances = (await _services.Instances.ScanAsync(gameDirectory).ConfigureAwait(true)).ToList();
        }
        catch (Exception ex)
        {
            SaveDirectory = string.IsNullOrEmpty(subFolder)
                ? gameDirectory
                : Path.Combine(gameDirectory, subFolder);
            SelectedTargetInstance = "";
            SaveHint = $"扫描实例失败：{ex.Message}";
            return;
        }

        var compatible = instances
            .Where(instance => file.GameVersions.Count == 0
                               || file.GameVersions.Any(v => v.Equals(instance.GameVersion, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(instance => instance.LastWriteTime)
            .ToList();
        var preferred = _preferredInstanceId is { Length: > 0 }
            ? instances.FirstOrDefault(instance =>
                instance.Id.Equals(_preferredInstanceId, StringComparison.OrdinalIgnoreCase))
            : null;
        var current = instances.FirstOrDefault(instance =>
            instance.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase));
        if (preferred is not null && compatible.Contains(preferred))
        {
            compatible.Remove(preferred);
            compatible.Insert(0, preferred);
        }
        else if (current is not null && compatible.Contains(current))
        {
            compatible.Remove(current);
            compatible.Insert(0, current);
        }

        foreach (var instance in compatible.Concat(instances.Except(compatible)))
        {
            _dialogInstances.Add(instance);
            TargetInstanceOptions.Add($"{instance.Name} · {instance.Description}");
        }

        var target = compatible.FirstOrDefault();
        if (target is null)
        {
            SelectedTargetInstance = "";
            SaveDirectory = string.IsNullOrEmpty(subFolder)
                ? gameDirectory
                : Path.Combine(gameDirectory, subFolder);
            SaveHint = "未找到可用实例，将保存到游戏目录，也可手动修改路径。";
            return;
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


    /// <summary>重建版本筛选选项：正式版全集（静态基线 + 版本清单动态补入）+ 本机实例版本，按版本号降序，保持当前选中项。</summary>
    private void RebuildGameVersionOptions(IEnumerable<string>? extra = null)
    {
        var selected = SelectedGameVersion;
        var versions = CompLocalization.ReleaseGameVersions
            .Concat(extra ?? [])
            .Where(CompLocalization.IsReleaseGameVersion)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(version => version, CompLocalization.VersionComparer)
            .ToList();

        // 从实例资源列表跳转时可能带入快照版本；异步重建选项时保留它，避免筛选被重置为「全部版本」
        if (selected != "全部版本" && !versions.Contains(selected, StringComparer.OrdinalIgnoreCase))
        {
            versions.Insert(0, selected);
        }

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
            await LoadReleaseVersionsAsync().ConfigureAwait(true);
            RebuildGameVersionOptions(instances.Select(instance => instance.GameVersion));
        }
        catch
        {
            // 筛选选项只是增强信息，扫描失败不应影响搜索
        }
    }

    /// <summary>版本清单只取一次（多个分区共用同一份正式版列表）。</summary>
    private static Task<IReadOnlyList<string>>? _releaseVersionsTask;

    private Task<IReadOnlyList<string>> LoadReleaseVersionsAsync()
        => _releaseVersionsTask ??= FetchReleaseVersionsAsync();

    /// <summary>从版本清单取正式版版本号并并入筛选全集；失败时保留静态基线，不影响搜索。</summary>
    private async Task<IReadOnlyList<string>> FetchReleaseVersionsAsync()
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
            return releases;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _services.Logger.Warn($"获取正式版版本列表失败（筛选仅使用静态基线）：{ex.Message}");
            return [];
        }
    }

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
