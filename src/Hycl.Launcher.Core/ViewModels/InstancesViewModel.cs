using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>版本管理：扫描、详情、设为当前、删除、打开目录。</summary>
public sealed partial class InstancesViewModel : ObservableObject
{
    private readonly LauncherServices _services;

    public InstancesViewModel(LauncherServices services)
    {
        _services = services;
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(InstanceCountText));
        };
    }

    public ObservableCollection<MinecraftInstance> Instances { get; } = new();

    [ObservableProperty]
    private MinecraftInstance? _selectedInstance;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isConfirmingDelete;

    [ObservableProperty]
    private string _statusMessage = "";

    public bool HasInstances => Instances.Count > 0;

    public bool IsEmpty => Instances.Count == 0;

    public string InstanceCountText => Instances.Count == 0 ? "暂无已安装版本" : $"共 {Instances.Count} 个版本";

    public string SelectedName => SelectedInstance?.Name ?? "未选择版本";

    public string SelectedDescription => SelectedInstance?.Description ?? "选择左侧版本查看详情";

    public string SelectedPath => SelectedInstance?.Directory ?? "";

    public string SelectedTypeLabel => SelectedInstance is null ? "" : InstanceDisplay.TypeLabel(SelectedInstance);

    public string SelectedLastWrite => SelectedInstance is null ? "" : InstanceDisplay.LastWriteText(SelectedInstance);

    public bool CanManage => SelectedInstance is not null;

    public bool IsSelectedCurrent => SelectedInstance is not null &&
                                     SelectedInstance.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase);

    public bool CanSetCurrent => !IsSelectedCurrent;

    public event EventHandler<string>? LaunchRequested;

    public event EventHandler<string>? SelectionChanged;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var instances = await _services.Instances.ScanAsync(_services.Config.EffectiveGameDirectory).ConfigureAwait(true);
            var previousId = SelectedInstance?.Id ?? _services.Config.SelectedVersion;
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(instance);
            }

            SelectedInstance = Instances.FirstOrDefault(x => x.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase))
                               ?? Instances.FirstOrDefault();
            IsRefreshing = false;
            StatusMessage = HasInstances ? "" : "未找到已安装版本，请先在下载页安装。";
        }
        catch (Exception ex)
        {
            IsRefreshing = false;
            StatusMessage = $"扫描失败：{ex.Message}";
            _services.Logger.Error("扫描实例失败", ex);
        }
    }

    [RelayCommand]
    private async Task SetCurrentAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _services.Config.SelectedVersion = SelectedInstance.Id;
        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        StatusMessage = $"已将 {SelectedInstance.Name} 设为当前版本";
        NotifySelection();
        SelectionChanged?.Invoke(this, SelectedInstance.Id);
    }

    [RelayCommand]
    private void LaunchSelected()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        LaunchRequested?.Invoke(this, SelectedInstance.Id);
    }

    [RelayCommand]
    private void RequestDelete()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        IsConfirmingDelete = true;
        StatusMessage = $"确认删除 {SelectedInstance.Name}？该操作不可撤销。";
    }

    [RelayCommand]
    private void CancelDelete()
    {
        IsConfirmingDelete = false;
        StatusMessage = "";
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        var target = SelectedInstance;
        var deleted = _services.Instances.Delete(_services.Config.EffectiveGameDirectory, target.Id);
        IsConfirmingDelete = false;
        if (!deleted)
        {
            StatusMessage = "删除失败，请检查文件是否被占用。";
            return;
        }

        StatusMessage = $"已删除 {target.Name}";
        if (_services.Config.SelectedVersion.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
        {
            _services.Config.SelectedVersion = "";
            await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        }

        await RefreshAsync().ConfigureAwait(true);
        SelectionChanged?.Invoke(this, "");
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _services.System.OpenFolder(SelectedInstance.Directory);
    }

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        IsConfirmingDelete = false;
        NotifySelection();
        StatusMessage = "";
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(SelectedTypeLabel));
        OnPropertyChanged(nameof(SelectedLastWrite));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(IsSelectedCurrent));
        OnPropertyChanged(nameof(CanSetCurrent));
    }
}
