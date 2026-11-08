using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 独立下载进度页：任务列表 + 每个任务的步骤、当前步骤、本步/总进度、文件数与当前文件。
/// 数据直接来自共享的 <see cref="InstallTaskViewModel"/>，与右下角悬浮图标、下载页任务条同源，
/// 不另建一套进度状态。
/// </summary>
public partial class DownloadProgressPageViewModel : ObservableObject, IPageLifecycle
{
    private readonly InstallTaskViewModel _install;
    private readonly Action _goBack;

    public DownloadProgressPageViewModel(InstallTaskViewModel install, Action goBack)
    {
        _install = install;
        _goBack = goBack;
        // 现有的安装/下载任务由同一个 InstallTaskViewModel 串行执行，列表结构可直接支持多任务。
        Tasks = new ObservableCollection<InstallTaskViewModel> { install };
        install.PropertyChanged += OnInstallPropertyChanged;
        UpdateSummary();
    }

    /// <summary>当前任务列表；任务的数据绑定到共享的安装状态。</summary>
    public ObservableCollection<InstallTaskViewModel> Tasks { get; }

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private bool _hasActiveTask;

    private void OnInstallPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(InstallTaskViewModel.IsInstalling)
            or nameof(InstallTaskViewModel.StageText)
            or nameof(InstallTaskViewModel.OverallProgressPercent))
        {
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        HasActiveTask = _install.IsInstalling;
        SummaryText = _install.IsInstalling
            ? $"{_install.InstallingName} · {_install.StageText} · 总进度 {_install.OverallProgressPercent:0}%"
            : "当前没有进行中的下载任务";
    }

    /// <summary>返回进入本页之前的页面（由主窗口负责恢复）。</summary>
    [RelayCommand]
    private void GoBack() => _goBack();

    public void Activate()
    {
    }

    public void Deactivate()
    {
    }
}
