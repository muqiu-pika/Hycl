using System.Collections.ObjectModel;
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
        IconUrl = project.IconUrl;
        SourceText = project.FromCurseForge ? "CurseForge" : "Modrinth";
        DownloadText = project.DownloadCount >= 10000
            ? $"{project.DownloadCount / 10000.0:0.#} 万下载"
            : $"{project.DownloadCount} 下载";
        Tags = CompLocalization.DisplayTags(project);
    }

    public CompProject Project { get; }

    public string Title { get; }

    public string Description { get; }

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
    public static readonly string[] CommonGameVersions =
    [
        "全部版本", "1.21.1", "1.21", "1.20.6", "1.20.4", "1.20.1", "1.19.4", "1.19.2",
        "1.18.2", "1.16.5", "1.12.2", "1.10.2", "1.8.9", "1.7.10"
    ];

    public static readonly string[] LoaderOptions = ["全部加载器", "Forge", "NeoForge", "Fabric", "Quilt"];

    public static readonly string[] SortOptions = ["综合排序", "相关性", "下载量", "关注数", "最新发布", "最近更新"];

    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private CompSearchResult? _lastResult;
    private int _pageIndex;
    private string? _lastQuerySignature;

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
        if (IsSearching)
        {
            return;
        }

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
                tasks.Add(CurseForge.SearchAsync(request));
            }

            if (source.HasFlag(CompSourceType.Modrinth))
            {
                tasks.Add(Modrinth.SearchAsync(request));
            }

            var results = await Task.WhenAll(tasks).ConfigureAwait(true);
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

            ResultSummary = $"共 {projects.Count} 条结果（CurseForge {Math.Max(0, cfTotal)} / Modrinth {Math.Max(0, mrTotal)}）";
            CanGoPrevious = _pageIndex > 0;
            CanGoNext = projects.Count >= 40
                        && (cfTotal > (_pageIndex + 1) * 40 || mrTotal > (_pageIndex + 1) * 40);
            if (SelectedProject is not null && !projects.Any(p => p.Id == SelectedProject.Project.Id))
            {
                SelectedProject = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ErrorText = $"搜索失败：{ex.Message}";
            _services.Logger.Error("社区资源搜索失败", ex);
        }
        finally
        {
            IsSearching = false;
            OnPropertyChanged(nameof(CanGoPrevious));
            OnPropertyChanged(nameof(CanGoNext));
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

        SelectedProject = item;
        SelectedProjectDescription = CompLocalization.DisplayDescription(item.Project);
        VersionErrorText = "";
        VersionFiles.Clear();
        IsLoadingVersions = true;
        var translation = CompLocalization.TranslateDescriptionAsync(_services.DownloadClient, item.Project);
        try
        {
            var files = item.Project.FromCurseForge
                ? await CurseForge.GetFilesAsync(item.Project.Id, Type).ConfigureAwait(true)
                : await Modrinth.GetFilesAsync(item.Project.Id, Type).ConfigureAwait(true);
            VersionFiles.Clear();
            foreach (var file in files.Take(60))
            {
                VersionFiles.Add(new CompFileItemViewModel(file, this));
            }

            if (VersionFiles.Count == 0)
            {
                VersionErrorText = "该项目没有可下载的文件";
            }
        }
        catch (Exception ex)
        {
            VersionErrorText = $"获取版本列表失败：{ex.Message}";
        }
        finally
        {
            IsLoadingVersions = false;
        }

        var translated = await translation.ConfigureAwait(true);
        if (SelectedProject == item)
        {
            SelectedProjectDescription = translated;
        }
    }

    [RelayCommand]
    private void CloseDetail()
    {
        SelectedProject = null;
        SelectedProjectDescription = "";
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

            await _install.RunCompModpackAsync(item.File, instanceName).ConfigureAwait(true);
            return;
        }

        var gameDirectory = _services.Config.EffectiveGameDirectory;
        var target = await _services.CompInstall
            .ResolveTargetAsync(gameDirectory, item.File)
            .ConfigureAwait(true);
        if (target is null)
        {
            _toast?.Invoke("尚未安装任何 Minecraft 版本，请先在「Minecraft」分区安装原版或加载器");
            return;
        }

        item.IsInstalling = true;
        try
        {
            await _install.RunCompFileAsync(item.File, target.Id, Title).ConfigureAwait(true);
        }
        finally
        {
            item.IsInstalling = false;
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
