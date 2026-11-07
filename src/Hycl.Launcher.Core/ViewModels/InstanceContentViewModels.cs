using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>实例管理页左侧导航分区（对应 PCL-CE PageInstanceLeft 的条目）。</summary>
public enum InstanceSection
{
    Overview,
    Setup,
    Modify,
    Export,
    Saves,
    Screenshots,
    Mods,
    ResourcePacks,
    ShaderPacks,
    Schematics,
    Servers
}

/// <summary>内容列表里的一行（模组 / 资源包 / 光影 / 投影 / 截图）。</summary>
public sealed class LocalContentItemViewModel
{
    public LocalContentItemViewModel(LocalContentFile file)
    {
        File = file;
    }

    public LocalContentFile File { get; }

    public string Name => File.Name;

    public string Path => File.Path;

    public string Info => $"{InstanceContentService.FormatSize(File.Size)} · {File.Modified:yyyy-MM-dd HH:mm}";

    public string? PreviewUrl => File.IsDirectory ? null : Path;

    public bool IsDirectory => File.IsDirectory;

    public bool IsImage => !IsDirectory &&
                           (Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled => File.IsEnabled;

    public string StateText => IsEnabled ? "" : "已禁用";
}

/// <summary>通用本地内容列表：模组（支持启用/禁用）、资源包、光影包、投影原理图、截图。</summary>
public sealed partial class InstanceFilesViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private string _gameDirectory = "";
    private string _directory = "";
    private string[] _extensions = [];
    private bool _includeDirectories;
    private bool _supportsDisable;

    public InstanceFilesViewModel(LauncherServices services)
    {
        _services = services;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(CountText));
        };
    }

    public ObservableCollection<LocalContentItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private string _emptyText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showToggle;

    public string Directory => _directory;

    public bool HasItems => Items.Count > 0;

    public string CountText => Items.Count == 0 ? "" : $"共 {Items.Count} 项";

    public void Configure(
        string gameDirectory,
        string directory,
        string emptyText,
        string[] extensions,
        bool includeDirectories,
        bool supportsDisable)
    {
        _gameDirectory = gameDirectory;
        _directory = directory;
        EmptyText = emptyText;
        _extensions = extensions;
        _includeDirectories = includeDirectories;
        _supportsDisable = supportsDisable;
        ShowToggle = supportsDisable;
        StatusMessage = "";
        OnPropertyChanged(nameof(Directory));
    }

    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_directory))
        {
            Items.Clear();
            return;
        }

        IsLoading = true;
        try
        {
            var files = await Task.Run(
                () => InstanceContentService.Scan(_directory, _extensions, _includeDirectories)).ConfigureAwait(true);
            Items.Clear();
            foreach (var file in files)
            {
                Items.Add(new LocalContentItemViewModel(file));
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_directory.Length == 0)
        {
            return;
        }

        _services.System.OpenFolder(_directory);
    }

    [RelayCommand]
    private void OpenItem(LocalContentItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (item.IsDirectory)
        {
            _services.System.OpenFolder(item.Path);
        }
        else
        {
            _services.System.OpenUrl(item.Path);
        }
    }

    [RelayCommand]
    private async Task ToggleAsync(LocalContentItemViewModel? item)
    {
        if (item is null || item.IsDirectory)
        {
            return;
        }

        var (success, message) = InstanceContentService.ToggleEnabled(item.Path);
        StatusMessage = message;
        if (success)
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(LocalContentItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var success = InstanceContentService.Delete(item.Path, _gameDirectory, _services.Logger);
        StatusMessage = success ? $"已删除：{item.Name}（可在回收站恢复）" : $"删除失败：{item.Name}";
        if (success)
        {
            await LoadAsync();
        }
    }
}

