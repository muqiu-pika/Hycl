using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 下载页 ViewModel：左侧分区导航 + 安装向导 + 底部安装条。
/// 界面结构来自 Hycl (2)，网络与安装动作全部调用当前 Core 的 VersionInstaller。
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
                new NavItemViewModel("OptiFine", "optifine", CreateUnsupportedSection("OptiFine")),
                new NavItemViewModel("Forge", "forge", CreateUnsupportedSection("Forge")),
                new NavItemViewModel("NeoForge", "neoforge", CreateUnsupportedSection("NeoForge")),
                new NavItemViewModel("Cleanroom", "cleanroom", CreateUnsupportedSection("Cleanroom")),
                new NavItemViewModel("Fabric", "fabric", CreateFabricSection()),
                new NavItemViewModel("Legacy Fabric", "legacyfabric", CreateUnsupportedSection("Legacy Fabric")),
                new NavItemViewModel("LabyMod", "labymod", CreateUnsupportedSection("LabyMod")),
                new NavItemViewModel("LiteLoader", "liteloader", CreateUnsupportedSection("LiteLoader"))
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

    private LoaderSectionViewModel CreateFabricSection() => new(
        "Fabric",
        "Fabric",
        "点击 Minecraft 版本可为该版本安装 Fabric 加载器（自动选择最新稳定版）。",
        LoadFabricGroupsAsync);

    private LoaderSectionViewModel CreateUnsupportedSection(string name) => new(
        name,
        name,
        $"该分区依赖 Hycl (2) 的下载实现，当前工程尚未迁移；现有 Core 仅支持原版与 Fabric。",
        () => Task.FromException<IReadOnlyList<VersionGroupViewModel>>(
            new NotSupportedException($"{name} 的下载实现尚未迁移，当前仅支持原版与 Fabric。")));

    private async Task<IReadOnlyList<VersionGroupViewModel>> LoadClientGroupsAsync()
    {
        var versions = await _services.Installer
            .GetVersionListAsync(DownloadPolicy.FromConfig(_services.Config))
            .ConfigureAwait(true);
        return InstallWizardViewModel.BuildGroups(versions);
    }

    private async Task<IReadOnlyList<VersionGroupViewModel>> LoadFabricGroupsAsync()
    {
        var versions = await _services.Installer
            .GetVersionListAsync(DownloadPolicy.FromConfig(_services.Config))
            .ConfigureAwait(true);
        var groups = InstallWizardViewModel.BuildGroups(versions);
        return groups
            .Select(group => new VersionGroupViewModel(
                group.Title,
                group.Items.Select(item => new VersionItemViewModel(
                    item.Title,
                    item.Info,
                    "fabric",
                    item.Tags,
                    item.Payload,
                    item.IsSelected)).ToList(),
                group.IsExpanded))
            .ToList();
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
            await ActiveSection.RefreshAsync(force: true).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task ActivateSectionItemAsync(VersionItemViewModel? item)
    {
        if (item?.Payload is not VersionSummary summary || Install.IsInstalling)
        {
            return;
        }

        if (ActiveSection?.Kind == "Fabric")
        {
            await Install.RunFabricAsync(summary.Id, $"{summary.Id} · Fabric").ConfigureAwait(true);
            return;
        }

        await Install.RunVanillaAsync(summary, summary.Id).ConfigureAwait(true);
    }

    [RelayCommand]
    private void CancelInstall() => Install.Cancel();

    private void OnInstallCompleted(object? sender, string installedId)
    {
        Installed?.Invoke(this, installedId);
    }
}
