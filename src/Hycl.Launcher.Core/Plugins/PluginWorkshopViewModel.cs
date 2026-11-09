using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Hycl.Launcher.Core.Plugins;

/// <summary>插件工坊列表行：清单条目 + 安装态 + 启用态（对应 KAMUCL 设置页的插件列表）。</summary>
public partial class PluginWorkshopRow : ObservableObject
{
    public PluginWorkshopRow(PluginCatalogItem source, bool enabled)
    {
        Source = source;
        Apply(source, enabled);
    }

    public PluginCatalogItem Source { get; private set; }

    public string Id => Source.Id;

    public string Name => Source.DisplayName;

    public string Description => Source.Entry.Description;

    [ObservableProperty]
    private string _stateText = "";

    [ObservableProperty]
    private string _buttonText = "安装";

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _isInstalled;

    [ObservableProperty]
    private bool _showInstallButton;

    [ObservableProperty]
    private bool _showUninstallButton;

    [ObservableProperty]
    private bool _restartRequired;

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private double _percent;

    public void Apply(PluginCatalogItem source, bool enabled)
    {
        Source = source;
        IsEnabled = enabled;
        IsInstalled = source.Installed is not null;
        RestartRequired = source.RestartRequired;
        ShowInstallButton = source.State is PluginInstallState.NotInstalled or PluginInstallState.UpdateAvailable;
        ShowUninstallButton = source.State is PluginInstallState.Installed
            or PluginInstallState.UpdateAvailable
            or PluginInstallState.UpdatePendingRestart;
        ButtonText = source.State == PluginInstallState.UpdateAvailable ? "更新" : "安装";
        StateText = source.State switch
        {
            PluginInstallState.NotInstalled => "未安装",
            PluginInstallState.Installed => $"已安装 v{source.Installed?.Version}",
            PluginInstallState.UpdateAvailable => $"已安装 v{source.Installed?.Version} · 可更新至 v{source.Entry.Version}",
            PluginInstallState.UpdatePendingRestart => $"已更新至 v{source.Entry.Version} · 重启启动器后生效",
            _ => "",
        };
    }
}

/// <summary>
/// 设置页「插件工坊」：清单发现 → 下载/更新/卸载 → 启用/禁用（状态落盘）。
/// 启用的插件才会出现在工具页；工具页点击进入插件自己的功能/设置页。
/// </summary>
public partial class PluginWorkshopViewModel : ObservableObject
{
    private readonly PluginManager _plugins;
    private readonly Action<string>? _toast;

    public PluginWorkshopViewModel(PluginManager plugins, Action<string>? toast = null)
    {
        _plugins = plugins;
        _toast = toast;
        _plugins.PluginsChanged += (_, _) => Rebuild();
        Rebuild();
    }

    public ObservableCollection<PluginWorkshopRow> Rows { get; } = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isManifestOffline;

    [ObservableProperty]
    private string _manifestStatusText = "";

    /// <summary>进入设置页时由 SettingsPageViewModel 调用（缓存 TTL 内直接复用清单）。</summary>
    public async Task LoadManifestAsync()
    {
        IsLoading = true;
        try
        {
            var manifest = await _plugins.RefreshManifestAsync().ConfigureAwait(true);
            IsManifestOffline = manifest is null;
            ManifestStatusText = manifest is null
                ? "插件清单源不可达（可离线管理已安装插件，或在上方检查清单源地址）"
                : $"清单更新时间：{manifest.UpdatedAt}";
            Rebuild();
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task Refresh() => LoadManifestAsync();

    [RelayCommand]
    private async Task InstallAsync(PluginWorkshopRow? row)
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
            var progress = new Progress<double>(value => row.Percent = Math.Clamp(value * 100, 0, 100));
            await _plugins.InstallAsync(row.Id, progress).ConfigureAwait(true);
            _toast?.Invoke($"{row.Name} 安装完成，已默认启用");
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
            Rebuild();
        }
    }

    [RelayCommand]
    private async Task UninstallAsync(PluginWorkshopRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await _plugins.UninstallAsync(row.Id).ConfigureAwait(true);
            _toast?.Invoke($"{row.Name} 已卸载");
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"{row.Name} 卸载失败：{ex.Message}");
        }
        finally
        {
            IsBusy = false;
            Rebuild();
        }
    }

    [RelayCommand]
    private void Enable(PluginWorkshopRow? row)
    {
        if (row is not null)
        {
            _plugins.SetEnabled(row.Id, true);
            _toast?.Invoke($"{row.Name} 已启用，可在工具页打开");
        }
    }

    [RelayCommand]
    private void Disable(PluginWorkshopRow? row)
    {
        if (row is not null)
        {
            _plugins.SetEnabled(row.Id, false);
            _toast?.Invoke($"{row.Name} 已禁用，工具页入口已隐藏");
        }
    }

    /// <summary>复用行对象，避免安装进度/绑定在清单刷新时被重置。</summary>
    private void Rebuild()
    {
        var previous = Rows.ToDictionary(row => row.Id, row => row, StringComparer.Ordinal);
        Rows.Clear();
        foreach (var item in _plugins.BuildCatalog())
        {
            var enabled = _plugins.IsEnabled(item.Id);
            if (previous.TryGetValue(item.Id, out var row))
            {
                row.Apply(item, enabled);
                Rows.Add(row);
            }
            else
            {
                Rows.Add(new PluginWorkshopRow(item, enabled));
            }
        }
    }
}
