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
    /// <summary>首页「快速联机」入口对应的插件 id。</summary>
    private const string OnlinePluginId = "hycl.online";

    private readonly LauncherServices _services;
    private readonly Task _pluginsReady;

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

    /// <summary>独立「启动中」页面（与启动页共用同一个启动会话状态）。</summary>
    public LaunchProgressViewModel LaunchProgress { get; }

    public DownloadPageViewModel Download { get; }

    /// <summary>独立下载进度页：右下角悬浮图标点入，可返回到进入前的页面。</summary>
    public DownloadProgressPageViewModel DownloadProgress { get; }

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

    /// <summary>独立下载进度页是否打开（打开期间右下角悬浮图标隐藏）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFloatingDownload))]
    [NotifyPropertyChangedFor(nameof(FloatingDownloadIndeterminate))]
    private bool _isDownloadProgressOpen;

    /// <summary>进入下载进度页前所在的页面，返回时原样恢复。</summary>
    private object? _pageBeforeDownloadProgress;

    public event EventHandler? CloseRequested;

    public event EventHandler? MinimizeRequested;

    public MainViewModel(LauncherServices services)
    {
        _services = services;
        _pluginsReady = Task.CompletedTask;
        Launch = new LaunchPageViewModel(services, ShowToast);
        PerfTrace.Mark("launch_vm_ctor_done");
        LaunchProgress = new LaunchProgressViewModel(Launch);
        Download = new DownloadPageViewModel(services, ShowToast);
        PerfTrace.Mark("download_vm_ctor_done");
        DownloadProgress = new DownloadProgressPageViewModel(Download.Install, CloseDownloadProgress);
        Instances = new InstancesViewModel(services, Download.Install);
        Instances.DownloadResourceRequested = (type, gameVersion, loaderLabel, instanceId) =>
        {
            SelectedTabIndex = 1;
            Download.OpenCompSection(type, gameVersion, loaderLabel, instanceId);
        };
        PerfTrace.Mark("instances_vm_ctor_done");
        Settings = new SettingsPageViewModel(services, ShowToast);
        PerfTrace.Mark("settings_vm_ctor_done");
        Tools = new ToolsPageViewModel(services, ShowToast);
        PerfTrace.Mark("tools_vm_ctor_done");

        // 插件加载（本地 IO + 反射，个别失败不影响其余）：保存 Task，
        // 首页联机入口跳转前会等待它完成，避免启动瞬间点击误判为「未安装」
        _pluginsReady = _services.Plugins.LoadInstalledAsync();
        _currentPage = Launch;

        // 启动开始/成功返回时在启动页与独立启动中页面之间切换（仍走 PageHost 的串行淡入淡出）
        Launch.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LaunchPageViewModel.IsLaunchProgressVisible)
                && SelectedTabIndex == 0
                && !IsDownloadProgressOpen)
            {
                CurrentPage = Launch.IsLaunchProgressVisible ? LaunchProgress : Launch;
            }
        };

        Download.Installed += (_, instanceId) => _ = Launch.RefreshInstancesAsync(instanceId);
        // 下载页底部任务条整块可点击，进入与悬浮图标相同的独立进度页
        Download.OpenProgressRequested += (_, _) => OpenDownloadProgress();
        // 任务成功后：若用户正停在下载进度页且没有其它任务在跑，自动返回进入前的页面；
        // 不在进度页时保持当前页面，只由安装任务统一弹出底部的「下载完成」提示。
        Download.Install.Completed += (_, _) =>
        {
            if (!IsDownloadProgressOpen)
            {
                return;
            }

            // 还有别的任务在跑时不自动返回，进度页与悬浮图标继续展示剩余任务
            var othersRunning = DownloadProgress.Tasks.Any(
                task => task.IsInstalling && task.StageText != "安装完成");
            if (!othersRunning)
            {
                CloseDownloadProgress();
            }
        };
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
        Launch.NavigateRequested += (_, key) => _ = HandleNavigateAsync(key);

        // 右下角悬浮下载按钮：安装/下载任务进行中且不在下载页时显示（进度实时跟随 Install）
        Download.Install.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(InstallTaskViewModel.IsInstalling)
                or nameof(InstallTaskViewModel.ProgressPercent)
                or nameof(InstallTaskViewModel.OverallProgressPercent)
                or nameof(InstallTaskViewModel.InstallingName)
                or nameof(InstallTaskViewModel.StageText)
                or nameof(InstallTaskViewModel.StepCounterText))
            {
                SyncFloatingDownloadState();
            }
        };

        SyncFloatingDownloadState();

        _ = Launch.RefreshInstancesAsync();
        // 启动时补全账号皮肤缓存：SkinPath 已持久化则直接用缓存，缺失才重新拉取
        _ = Launch.RefreshSkinsAsync();
        PerfTrace.Mark("main_vm_ctor_done");
    }

    /// <summary>是否显示右下角悬浮下载按钮：有任务在进行、不在下载页且未打开独立进度页。</summary>
    public bool ShowFloatingDownload =>
        Download.Install.IsInstalling && SelectedTabIndex != 1 && !IsDownloadProgressOpen;

    /// <summary>悬浮下载按钮的悬停提示：阶段 + 任务名 + 总进度。</summary>
    public string FloatingDownloadTip
    {
        get
        {
            var install = Download.Install;
            return $"{install.StageText}：{install.InstallingName} · 总进度 {install.OverallProgressPercent:0}%";
        }
    }

    /// <summary>悬浮图标上显示的百分比：与下载页/进度页同一份总进度。</summary>
    public string FloatingDownloadPercentText => $"{Download.Install.OverallProgressPercent:0}%";

    /// <summary>刚入队、尚无进度时显示旋转环，避免图标停在 0% 看起来卡死。</summary>
    public bool FloatingDownloadIndeterminate => Download.Install.IsProgressIndeterminate;

    /// <summary>从右下角悬浮图标进入独立下载进度页；记录来源页面以便返回。</summary>
    public void OpenDownloadProgress()
    {
        if (IsDownloadProgressOpen)
        {
            return;
        }

        _pageBeforeDownloadProgress = CurrentPage ?? Launch;
        IsDownloadProgressOpen = true;
        CurrentPage = DownloadProgress;
        SyncFloatingDownloadState();
    }

    /// <summary>返回进入下载进度页之前的页面（不是首页，也不是停在下载页）。</summary>
    public void CloseDownloadProgress()
    {
        if (!IsDownloadProgressOpen)
        {
            return;
        }

        IsDownloadProgressOpen = false;
        var page = _pageBeforeDownloadProgress ?? Launch;
        _pageBeforeDownloadProgress = null;
        CurrentPage = page;
        (page as IPageLifecycle)?.Activate();
        SyncFloatingDownloadState();
    }

    /// <summary>同步悬浮按钮相关的派生属性与启动页按钮间距。</summary>
    private void SyncFloatingDownloadState()
    {
        Launch.IsFloatingDownloadVisible = ShowFloatingDownload;
        OnPropertyChanged(nameof(ShowFloatingDownload));
        OnPropertyChanged(nameof(FloatingDownloadTip));
        OnPropertyChanged(nameof(FloatingDownloadPercentText));
        OnPropertyChanged(nameof(FloatingDownloadIndeterminate));
    }

    private async Task HandleNavigateAsync(string key)
    {
        try
        {
            switch (key)
            {
                case "download":
                    SelectedTabIndex = 1;
                    return;
                case "instances":
                    SelectedTabIndex = 2;
                    return;
                case "online":
                    await OpenOnlineFeatureAsync().ConfigureAwait(true);
                    return;
            }
        }
        catch (Exception ex)
        {
            // 事件回调是 fire-and-forget：异常只落日志，不让启动器崩溃
            _services.Logger.Warn("[导航] 页面跳转失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 首页「快速联机」：陶瓦联机插件已安装且启用 → 直接打开它的功能设置页；
    /// 未安装 / 未启用 / 加载失败 → 打开设置页插件工坊，让用户安装或启用。
    /// </summary>
    private async Task OpenOnlineFeatureAsync()
    {
        try
        {
            await _pluginsReady.ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _services.Logger.Warn("[插件] 等待插件加载失败：" + ex.Message);
        }

        // EnabledPlugins 只含「已加载 + 已启用」的插件，天然满足安装且启用的判定
        var entry = _services.Plugins.EnabledPlugins
            .FirstOrDefault(item => string.Equals(item.Id, OnlinePluginId, StringComparison.Ordinal));
        if (entry is not null)
        {
            Tools.OpenPluginEntry(entry);
            SelectedTabIndex = 4;
            return;
        }

        Settings.RequestSection(SettingsPageViewModel.PluginWorkshopSectionTitle);
        SelectedTabIndex = 3;
        ShowToast("尚未安装或启用陶瓦联机插件，已打开插件工坊");
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        var switchStart = PerfTrace.Now;
        // 从独立下载进度页切换标签：直接回到目标标签页，不再恢复旧页面
        if (IsDownloadProgressOpen)
        {
            IsDownloadProgressOpen = false;
            _pageBeforeDownloadProgress = null;
        }

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
            // 启动中页面是“启动”标签的一种状态，切回来时按当前会话状态决定显示哪一个
            _ => Launch.IsLaunchProgressVisible ? LaunchProgress : Launch,
        };

        (CurrentPage as IPageLifecycle)?.Activate();
        SyncFloatingDownloadState();
        PerfTrace.MarkSince($"tab_ready:{value}", switchStart);
    }

    /// <summary>窗口关闭：取消所有页面级在途任务并释放取消令牌；插件退出清理（限时）。</summary>
    public void Shutdown()
    {
        if (CurrentPage is IPageLifecycle lifecycle)
        {
            lifecycle.Deactivate();
        }

        Launch.CancelPending();
        Download.Shutdown();
        _services.Plugins.ShutdownAll(TimeSpan.FromSeconds(3));
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
