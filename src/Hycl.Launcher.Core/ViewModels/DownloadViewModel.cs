using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>下载页：版本清单、过滤、原版 / Fabric 安装与进度。</summary>
public sealed partial class DownloadViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private List<VersionItem> _allVersions = new();
    private (int VersionList, int File)? _loadedSource;
    private CancellationTokenSource? _installCts;

    public DownloadViewModel(LauncherServices services)
    {
        _services = services;
        Categories = new ObservableCollection<SidebarItem>
        {
            new("vanilla", "原版游戏", "Minecraft 正式版与快照"),
            new("fabric", "Fabric", "轻量模组加载器"),
            new("forge", "Forge", "经典模组加载器（尚未移植）"),
            new("neoforge", "NeoForge", "新一代模组加载器（尚未移植）"),
            new("optifine", "OptiFine", "光影前置优化（尚未移植）"),
            new("modpack", "整合包", "一键安装整合包（尚未移植）"),
            new("mod", "模组", "Modrinth / CurseForge（尚未移植）"),
            new("resourcepack", "资源包", "材质与音效（尚未移植）"),
            new("shader", "光影", "Iris / OptiFine 光影（尚未移植）"),
            new("world", "世界", "存档与世界（尚未移植）"),
            new("favorite", "收藏", "收藏的资源（尚未移植）")
        };
        SelectedCategoryItem = Categories[0];
    }

    public ObservableCollection<SidebarItem> Categories { get; }

    [ObservableProperty]
    private SidebarItem? _selectedCategoryItem;

    [ObservableProperty]
    private IReadOnlyList<VersionItem> _filteredVersions = Array.Empty<VersionItem>();

    [ObservableProperty]
    private VersionItem? _selectedVersion;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private bool _showSnapshots;

    [ObservableProperty]
    private bool _isLoadingVersions;

    [ObservableProperty]
    private string _loadError = "";

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _installStage = "";

    [ObservableProperty]
    private string _installMessage = "";

    [ObservableProperty]
    private string _installProgressText = "";

    [ObservableProperty]
    private double _installPercent;

    [ObservableProperty]
    private string _installStatus = "";

    public bool IsVanillaCategory => SelectedCategoryItem?.Key == "vanilla";

    public bool IsFabricCategory => SelectedCategoryItem?.Key == "fabric";

    public bool IsPlaceholderCategory => SelectedCategoryItem is not null && !IsVanillaCategory && !IsFabricCategory;

    public bool IsVersionListVisible => IsVanillaCategory || IsFabricCategory;

    public string InstallHint => IsFabricCategory
        ? "安装 Fabric 前会检查对应原版是否已安装，缺失时自动补全原版文件。"
        : "安装包含客户端 jar、依赖库、资源文件与日志配置；已存在的文件会自动跳过。";

    public string PlaceholderTitle => $"{SelectedCategoryItem?.Title} 尚未移植";

    public string PlaceholderDescription => SelectedCategoryItem?.Key switch
    {
        "forge" or "neoforge" =>
            "Forge / NeoForge 需要执行官方安装器并处理复杂的继承链，当前版本尚未实现。" +
            "你可以先安装原版或 Fabric，或使用官方安装器后把版本放入 versions 目录，Hycl 会自动识别。",
        "optifine" =>
            "OptiFine 安装涉及修改客户端 jar，当前版本尚未实现。可手动安装后将版本放入 versions 目录。",
        "modpack" =>
            "整合包安装（Modrinth / CurseForge）涉及额外的打包格式解析，尚未移植。",
        "mod" =>
            "模组搜索与安装尚未移植。可把模组 jar 放入实例的 mods 目录。",
        "resourcepack" or "shader" or "world" =>
            "资源下载分类尚未移植。可手动将文件放入对应的游戏目录。",
        _ => "该分类尚未移植。当前版本聚焦原版与 Fabric 安装。"
    };

    public bool HasVersions => FilteredVersions.Count > 0;

    public bool HasLoadError => LoadError.Length > 0;

    public bool CanInstallVanilla => !IsInstalling && IsVanillaCategory && SelectedVersion is not null;

    public bool CanInstallFabric => !IsInstalling && IsFabricCategory && SelectedVersion is not null;

    public bool CanCancelInstall => IsInstalling;

    public bool CanRefresh => !IsInstalling && !IsLoadingVersions;

    public string SelectedVersionText => SelectedVersion is null ? "未选择版本" : SelectedVersion.Id;

    public string SourceText
    {
        get
        {
            var config = _services.Config;
            var listText = config.VersionListSource switch
            {
                (int)DownloadSourceMode.MirrorFirst => "镜像优先",
                (int)DownloadSourceMode.OfficialOnly => "仅官方",
                _ => "官方优先（自动切换）"
            };
            var fileText = config.FileSource switch
            {
                (int)DownloadSourceMode.MirrorFirst => "镜像优先",
                (int)DownloadSourceMode.OfficialOnly => "仅官方",
                _ => "官方优先（自动切换）"
            };
            return $"版本列表源：{listText} · 下载文件源：{fileText}";
        }
    }

    public string VersionCountText
    {
        get
        {
            if (IsLoadingVersions)
            {
                return "正在加载版本列表…";
            }

            return FilteredVersions.Count == 0 ? "没有符合条件的版本" : $"共 {FilteredVersions.Count} 个版本";
        }
    }

    public string InstallButtonText => IsInstalling ? "正在安装…" : "安装此版本";

    public event EventHandler<string>? VersionInstalled;

    /// <summary>进入下载页时调用；仅在首次或下载源变化时拉取清单。</summary>
    public async Task EnsureVersionsAsync(bool force = false)
    {
        var source = DownloadPolicy.FromConfig(_services.Config);
        OnPropertyChanged(nameof(SourceText));
        if (!force && _loadedSource == (source.VersionListSource, source.FileSource) && _allVersions.Count > 0)
        {
            ApplyFilter();
            return;
        }

        IsLoadingVersions = true;
        LoadError = "";
        OnPropertyChanged(nameof(HasLoadError));
        OnPropertyChanged(nameof(VersionCountText));
        OnPropertyChanged(nameof(CanRefresh));
        InstallStatus = "正在获取版本清单…";
        try
        {
            var versions = await _services.Installer.GetVersionListAsync(source).ConfigureAwait(true);
            var latestRelease = versions.FirstOrDefault(x => x.Type.Equals("release", StringComparison.OrdinalIgnoreCase))?.Id;
            _allVersions = versions
                .Select(x => new VersionItem(x, x.Id == latestRelease))
                .ToList();
            _loadedSource = (source.VersionListSource, source.FileSource);
            IsLoadingVersions = false;
            InstallStatus = "版本清单已就绪";
            ApplyFilter();
        }
        catch (Exception ex)
        {
            IsLoadingVersions = false;
            LoadError = $"获取版本清单失败：{ex.Message}";
            InstallStatus = LoadError;
            _services.Logger.Error("获取版本清单失败", ex);
        }
        finally
        {
            IsLoadingVersions = false;
            OnPropertyChanged(nameof(CanRefresh));
            OnPropertyChanged(nameof(VersionCountText));
            OnPropertyChanged(nameof(HasLoadError));
            NotifyInstallAvailability();
        }
    }

    public void InvalidateVersions()
    {
        _loadedSource = null;
    }

    [RelayCommand]
    private async Task RefreshVersionsAsync()
    {
        await EnsureVersionsAsync(true).ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task InstallVanillaAsync()
    {
        var version = SelectedVersion;
        if (version is null || IsInstalling)
        {
            return;
        }

        var config = _services.Config;
        _installCts = new CancellationTokenSource();
        IsInstalling = true;
        InstallStatus = "";
        NotifyInstallAvailability();
        try
        {
            var progress = CreateProgress();
            await _services.Installer.InstallVanillaAsync(
                    config.EffectiveGameDirectory,
                    version.Summary,
                    DownloadPolicy.FromConfig(config),
                    config.DownloadThreads,
                    progress,
                    _installCts.Token)
                .ConfigureAwait(true);

            InstallStatus = $"版本 {version.Id} 安装完成，可回到主页启动";
            VersionInstalled?.Invoke(this, version.Id);
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "安装已取消";
        }
        catch (DownloadException ex)
        {
            InstallStatus = $"安装失败：{ex.Message}";
            _services.Logger.Error("安装失败", ex);
        }
        catch (Exception ex)
        {
            InstallStatus = $"安装失败：{ex.Message}";
            _services.Logger.Error("安装失败", ex);
        }
        finally
        {
            IsInstalling = false;
            _installCts.Dispose();
            _installCts = null;
            NotifyInstallAvailability();
        }
    }

    [RelayCommand]
    private async Task InstallFabricAsync()
    {
        var version = SelectedVersion;
        if (version is null || IsInstalling)
        {
            return;
        }

        var config = _services.Config;
        _installCts = new CancellationTokenSource();
        IsInstalling = true;
        InstallStatus = "";
        NotifyInstallAvailability();
        try
        {
            var progress = CreateProgress();
            var installed = await _services.Installer.InstallFabricAsync(
                    config.EffectiveGameDirectory,
                    version.Id,
                    DownloadPolicy.FromConfig(config),
                    config.DownloadThreads,
                    progress,
                    _installCts.Token)
                .ConfigureAwait(true);

            InstallStatus = $"Fabric 安装完成：{installed}";
            VersionInstalled?.Invoke(this, installed);
        }
        catch (OperationCanceledException)
        {
            InstallStatus = "安装已取消";
        }
        catch (DownloadException ex)
        {
            InstallStatus = $"Fabric 安装失败：{ex.Message}";
            _services.Logger.Error("Fabric 安装失败", ex);
        }
        catch (Exception ex)
        {
            InstallStatus = $"Fabric 安装失败：{ex.Message}";
            _services.Logger.Error("Fabric 安装失败", ex);
        }
        finally
        {
            IsInstalling = false;
            _installCts.Dispose();
            _installCts = null;
            NotifyInstallAvailability();
        }
    }

    [RelayCommand]
    private void CancelInstall()
    {
        if (_installCts is not null)
        {
            InstallStatus = "正在取消安装…";
            _installCts.Cancel();
        }
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        _services.System.OpenFolder(_services.Config.EffectiveGameDirectory);
    }

    private IProgress<InstallProgress> CreateProgress()
    {
        return new Progress<InstallProgress>(progress =>
        {
            InstallStage = progress.Stage;
            InstallMessage = progress.CurrentItem;
            InstallPercent = progress.Percentage * 100;
            InstallProgressText = progress.FilesTotal > 0
                ? $"{InstallPercent:0}% · {progress.FilesDone}/{progress.FilesTotal}"
                : $"{InstallPercent:0}%";
        });
    }

    private void ApplyFilter()
    {
        if (SelectedCategoryItem is null)
        {
            return;
        }

        if (IsPlaceholderCategory)
        {
            FilteredVersions = Array.Empty<VersionItem>();
            SelectedVersion = null;
            OnPropertyChanged(nameof(HasVersions));
            OnPropertyChanged(nameof(VersionCountText));
            NotifyInstallAvailability();
            return;
        }

        IEnumerable<VersionItem> items = _allVersions;
        if (IsFabricCategory || !ShowSnapshots)
        {
            items = items.Where(x => x.IsRelease);
        }

        var keyword = SearchText.Trim();
        if (keyword.Length > 0)
        {
            items = items.Where(x => x.Id.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        var previousId = SelectedVersion?.Id;
        var list = items.ToList();
        FilteredVersions = list;
        SelectedVersion = list.FirstOrDefault(x => x.Id == previousId) ?? list.FirstOrDefault();
        OnPropertyChanged(nameof(HasVersions));
        OnPropertyChanged(nameof(VersionCountText));
        NotifyInstallAvailability();
    }

    private void NotifyInstallAvailability()
    {
        OnPropertyChanged(nameof(CanInstallVanilla));
        OnPropertyChanged(nameof(CanInstallFabric));
        OnPropertyChanged(nameof(CanCancelInstall));
        OnPropertyChanged(nameof(InstallButtonText));
        OnPropertyChanged(nameof(SelectedVersionText));
        OnPropertyChanged(nameof(VersionCountText));
    }

    partial void OnSelectedCategoryItemChanged(SidebarItem? value)
    {
        OnPropertyChanged(nameof(IsVanillaCategory));
        OnPropertyChanged(nameof(IsFabricCategory));
        OnPropertyChanged(nameof(IsPlaceholderCategory));
        OnPropertyChanged(nameof(IsVersionListVisible));
        OnPropertyChanged(nameof(InstallHint));
        OnPropertyChanged(nameof(PlaceholderTitle));
        OnPropertyChanged(nameof(PlaceholderDescription));
        ApplyFilter();
    }

    partial void OnSelectedVersionChanged(VersionItem? value)
    {
        OnPropertyChanged(nameof(SelectedVersionText));
        NotifyInstallAvailability();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowSnapshotsChanged(bool value) => ApplyFilter();

    partial void OnIsInstallingChanged(bool value) => NotifyInstallAvailability();
}
