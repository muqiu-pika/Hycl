using CommunityToolkit.Mvvm.ComponentModel;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>
/// 主窗口 ViewModel：维护顶部标签页、当前页面与轻提示。
/// 页面结构与 Hycl (2) 保持一致（启动 / 下载 / 设置 / 工具）。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private int _toastVersion;

    public IReadOnlyList<TabItemViewModel> Tabs { get; } =
    [
        new("启动", "play"),
        new("下载", "download"),
        new("版本", "list"),
        new("设置", "settings"),
        new("工具", "wrench"),
    ];

    public LaunchPageViewModel Launch { get; }

    public DownloadPageViewModel Download { get; }

    public InstancesViewModel Instances { get; }

    public SettingsPageViewModel Settings { get; }

    public ToolsPageViewModel Tools { get; }

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string? _toastMessage;

    [ObservableProperty]
    private bool _isToastVisible;

    public event EventHandler? CloseRequested;

    public event EventHandler? MinimizeRequested;

    public MainViewModel(LauncherServices services)
    {
        Launch = new LaunchPageViewModel(services, ShowToast);
        Download = new DownloadPageViewModel(services, ShowToast);
        Instances = new InstancesViewModel(services);
        Settings = new SettingsPageViewModel(services, ShowToast);
        Tools = new ToolsPageViewModel(ShowToast);
        _currentPage = Launch;

        Download.Installed += (_, instanceId) => _ = Launch.RefreshInstancesAsync(instanceId);
        Instances.SelectionChanged += (_, instanceId) =>
        {
            if (!string.IsNullOrWhiteSpace(instanceId))
            {
                _ = Launch.SelectInstanceAsync(instanceId);
            }
            else
            {
                // 实例被删除：同步刷新启动页，避免“切换版本”里仍停留着已删除的实例
                _ = Launch.RefreshInstancesAsync();
            }
        };
        Instances.LaunchRequested += async (_, instanceId) =>
        {
            await Launch.SelectInstanceAsync(instanceId);
            SelectedTabIndex = 0;
            Launch.LaunchCommand.Execute(null);
        };
        Launch.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Launch.LauncherVisibilityChanged += (_, kind) =>
        {
            if (kind == 0)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (kind == 4)
            {
                MinimizeRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        Launch.NavigateRequested += (_, key) =>
        {
            if (key == "download")
            {
                SelectedTabIndex = 1;
            }
            else if (key == "instances")
            {
                SelectedTabIndex = 2;
            }
        };

        // 右下角悬浮下载按钮：安装/下载任务进行中且不在下载页时显示（进度实时跟随 Install）
        Download.Install.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstallTaskViewModel.IsInstalling)
                or nameof(InstallTaskViewModel.ProgressPercent)
                or nameof(InstallTaskViewModel.InstallingName)
                or nameof(InstallTaskViewModel.StageText))
            {
                OnPropertyChanged(nameof(ShowFloatingDownload));
                OnPropertyChanged(nameof(FloatingDownloadTip));
            }
        };

        _ = Launch.RefreshInstancesAsync();
    }

    /// <summary>是否显示右下角悬浮下载按钮：有任务在进行且当前不在下载页。</summary>
    public bool ShowFloatingDownload => Download.Install.IsInstalling && SelectedTabIndex != 1;

    /// <summary>悬浮下载按钮的悬停提示：阶段 + 任务名 + 百分比。</summary>
    public string FloatingDownloadTip
    {
        get
        {
            var install = Download.Install;
            return $"{install.StageText}：{install.InstallingName} · {install.ProgressPercent:0}%";
        }
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        // 离开当前页：取消该页在途的加载/下载，避免后台任务继续跑
        if (_currentPage is IPageLifecycle leaving)
        {
            leaving.Deactivate();
        }

        if (value == 3)
        {
            _ = Settings.EnsureLoadedAsync();
        }

        if (value == 0)
        {
            _ = Launch.RefreshInstancesAsync();
        }

        if (value == 2)
        {
            _ = Instances.RefreshAsync();
        }

        CurrentPage = value switch
        {
            1 => Download,
            2 => Instances,
            3 => Settings,
            4 => Tools,
            _ => Launch,
        };

        (CurrentPage as IPageLifecycle)?.Activate();
        OnPropertyChanged(nameof(ShowFloatingDownload));
    }

    /// <summary>窗口关闭：取消所有页面级在途任务并释放取消令牌。</summary>
    public void Shutdown()
    {
        if (CurrentPage is IPageLifecycle lifecycle)
        {
            lifecycle.Deactivate();
        }

        Launch.CancelPending();
        Download.Shutdown();
    }

    /// <summary>显示轻提示（1.6 秒后自动消失）。</summary>
    public void ShowToast(string message)
    {
        ToastMessage = message;
        IsToastVisible = true;
        var version = ++_toastVersion;
        _ = HideToastAsync(version);
    }

    private async Task HideToastAsync(int version)
    {
        await Task.Delay(1600);
        if (version == _toastVersion)
        {
            IsToastVisible = false;
        }
    }
}

/// <summary>顶部标签页条目。</summary>
public sealed record TabItemViewModel(string Label, string IconKey);

/// <summary>页面级生命周期：切换标签页/关闭窗口时取消该页在途的加载与下载。</summary>
public interface IPageLifecycle
{
    /// <summary>进入页面：恢复事件订阅。</summary>
    void Activate();

    /// <summary>离开页面：取消在途请求并退订临时事件。</summary>
    void Deactivate();
}
