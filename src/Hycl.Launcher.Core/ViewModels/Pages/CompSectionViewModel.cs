using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
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

    /// <summary>安装资源：ModPack 走整合包流程（自动装原版与加载器），其余落盘到实例对应目录。</summary>
    public async Task InstallFileAsync(CompFileItemViewModel item)
    {
        if (item.IsInstalling || _install.IsInstalling)
        {
            return;
        }

        if (item.File.Type == CompResourceType.ModPack)
        {
            var rawName = SelectedProject?.Project.Title ?? item.Title;
            var instanceName = string.Join("_", rawName.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries))
                .Trim();
            if (instanceName.Length == 0)
            {
                instanceName = "Modpack";
            }

            _installingItem = item;
            try
            {
                await _install.RunCompModpackAsync(item.File, instanceName).ConfigureAwait(true);
            }
            finally
            {
                if (ReferenceEquals(_installingItem, item))
                {
                    _installingItem = null;
                }
            }

            return;
        }

        var gameDirectory = _services.Config.EffectiveGameDirectory;
        var target = await _services.CompInstall
            .ResolveTargetAsync(gameDirectory, item.File)
            .ConfigureAwait(true);
        if (!_attached)
        {
            // 页面已离开，不再发起新的下载
            return;
        }

        if (target is null)
        {
            _toast?.Invoke("尚未安装任何 Minecraft 版本，请先在「Minecraft」分区安装原版或加载器");
            return;
        }

        item.IsInstalling = true;
        _installingItem = item;
        try
        {
            await _install.RunCompFileAsync(item.File, target.Id, Title).ConfigureAwait(true);
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
        // CurseForge 走镜像（Key 留空时官方 403 由镜像兜底），双源并行
        return CompSourceType.Any;
    }

    private static string Signature(CompSearchRequest request)
        => $"{request.Type}:{request.Query}:{request.GameVersion}:{request.Loader}:{request.Tag}:{request.Sort}:{request.Offset}";
}
