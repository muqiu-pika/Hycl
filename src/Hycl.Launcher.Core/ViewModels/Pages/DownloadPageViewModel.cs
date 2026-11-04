using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 下载页 ViewModel：左侧分区导航 + 安装向导 + 底部安装条。
/// 界面结构来自 Hycl (2)，网络与安装动作全部调用 Core 的加载器安装门面。
/// </summary>
public partial class DownloadPageViewModel : ObservableObject, IPageLifecycle
{
    private readonly LauncherServices _services;

    public DownloadPageViewModel(LauncherServices services, Action<string>? toast)
    {
        _services = services;
        Install = new InstallTaskViewModel(services, toast);
        Wizard = new InstallWizardViewModel(services, Install, toast);
        Install.Completed += OnInstallCompleted;
        Wizard.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstallWizardViewModel.IsStep2) or nameof(InstallWizardViewModel.Step))
            {
                OnPropertyChanged(nameof(IsStartButtonVisible));
            }
        };
        Install.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InstallTaskViewModel.IsInstalling))
            {
                OnPropertyChanged(nameof(IsStartButtonVisible));
            }
        };

        var minecraft = new NavItemViewModel("Minecraft", "grass-block");
        CompSections = new List<CompSectionViewModel>
        {
            new(services, Install, CompResourceType.ModPack, "整合包", "package", "搜索并安装 Modrinth / CurseForge 整合包，自动安装所需原版与加载器", toast),
            new(services, Install, CompResourceType.Mod, "模组", "forge", "搜索 Modrinth / CurseForge 模组，安装到实例 mods 目录", toast),
            new(services, Install, CompResourceType.ResourcePack, "资源包", "cobblestone", "搜索并安装资源包（原版可用，无需加载器）", toast),
            new(services, Install, CompResourceType.Shader, "光影", "gold-block", "搜索 Iris / OptiFine 光影包，安装到 shaderpacks 目录", toast)
        };
        Groups = new ObservableCollection<NavGroupViewModel>
        {
            new("", [minecraft]),
            new("安装包",
            [
                new NavItemViewModel("Minecraft 客户端", "vanilla", CreateClientSection()),
                new NavItemViewModel("Forge", "forge", CreateLoaderSection("forge", "Forge", "forge")),
                new NavItemViewModel("NeoForge", "neoforge", CreateLoaderSection("neoforge", "NeoForge", "neoforge")),
                new NavItemViewModel("Cleanroom", "cleanroom", CreateLoaderSection("cleanroom", "Cleanroom", "cleanroom")),
                new NavItemViewModel("Fabric", "fabric", CreateLoaderSection("fabric", "Fabric", "fabric")),
                new NavItemViewModel("Quilt", "quilt", CreateLoaderSection("quilt", "Quilt", "quilt")),
                new NavItemViewModel("Legacy Fabric", "legacyfabric", CreateLoaderSection("legacyfabric", "Legacy Fabric", "fabric")),
                new NavItemViewModel("OptiFine", "optifine", CreateLoaderSection("optifine", "OptiFine", "optifine")),
                new NavItemViewModel("LiteLoader", "liteloader", CreateLoaderSection("liteloader", "LiteLoader", "liteloader")),
                new NavItemViewModel("LabyMod", "labymod", CreateLoaderSection("labymod", "LabyMod", "labymod"))
            ]),
            new("社区资源",
            [
                new NavItemViewModel("整合包", "package", CompSections[0]),
                new NavItemViewModel("模组", "forge", CompSections[1]),
                new NavItemViewModel("资源包", "cobblestone", CompSections[2]),
                new NavItemViewModel("光影", "gold-block", CompSections[3])
            ])
        };

        SelectedSection = minecraft;

        // 保存位置弹窗的可见性需要跨分区转达：ActiveCompSection 为空时一律隐藏
        foreach (var section in CompSections)
        {
            section.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CompSectionViewModel.IsSaveDialogVisible))
                {
                    OnPropertyChanged(nameof(IsSaveDialogVisible));
                }
            };
        }
    }

    /// <summary>社区资源分区（与左侧导航一一对应）。</summary>
    public IReadOnlyList<CompSectionViewModel> CompSections { get; }

    /// <summary>当前选中的社区资源分区（非 null 时显示社区资源界面）。</summary>
    public CompSectionViewModel? ActiveCompSection =>
        SelectedSection?.Section as CompSectionViewModel;

    public ObservableCollection<NavGroupViewModel> Groups { get; }

    public InstallWizardViewModel Wizard { get; }

    public InstallTaskViewModel Install { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallSection))]
    [NotifyPropertyChangedFor(nameof(IsCompSection))]
    [NotifyPropertyChangedFor(nameof(ActiveCompSection))]
    private NavItemViewModel? _selectedSection;

    [ObservableProperty]
    private LoaderSectionViewModel? _activeSection;

    /// <summary>安装向导入口（首项）显示向导，其余分区显示加载器版本列表。</summary>
    public bool IsInstallSection => SelectedSection?.Section is null;

    /// <summary>社区资源分区可见性。</summary>
    public bool IsCompSection => ActiveCompSection is not null;

    /// <summary>下载前保存位置弹窗（仅社区资源分区有效）。</summary>
    public bool IsSaveDialogVisible => ActiveCompSection?.IsSaveDialogVisible == true;

    public bool IsStartButtonVisible => IsInstallSection && Wizard.IsStep2 && !Install.IsInstalling;

    public event EventHandler<string>? Installed;

    private LoaderSectionViewModel CreateClientSection() => new(
        "Client",
        "Minecraft 客户端",
        "点击版本可直接安装原版客户端；安装进度显示在底部。",
        LoadClientGroupsAsync);

    private LoaderSectionViewModel CreateLoaderSection(string kind, string title, string iconKey) => new(
        kind,
        title,
        $"点击 Minecraft 版本可为该版本安装 {title} 的最新稳定版；具体加载器版本请在向导中选择。",
        token => LoadLoaderGroupsAsync(kind, iconKey, token));

    private async Task<IReadOnlyList<VersionGroupViewModel>> LoadClientGroupsAsync(CancellationToken token)
    {
        var versions = await _services.Installer
            .GetVersionListAsync(DownloadPolicy.FromConfig(_services.Config), token)
            .ConfigureAwait(true);
        return InstallWizardViewModel.BuildGroups(versions);
    }

    private async Task<IReadOnlyList<VersionGroupViewModel>> LoadLoaderGroupsAsync(
        string kind,
        string iconKey,
        CancellationToken token)
    {
        var policy = DownloadPolicy.FromConfig(_services.Config);
        var provider = _services.LoaderProviders.Get(Enum.Parse<LoaderKind>(kind, true));
        var mcVersions = await provider.GetSupportedMcVersionsAsync(policy, token).ConfigureAwait(true);
        return InstallWizardViewModel.BuildMcVersionGroups(mcVersions, iconKey);
    }

    /// <summary>离开下载页：取消在途的版本列表、社区搜索/详情与安装下载，回到可再次发起的状态。</summary>
    public void Deactivate()
    {
        Wizard.CancelPending();
        // 只取消仍处于下载/准备阶段的任务；解压与安装原版/加载器等不可逆阶段继续完成
        Install.CancelIfDownloading();
        ActiveSection?.CancelPending();
        foreach (var section in CompSections)
        {
            section.Detach();
        }
    }

    /// <summary>
    /// 窗口关闭：取消所有在途任务（含安装/解压阶段），并等待下载临时文件与半成品目录回滚完成。
    /// </summary>
    public void Shutdown()
    {
        Wizard.CancelPending();
        Install.CancelForShutdown();
        ActiveSection?.CancelPending();
        foreach (var section in CompSections)
        {
            section.Detach();
        }
    }

    /// <summary>重新进入下载页：恢复社区分区的安装进度监听。</summary>
    public void Activate()
    {
        foreach (var section in CompSections)
        {
            section.Attach();
        }
    }

    partial void OnSelectedSectionChanged(NavItemViewModel? value)
    {
        foreach (var item in Groups.SelectMany(group => group.Items))
        {
            item.IsActive = ReferenceEquals(item, value);
        }

        // 切换分区时取消上一个分区仍在进行的版本列表请求
        ActiveSection?.CancelPending();
        if (value?.Section is LoaderSectionViewModel section)
        {
            ActiveSection = section;
            _ = section.RefreshAsync();
        }
        else
        {
            ActiveSection = null;
            if (!IsCompSection)
            {
                _ = Wizard.LoadVersionListAsync();
            }
        }

        if (ActiveCompSection is { Results.Count: 0 } compSection && !compSection.IsSearching)
        {
            _ = compSection.SearchFromStartCommand.ExecuteAsync(null);
        }

        OnPropertyChanged(nameof(IsStartButtonVisible));
        OnPropertyChanged(nameof(IsSaveDialogVisible));
    }

    [RelayCommand]
    private void SelectSection(NavItemViewModel? item) => SelectedSection = item;

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsInstallSection)
        {
            Wizard.Invalidate();
            await Wizard.LoadVersionListAsync(force: true).ConfigureAwait(true);
            return;
        }

        if (ActiveCompSection is not null)
        {
            await ActiveCompSection.SearchFromStartCommand.ExecuteAsync(null).ConfigureAwait(true);
            return;
        }

        if (ActiveSection is not null)
        {
            _services.LoaderProviders.Cache.Clear();
            await ActiveSection.RefreshAsync(force: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ActivateSectionItemAsync(VersionItemViewModel? item)
    {
        if (item?.Payload is not VersionSummary summary || Install.IsInstalling || ActiveSection is null)
        {
            return;
        }

        // 版本行单选：点谁只高亮谁，避免整组条目共享同一个选中状态
        ActiveSection.SelectItem(item);
        var kind = ActiveSection.Kind.ToLowerInvariant();
        if (kind is "client")
        {
            await Install.RunVanillaAsync(summary, summary.Id).ConfigureAwait(true);
            return;
        }

        // 加载器分区：安装该加载器在所选 MC 版本下的最新稳定版
        var targetName = kind switch
        {
            "optifine" => $"{summary.Id}-optifine",
            "liteloader" => $"{summary.Id}-liteloader",
            _ => $"{summary.Id}-{kind}"
        };
        var companionModSlug = kind switch
        {
            "fabric" => "fabric-api",
            "legacyfabric" => "legacy-fabric-api",
            _ => null
        };
        var plan = new LoaderInstallPlan
        {
            Kind = kind,
            GameDirectory = _services.Config.EffectiveGameDirectory,
            GameVersion = summary.Id,
            TargetName = targetName,
            CompanionModSlug = companionModSlug,
            Policy = DownloadPolicy.FromConfig(_services.Config),
            Threads = _services.Config.DownloadThreads,
            JavaPath = _services.Config.JavaPath
        };
        await Install.RunPlanAsync(plan, $"{summary.Id} · {ActiveSection.Title}").ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelInstall() => Install.Cancel();

    private void OnInstallCompleted(object? sender, string installedId)
    {
        Installed?.Invoke(this, installedId);
    }
}
