using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;

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
    private bool _showRetryButton;

    [ObservableProperty]
    private string _loadErrorText = "";

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
        ShowInstallButton = source.State is PluginInstallState.NotInstalled
            or PluginInstallState.UpdateAvailable
            or PluginInstallState.LoadFailed;
        ShowUninstallButton = source.State is PluginInstallState.Installed
            or PluginInstallState.UpdateAvailable
            or PluginInstallState.UpdatePendingRestart
            or PluginInstallState.LoadFailed;
        ShowRetryButton = source.State == PluginInstallState.LoadFailed;
        LoadErrorText = source.Installed?.LoadError ?? "";
        ButtonText = source.State switch
        {
            PluginInstallState.UpdateAvailable => "更新",
            PluginInstallState.LoadFailed => "重装",
            _ => "安装",
        };
        StateText = source.State switch
        {
            PluginInstallState.NotInstalled => "未安装",
            PluginInstallState.Installed => $"已安装 v{source.Installed?.Version}",
            PluginInstallState.UpdateAvailable => $"已安装 v{source.Installed?.Version} · 可更新至 v{source.Entry.Version}",
            PluginInstallState.UpdatePendingRestart => $"已更新至 v{source.Entry.Version} · 重启启动器后生效",
            PluginInstallState.LoadFailed => $"已安装 v{source.Installed?.Version} · 加载失败",
            _ => "",
        };
    }
}

/// <summary>
/// 设置页「插件工坊」：清单源选择 → 清单发现 → 下载/更新/卸载 → 启用/禁用（状态落盘）。
/// 启用的插件才会出现在工具页；工具页点击进入插件自己的功能/设置页。
/// </summary>
public partial class PluginWorkshopViewModel : ObservableObject
{
    private readonly PluginManager _plugins;
    private readonly LauncherConfig _config;
    private readonly ConfigStore _configStore;
    private readonly Action<string>? _toast;

    public PluginWorkshopViewModel(
        PluginManager plugins,
        LauncherConfig config,
        ConfigStore configStore,
        Action<string>? toast = null)
    {
        _plugins = plugins;
        _config = config;
        _configStore = configStore;
        _toast = toast;
        var customUrl = config.OnlineManifestUrl?.Trim() ?? "";
        _customSourceUrl = customUrl;
        _selectedSourceIndex = customUrl.Length == 0 ? 0 : 1;
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

    // ---------- 下载源 ----------

    /// <summary>0 = Hycl 官方仓库源；1 = 自定义源。</summary>
    [ObservableProperty]
    private int _selectedSourceIndex;

    [ObservableProperty]
    private string _customSourceUrl;

    public bool IsCustomSource => SelectedSourceIndex == 1;

    partial void OnSelectedSourceIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomSource));
        // 切回官方源时立即清空并落盘自定义地址，避免「看似官方源、实际仍走旧自定义源」
        if (value == 0 && !string.IsNullOrEmpty(_config.OnlineManifestUrl?.Trim()))
        {
            _ = ApplySourceUrlAsync("");
            CustomSourceUrl = "";
        }
    }

    /// <summary>应用文本框中的自定义源（校验 http(s) 格式；空等于回到官方源）。</summary>
    [RelayCommand]
    private Task ApplyCustomSourceAsync() => ApplySourceUrlAsync(CustomSourceUrl);

    private async Task ApplySourceUrlAsync(string? url)
    {
        var trimmed = url?.Trim() ?? "";
        var valid = trimmed.Length == 0
            || (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps));
        if (!valid)
        {
            _toast?.Invoke("自定义源地址无效：需要 http(s) 链接");
            return;
        }

        _config.OnlineManifestUrl = trimmed;
        // 换源后让下一次「刷新清单」立即走新地址，不被 5 分钟缓存挡住
        _plugins.Manifests.Invalidate();
        await _configStore.SaveAsync().ConfigureAwait(true);
        if (trimmed.Length == 0)
        {
            _toast?.Invoke("已恢复 Hycl 官方仓库源");
        }
        else
        {
            _toast?.Invoke("自定义源已保存；外部来源不受 Hycl 官方保证，请注意甄别");
        }

        await LoadManifestAsync().ConfigureAwait(true);
    }

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

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(PluginWorkshopRow? row, CancellationToken cancellationToken)
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
            await _plugins.InstallAsync(row.Id, progress, cancellationToken).ConfigureAwait(true);
            // 安装成功但加载失败时不能报「已完成」：工具页不会出现入口，必须把原因告诉用户。
            var loadError = _plugins.Installed.TryGetValue(row.Id, out var installed) ? installed.LoadError : null;
            _toast?.Invoke(loadError is null
                ? $"{row.Name} 安装完成，已默认启用"
                : $"{row.Name} 已安装但加载失败：{loadError}。可在插件工坊点「重试加载」或重启启动器");
        }
        catch (OperationCanceledException)
        {
            // 用户取消：断点临时文件已保留，短时间内再次点「安装」会续传
            _toast?.Invoke($"{row.Name} 已取消下载，进度已保留");
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

    /// <summary>重新加载已安装但加载失败的插件（不重新下载，修复运行环境后可直接恢复）。</summary>
    [RelayCommand]
    private async Task RetryLoadAsync(PluginWorkshopRow? row)
    {
        if (row is null || IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var ok = await _plugins.RetryLoadAsync(row.Id).ConfigureAwait(true);
            _toast?.Invoke(ok
                ? $"{row.Name} 加载成功，可在工具页打开"
                : $"{row.Name} 加载仍失败，详见启动器日志");
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"{row.Name} 重试加载失败：{ex.Message}");
        }
        finally
        {
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
            _toast?.Invoke($"{row.Name} 已启用（立即生效，可在工具页打开）");
        }
    }

    [RelayCommand]
    private void Disable(PluginWorkshopRow? row)
    {
        if (row is not null)
        {
            _plugins.SetEnabled(row.Id, false);
            _toast?.Invoke($"{row.Name} 已禁用（立即生效，后台任务已停止）");
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