/// <summary>导出实例为 zip：勾选内容 → 选择保存位置 → 逐文件写包。</summary>
public sealed partial class InstanceExportViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string> _notify;
    private MinecraftInstance? _instance;
    private string _contentRoot = "";

    public InstanceExportViewModel(LauncherServices services, Action<string> notify)
    {
        _services = services;
        _notify = notify;
    }

    /// <summary>由 UI 层注入的保存文件选择器（取消返回 null）。</summary>
    public Func<string?, Task<string?>>? SaveFilePicker { get; set; }

    [ObservableProperty]
    private string _exportName = "";

    [ObservableProperty]
    private bool _includeMods = true;

    [ObservableProperty]
    private bool _includeResourcePacks = true;

    [ObservableProperty]
    private bool _includeShaderPacks = true;

    [ObservableProperty]
    private bool _includeSaves = true;

    [ObservableProperty]
    private bool _includeScreenshots = false;

    [ObservableProperty]
    private bool _includeSchematics = true;

    [ObservableProperty]
    private bool _includeServers = true;

    [ObservableProperty]
    private bool _includeConfig = true;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _statusMessage = "";

    public string SuggestedFileName => $"{ExportName}.zip";

    public void Configure(MinecraftInstance? instance, string contentRoot)
    {
        _instance = instance;
        _contentRoot = contentRoot;
        ExportName = instance is null ? "" : $"{instance.Id}-导出";
        StatusMessage = "";
        Progress = 0;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_instance is null || IsExporting)
        {
            return;
        }

        var name = ExportName.Trim();
        if (name.Length == 0 || InstanceNameValidator.ValidateName(name) is not null)
        {
            StatusMessage = "导出名称包含非法字符，请修改后重试。";
            _notify(StatusMessage);
            return;
        }

        var picker = SaveFilePicker;
        if (picker is null)
        {
            StatusMessage = "当前平台不支持选择保存位置。";
            return;
        }

        var target = await picker(SuggestedFileName);
        if (target is null)
        {
            return;
        }

        if (!target.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            target += ".zip";
        }

        IsExporting = true;
        Progress = 0;
        StatusMessage = "正在导出…";
        try
        {
            var options = new InstanceExportOptions(
                IncludeMods,
                IncludeResourcePacks,
                IncludeShaderPacks,
                IncludeSaves,
                IncludeScreenshots,
                IncludeSchematics,
                IncludeServers,
                IncludeConfig);
            var report = new Progress<double>(value => Progress = value);
            await InstanceContentService.ExportAsync(
                _instance.Directory,
                _contentRoot,
                target,
                options,
                report,
                CancellationToken.None).ConfigureAwait(true);
            Progress = 100;
            StatusMessage = $"已导出：{target}";
            _notify($"实例已导出：{Path.GetFileName(target)}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            _services.Logger.Error("导出实例失败", ex);
            _notify(StatusMessage);
        }
        finally
        {
            IsExporting = false;
        }
    }
}

/// <summary>服务器页：读写实例的 servers.dat（名称 / 地址 / 图标）。</summary>
public sealed partial class InstanceServersViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string> _notify;
    private string _contentRoot = "";
    private string _path = "";

    public InstanceServersViewModel(LauncherServices services, Action<string> notify)
    {
        _services = services;
        _notify = notify;
        Servers.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasServers));
            OnPropertyChanged(nameof(CountText));
        };
    }

    public ObservableCollection<GameServerEntry> Servers { get; } = new();

    [ObservableProperty]
    private string _newServerName = "";

    [ObservableProperty]
    private string _newServerIp = "";

    [ObservableProperty]
    private string _statusMessage = "";

    public string ServerPath => _path;

    public bool HasServers => Servers.Count > 0;

    public string CountText => Servers.Count == 0 ? "" : $"共 {Servers.Count} 个服务器";

    public void Configure(string contentRoot)
    {
        _contentRoot = contentRoot;
        _path = Path.Combine(contentRoot, "servers.dat");
        NewServerName = "";
        NewServerIp = "";
        StatusMessage = "";
        OnPropertyChanged(nameof(ServerPath));
        Refresh();
    }

    [RelayCommand]
    private void Refresh()
    {
        Servers.Clear();
        if (_path.Length == 0)
        {
            return;
        }

        foreach (var server in ServersDatService.Read(_path))
        {
            Servers.Add(server);
        }
    }

    [RelayCommand]
    private void AddServer()
    {
        var ip = NewServerIp.Trim();
        if (ip.Length == 0)
        {
            StatusMessage = "请填写服务器地址，例如 play.example.com:25565";
            return;
        }

        if (Servers.Any(server => server.Ip.Equals(ip, StringComparison.OrdinalIgnoreCase)))
        {
            StatusMessage = $"服务器已存在：{ip}";
            return;
        }

        var name = NewServerName.Trim();
        if (name.Length == 0)
        {
            name = ip;
        }

        Servers.Add(new GameServerEntry(name, ip, ""));
        if (!Save())
        {
            Servers.RemoveAt(Servers.Count - 1);
            return;
        }

        NewServerName = "";
        NewServerIp = "";
        StatusMessage = $"已添加服务器：{name}";
        _notify(StatusMessage);
    }

    [RelayCommand]
    private void DeleteServer(GameServerEntry? server)
    {
        if (server is null)
        {
            return;
        }

        var index = Servers.IndexOf(server);
        if (index < 0)
        {
            return;
        }

        Servers.RemoveAt(index);
        if (!Save())
        {
            Servers.Insert(index, server);
            return;
        }

        StatusMessage = $"已删除服务器：{server.Name}";
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (_contentRoot.Length == 0)
        {
            return;
        }

        _services.System.OpenFolder(_contentRoot);
    }

    private bool Save()
    {
        if (_path.Length == 0)
        {
            StatusMessage = "实例目录不可用，无法保存服务器列表";
            return false;
        }

        try
        {
            ServersDatService.Write(_path, Servers.ToList());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"保存服务器列表失败：{ex.Message}";
            _services.Logger.Error("写入 servers.dat 失败", ex);
            return false;
        }
    }
}
