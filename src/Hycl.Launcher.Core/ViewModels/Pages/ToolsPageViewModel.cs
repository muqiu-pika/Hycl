using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Plugins;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>插件中心列表行：清单条目 + 本地安装态的 UI 包装（带安装进度）。</summary>
public partial class PluginListRowViewModel : ObservableObject
{
    public PluginListRowViewModel(PluginCatalogItem source)
    {
        Source = source;
        Apply(source);
    }

    public PluginCatalogItem Source { get; }

    public string Id => Source.Id;

    public string Name => Source.DisplayName;

    public string Description => Source.Entry.Description;

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private string _buttonText = "";

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _percent;

    /// <summary>主按钮语义：安装 / 更新 / 重启生效（重启后变为已装）。</summary>
    [ObservableProperty]
    private bool _showInstallButton;

    [ObservableProperty]
    private bool _showUninstallButton;

    [ObservableProperty]
    private bool _restartRequired;

    public void Apply(PluginCatalogItem source)
    {
        StateText = source.State switch
        {
            PluginInstallState.NotInstalled => "未安装",
            PluginInstallState.Installed => $"已安装 v{source.Installed?.Version}",
            PluginInstallState.UpdateAvailable => $"已安装 v{source.Installed?.Version} · 可更新至 v{source.Entry.Version}",
            PluginInstallState.UpdatePendingRestart => $"已更新至 v{source.Entry.Version} · 重启启动器后生效",
            _ => "",
        };
        RestartRequired = source.RestartRequired;
        ShowInstallButton = source.State is PluginInstallState.NotInstalled or PluginInstallState.UpdateAvailable;
        ButtonText = source.State == PluginInstallState.UpdateAvailable ? "更新" : "安装";
        ShowUninstallButton = source.State is PluginInstallState.Installed or PluginInstallState.UpdateAvailable or PluginInstallState.UpdatePendingRestart;
    }
}

/// <summary>
/// 工具页 ViewModel：插件中心（清单发现 → 安装/更新/卸载）+ 已加载插件的动态工具卡。
/// </summary>
public partial class ToolsPageViewModel : ObservableObject, IPageLifecycle
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private CancellationTokenSource? _lifecycleCts;

    public ToolsPageViewModel(LauncherServices services, Action<string>? toast = null)
    {
        _services = services;
        _toast = toast;
        _services.Plugins.PluginsChanged += (_, _) => RebuildCatalog();
        RebuildCatalog();
    }

    /// <summary>已加载插件提供的动态工具卡（对象即 Avalonia 控件，ItemsControl 直接渲染）。</summary>
    public ObservableCollection<object> PluginCards => _services.Plugins.ToolCards;

    public ObservableCollection<PluginListRowViewModel> Catalog { get; } = [];

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isManifestOffline;

    [ObservableProperty]
    private string _manifestStatusText = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>进入工具页：刷新清单（缓存 TTL 内直接复用）。</summary>
    public void Activate()
    {
        _lifecycleCts = new CancellationTokenSource();
        _ = RefreshAsync();
    }

    public void Deactivate()
    {
        _lifecycleCts?.Cancel();
        _lifecycleCts?.Dispose();
        _lifecycleCts = null;
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var manifest = await _services.Plugins.RefreshManifestAsync().ConfigureAwait(true);
            IsManifestOffline = manifest is null;
            ManifestStatusText = manifest is null
                ? "插件清单源不可达（可离线管理已安装插件，或在设置 → 联机与局域网检查清单源）"
                : $"清单更新时间：{manifest.UpdatedAt}";
            RebuildCatalog();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task InstallAsync(PluginListRowViewModel? row)
    {
        if (row is null || IsBusy || row.IsInstalling)
        {
            return;
        }

        IsBusy = true;
        row.IsInstalling = true;
        row.Percent = 0;
        try
        {
            var progress = new Progress<double>(value =>
            {
                row.Percent = Math.Clamp(value * 100, 0, 100);
            });
            await _services.Plugins.InstallAsync(row.Id, progress).ConfigureAwait(true);
            _toast?.Invoke($"{row.Name} 安装完成");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"{row.Name} 安装失败：{ex.Message}");
        }
        finally
        {
            row.IsInstalling = false;
            IsBusy = false;
            RebuildCatalog();
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(PluginListRowViewModel? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _services.Plugins.UninstallAsync(row.Id).ConfigureAwait(true);
            _toast?.Invoke($"{row.Name} 已卸载");
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"{row.Name} 卸载失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            RebuildCatalog();
        }
    }

    private void RebuildCatalog()
    {
        var previous = Catalog.ToDictionary(row => row.Id, row => row, StringComparer.Ordinal);
        Catalog.Clear();
        foreach (var item in _services.Plugins.BuildCatalog())
        {
            if (previous.TryGetValue(item.Id, out var row))
            {
                row.Apply(item);
                Catalog.Add(row);
            }
            else
            {
                Catalog.Add(new PluginListRowViewModel(item));
            }
        }
    }
}
