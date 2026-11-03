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
        new("设置", "settings"),
        new("工具", "wrench"),
    ];

    public LaunchPageViewModel Launch { get; }

    public DownloadPageViewModel Download { get; }

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

    public MainViewModel(LauncherServices services)
    {
        Launch = new LaunchPageViewModel(services, ShowToast);
        Download = new DownloadPageViewModel(services, ShowToast);
        Settings = new SettingsPageViewModel(services, ShowToast);
        Tools = new ToolsPageViewModel(ShowToast);
        _currentPage = Launch;

        Download.Installed += (_, instanceId) => _ = Launch.RefreshInstancesAsync(instanceId);
        Launch.CloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Launch.NavigateRequested += (_, key) =>
        {
            if (key == "download")
            {
                SelectedTabIndex = 1;
            }
        };

        _ = Launch.RefreshInstancesAsync();
    }

    partial void OnSelectedTabIndexChanged(int value)
    {
        if (value == 2)
        {
            _ = Settings.EnsureLoadedAsync();
        }

        if (value == 0)
        {
            _ = Launch.RefreshInstancesAsync();
        }

        CurrentPage = value switch
        {
            1 => Download,
            2 => Settings,
            3 => Tools,
            _ => Launch,
        };
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
