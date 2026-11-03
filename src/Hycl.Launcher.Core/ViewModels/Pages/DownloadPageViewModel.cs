using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 下载页 ViewModel：左侧分区导航 + 安装向导 + 底部安装条。
/// 界面结构来自 Hycl (2)，网络与安装动作全部调用 Core 的加载器安装门面。
/// </summary>
public partial class DownloadPageViewModel : ObservableObject
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
        Groups = new ObservableCollection<NavGroupViewModel>
        {
            new("", [minecraft]),
            new("安装包",
            [
                new NavItemViewModel("Minecraft 客户端", "vanilla", CreateClientSection()),
                new NavItemViewModel("Forge", "forge", CreateLoaderSection("forge", "Forge", "anvil")),
                new NavItemViewModel("NeoForge", "neoforge", CreateLoaderSection("neoforge", "NeoForge", "neoforge")),
                new NavItemViewModel("Cleanroom", "cleanroom", CreateLoaderSection("cleanroom", "Cleanroom", "cleanroom")),
                new NavItemViewModel("Fabric", "fabric", CreateLoaderSection("fabric", "Fabric", "fabric")),
                new NavItemViewModel("Quilt", "quilt", CreateLoaderSection("quilt", "Quilt", "quilt")),
                new NavItemViewModel("Legacy Fabric", "legacyfabric", CreateLoaderSection("legacyfabric", "Legacy Fabric", "fabric")),
                new NavItemViewModel("OptiFine", "optifine", CreateLoaderSection("optifine", "OptiFine", "optifine")),
                new NavItemViewModel("LiteLoader", "liteloader", CreateLoaderSection("liteloader", "LiteLoader", "liteloader")),
                new NavItemViewModel("LabyMod", "labymod", CreateLoaderSection("labymod", "LabyMod", "labymod"))
            ])
        };

        SelectedSection = minecraft;
    }

    public ObservableCollection<NavGroupViewModel> Groups { get; }

    public InstallWizardViewModel Wizard { get; }

    public InstallTaskViewModel Install { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInstallSection))]
    private NavItemViewModel? _selectedSection;

    [ObservableProperty]
    private LoaderSectionViewModel? _activeSection;

    /// <summary>安装向导入口（首项）显示向导，其余分区显示加载器版本列表。</summary>
    public bool IsInstallSection => SelectedSection?.Section is null;

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
        () => LoadLoaderGroupsAsync(kind, iconKey));

    private async Task<IReadOnlyList<VersionGroupViewModel>> LoadClientGroupsAsync()
    {
        var versions = await _services.Installer
            .GetVersionListAsync(DownloadPolicy.FromConfig(_services.Config))
            .ConfigureAwait(true);
        return InstallWizardViewModel.BuildGroups(versions);
    }

    private async Task<IReadOnlyList<VersionGroupViewModel>> LoadLoaderGroupsAsync(string kind, string iconKey)
    {
        var policy = DownloadPolicy.FromConfig(_services.Config);
        var provider = _services.LoaderProviders.Get(Enum.Parse<LoaderKind>(kind, true));
        var mcVersions = await provider.GetSupportedMcVersionsAsync(policy).ConfigureAwait(true);
        return InstallWizardViewModel.BuildMcVersionGroups(mcVersions, iconKey);
    }

    partial void OnSelectedSectionChanged(NavItemViewModel? value)
    {
        foreach (var item in Groups.SelectMany(group => group.Items))
        {
            item.IsActive = ReferenceEquals(item, value);
        }

        if (value?.Section is LoaderSectionViewModel section)
        {
            ActiveSection = section;
            _ = section.RefreshAsync();
        }
        else
        {
            ActiveSection = null;
            _ = Wizard.LoadVersionListAsync();
        }

        OnPropertyChanged(nameof(IsStartButtonVisible));
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
