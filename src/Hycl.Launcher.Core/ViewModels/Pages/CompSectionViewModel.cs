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
            if (File.GameVersions.Count == 0)
            {
                return "";
            }

            var first = CompLocalization.GameVersionName(File.GameVersions[0]);
            var last = CompLocalization.GameVersionName(File.GameVersions[^1]);
            return File.GameVersions.Count == 1 ? first : $"{first} ~ {last}";
        }
    }

    [ObservableProperty]
    private bool _isInstalling;

    [RelayCommand]
    private async Task InstallAsync() => await _owner.InstallFileAsync(this).ConfigureAwait(true);
}

/// <summary>
/// 下载页「社区资源」分区（对应 PCL-CE PageComp）：搜索 + 筛选 + 结果列表 + 版本列表 + 分页。
/// </summary>
public partial class CompSectionViewModel : ObservableObject
{
    /// <summary>常用游戏版本筛选（对应 PCL-CE TextSearchVersion 预置列表）。</summary>
    public static readonly IReadOnlyList<string> CommonGameVersions =
    [
        "全部版本", "1.21.1", "1.21", "1.20.6", "1.20.4", "1.20.1", "1.19.4", "1.19.2",
        "1.18.2", "1.16.5", "1.12.2", "1.10.2", "1.8.9", "1.7.10"
    ];

    public static readonly IReadOnlyList<string> LoaderOptions = ["全部加载器", "Forge", "NeoForge", "Fabric", "Quilt"];

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

        // 上次离开页面时搜索结果被取消且列表仍为空：重新发起一次，保证再次进入能自动加载
        if (_searchInterrupted && Results.Count == 0)
        {
            _ = SearchAsync();
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
        // 保存位置选择框随页面一起关闭：尚未开始下载，不产生任何文件
        IsSaveDialogVisible = false;
        _pendingFile = null;
        SaveHint = "";
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

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedGameVersion = "全部版本";

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

            VersionFiles.Clear();
            foreach (var file in files.Take(60))
            {
                VersionFiles.Add(new CompFileItemViewModel(file, this));
            }

            if (VersionFiles.Count == 0)
            {
                VersionErrorText = "该项目没有可下载的文件";
            }

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
        if (item.IsInstalling || _install.IsInstalling || IsSaveDialogVisible)
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
            InstanceName = SuggestedInstanceName(item);
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
        var current = instances.FirstOrDefault(instance =>
            instance.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase));
        if (current is not null && compatible.Contains(current))
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
        item.IsInstalling = true;
        _installingItem = item;
        try
        {
            await _install.RunCompFileToDirectoryAsync(item.File, directory, Title).ConfigureAwait(true);
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

    private async Task ConfirmModpackSaveAsync(CompFileItemViewModel item)
    {
        var name = InstanceName.Trim();
        if (name.Length == 0)
        {
            SaveHint = "请输入实例名称";
            return;
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..", StringComparison.Ordinal))
        {
            SaveHint = "实例名称包含非法字符";
            return;
        }

        if (Directory.Exists(Path.Combine(_services.Config.EffectiveGameDirectory, "versions", name)))
        {
            SaveHint = "同名实例已存在，请换一个名称";
            return;
        }

        IsSaveDialogVisible = false;
        _pendingFile = null;
        SaveHint = "";
        item.IsInstalling = true;
        _installingItem = item;
        try
        {
            await _install.RunCompModpackAsync(item.File, name).ConfigureAwait(true);
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
