using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>
/// 版本管理：实例列表、切换当前实例、实例设置（名称/备注/隔离/内存/JVM/窗口/Java）、
/// 复制、重命名、删除与目录入口。
/// </summary>
public sealed partial class InstancesViewModel : ObservableObject, IPageLifecycle
{
    private readonly LauncherServices _services;
    private bool _suppressEditorReload;
    private CancellationTokenSource? _savesCts;
    private int _savesGeneration;

    public InstancesViewModel(LauncherServices services)
    {
        _services = services;
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(InstanceCountText));
        };
        Saves.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSaves));
            OnPropertyChanged(nameof(SavesTitleText));
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

    /// <summary>由 UI 层注入的文件夹选择器（取消返回 null）。</summary>
    public Func<string?, Task<string?>>? FolderPicker { get; set; }

    /// <summary>由 UI 层注入的整合包文件选择器（.zip / .mrpack / .rar，取消返回 null）。</summary>
    public Func<string?, Task<string?>>? ModpackFilePicker { get; set; }

    /// <summary>导入整合包的进度（0-100）。</summary>
    [ObservableProperty]
    private double _importPercentage;

    /// <summary>导入整合包当前阶段文字。</summary>
    [ObservableProperty]
    private string _importStageText = "";

    [ObservableProperty]
    private bool _isImporting;

    private string? _pendingImportPath;

    private bool _isImportDialog;

    /// <summary>Java 可执行文件选择器：返回 javaw.exe / java 的完整文件路径（由 UI 层注入）。</summary>
    public Func<string?, Task<string?>>? JavaFilePicker { get; set; }

    public bool HasInstances => Instances.Count > 0;

    public bool IsEmpty => Instances.Count == 0;

    public string InstanceCountText => Instances.Count == 0 ? "暂无已安装版本" : $"共 {Instances.Count} 个版本";

    public string SelectedName => SelectedInstance?.Name ?? "未选择版本";

    public string SelectedDescription => SelectedInstance?.Description ?? "选择左侧版本查看设置";

    public string SelectedPath => SelectedInstance?.Directory ?? "";

    public string SelectedGameVersion => SelectedInstance?.GameVersion ?? "";

    public string SelectedLoaderLabel => SelectedInstance?.Loader ?? "";

    public string SelectedIconKey => SelectedInstance?.IconKey ?? "generic";

    public bool SelectedHasBitmapIcon => SelectedInstance?.HasBitmapIcon ?? false;

    public bool SelectedUsesFallbackIcon => SelectedInstance?.UsesFallbackIcon ?? true;

    public string SelectedTypeLabel => SelectedInstance is null ? "" : InstanceDisplay.TypeLabel(SelectedInstance);

    public string SelectedLaunchCountText => SelectedInstance?.LaunchCountText ?? "0 次";

    public string SelectedLastWrite => SelectedInstance is null ? "" : InstanceDisplay.LastWriteText(SelectedInstance);

    public bool CanManage => SelectedInstance is not null;

    public bool IsSelectedCurrent => SelectedInstance is not null &&
                                     SelectedInstance.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase);

    public bool CanSetCurrent => !IsSelectedCurrent;

    /// <summary>实例内容根目录：隔离时是实例目录，否则是游戏根目录。</summary>
    public string ContentRoot => SelectedInstance is null
        ? ""
        : InstanceManager.GetContentRoot(
            _services.Config.EffectiveGameDirectory,
            SelectedInstance.Id,
            EditorVersionIsolation);

    public string ModsDirectory => SelectedInstance is null ? "" : Path.Combine(ContentRoot, "mods");

    public string ResourcePacksDirectory => SelectedInstance is null ? "" : Path.Combine(ContentRoot, "resourcepacks");

    public string ShaderPacksDirectory => SelectedInstance is null ? "" : Path.Combine(ContentRoot, "shaderpacks");

    public string SavesDirectory => SelectedInstance is null
        ? ""
        : string.IsNullOrWhiteSpace(EditorSavesDirectory)
            ? Path.Combine(ContentRoot, "saves")
            : EditorSavesDirectory;

    // ------------------------- 本地存档（列表 / 复制 / 删除 / 打开目录） -------------------------

    /// <summary>当前实例存档目录下的存档（随选中实例、隔离开关与独立存档目录变化重载）。</summary>
    public ObservableCollection<LocalSaveInfo> Saves { get; } = new();

    public bool HasSaves => Saves.Count > 0;

    public string SavesTitleText => Saves.Count == 0 ? "本地存档" : $"本地存档（{Saves.Count}）";

    public string SavesEmptyText => SelectedInstance is null
        ? "选择左侧版本后查看本地存档。"
        : string.IsNullOrWhiteSpace(SavesDirectory)
            ? "存档目录无效，请检查实例设置。"
            : "该目录下没有存档。";

    [ObservableProperty]
    private bool _isLoadingSaves;

    // ------------------------- 实例设置编辑区 -------------------------

    [ObservableProperty]
    private string _editorRemark = "";

    [ObservableProperty]
    private bool _editorVersionIsolation = true;

    [ObservableProperty]
    private string _editorSavesDirectory = "";

    [ObservableProperty]
    private string _editorJavaPath = "";

    [ObservableProperty]
    private string _editorMemoryText = "";

    [ObservableProperty]
    private string _editorInitialMemoryText = "";

    [ObservableProperty]
    private string _editorJvmArguments = "";

    [ObservableProperty]
    private string _editorGameArguments = "";

    [ObservableProperty]
    private string _editorWindowTitle = "";

    [ObservableProperty]
    private string _editorTypeInfo = "";

    [ObservableProperty]
    private string _editorServerToEnter = "";

    /// <summary>窗口模式选项（索引 0 跟随全局，其余映射到 0~4）。</summary>
    [ObservableProperty]
    private string _editorWindowTypeOption = "跟随全局";

    /// <summary>游戏内设置来源：跟随全局（首次启动写入）/ 跟随实例（不修改游戏内设置）。</summary>
    [ObservableProperty]
    private string _editorGameSettingsMode = "跟随全局";

    public IReadOnlyList<string> GameSettingsModeOptions { get; } = ["跟随全局", "跟随实例"];

    [ObservableProperty]
    private string _editorWindowWidthText = "";

    [ObservableProperty]
    private string _editorWindowHeightText = "";

    public IReadOnlyList<string> WindowTypeOptions { get; } =
        ["跟随全局", "默认", "全屏", "与启动器相同", "自定义", "最大化"];

    // ------------------------- 复制/重命名对话框 -------------------------

    [ObservableProperty]
    private bool _isNameDialogVisible;

    [ObservableProperty]
    private string _nameDialogTitle = "";

    [ObservableProperty]
    private string _nameDialogText = "";

    [ObservableProperty]
    private string _nameDialogHint = "";

    private bool _isRenameDialog;

    public event EventHandler<string>? LaunchRequested;

    /// <summary>当前实例变化（参数为实例 ID，空表示无实例）。</summary>
    public event EventHandler<string>? SelectionChanged;

    /// <summary>删除实例后列表已重置：UI 应滚动回开头，避免用户误以为还停在旧实例。</summary>
    public event EventHandler? ListResetRequested;

    /// <summary>删除/复制/重命名导致选中实例被自动切换：右侧详情应滚回顶部，避免停在危险操作区。</summary>
    public event EventHandler? DetailResetRequested;

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            var instances = await _services.Instances.ScanAsync(_services.Config.EffectiveGameDirectory).ConfigureAwait(true);
            var previousId = SelectedInstance?.Id ?? _services.Config.SelectedVersion;
            Instances.Clear();
            foreach (var instance in ApplyOrder(instances))
            {
                instance.LaunchCount = _services.Config.GetLaunchCount(instance.Id);
                Instances.Add(instance);
            }

            SelectedInstance = Instances.FirstOrDefault(x => x.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase))
                               ?? Instances.FirstOrDefault();
            UpdateCurrentMarkers();
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

    /// <summary>按玩家手动排序（config.InstanceOrder）整理实例；未列入的保持默认顺序排在后面。</summary>
    private IReadOnlyList<MinecraftInstance> ApplyOrder(IReadOnlyList<MinecraftInstance> instances)
    {
        var order = _services.Config.InstanceOrder;
        if (order.Count == 0)
        {
            return instances;
        }

        var rank = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < order.Count; i++)
        {
            rank[order[i]] = i;
        }

        return instances
            .OrderBy(x => rank.TryGetValue(x.Id, out var r) ? r : int.MaxValue)
            .ToList();
    }

    /// <summary>把当前列表顺序写回 config 并持久化。</summary>
    private async Task PersistOrderAsync()
    {
        _services.Config.InstanceOrder = Instances.Select(x => x.Id).ToList();
        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
    }

    /// <summary>右键菜单：上移/下移实例。</summary>
    [RelayCommand]
    public async Task MoveInstanceAsync(string direction)
    {
        if (SelectedInstance is null || Instances.Count < 2)
        {
            return;
        }

        var index = Instances.IndexOf(SelectedInstance);
        var target = direction == "up" ? index - 1 : index + 1;
        if (target < 0 || target >= Instances.Count)
        {
            return;
        }

        Instances.Move(index, target);
        await PersistOrderAsync().ConfigureAwait(true);
        UpdateCurrentMarkers();
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
        UpdateCurrentMarkers();
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

    /// <summary>右键菜单入口：先选中该实例再弹出删除确认。</summary>
    [RelayCommand]
    private void RequestDeleteFor(MinecraftInstance? instance)
    {
        if (instance is null)
        {
            return;
        }

        SelectedInstance = instance;
        RequestDelete();
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

        _services.Config.RemoveInstanceSettings(target.Id);
        _services.Config.RemoveLaunchCount(target.Id);
        _services.Config.InstanceOrder.Remove(target.Id);
        StatusMessage = $"已删除 {target.Name}";
        if (_services.Config.SelectedVersion.Equals(target.Id, StringComparison.OrdinalIgnoreCase))
        {
            _services.Config.SelectedVersion = "";
        }

        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectionChanged?.Invoke(this, "");
        // 选中项已回到列表第一项（RefreshAsync 兜底），通知 UI 滚回开头；
        // 右侧详情同样归位，避免滚动位置停在刚才点击确认删除的底部区域
        ListResetRequested?.Invoke(this, EventArgs.Empty);
        DetailResetRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------- 设置保存 -------------------------

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        if (!TryParseNumber(EditorMemoryText, 0, 65536, out var memory, "最大内存"))
        {
            return;
        }

        if (!TryParseNumber(EditorInitialMemoryText, -1, 65536, out var initial, "初始内存"))
        {
            return;
        }

        if (!TryParseNumber(EditorWindowWidthText, 0, 7680, out var width, "窗口宽度"))
        {
            return;
        }

        if (!TryParseNumber(EditorWindowHeightText, 0, 4320, out var height, "窗口高度"))
        {
            return;
        }

        var javaPath = EditorJavaPath.Trim();
        if (javaPath.Length > 0 && !File.Exists(javaPath))
        {
            StatusMessage = "Java 路径不存在，请重新选择";
            return;
        }

        var savesDirectory = EditorSavesDirectory.Trim();
        if (savesDirectory.Length > 0 && !Path.IsPathFullyQualified(savesDirectory))
        {
            StatusMessage = "独立存档目录必须是完整路径";
            return;
        }

        var windowTypeIndex = WindowTypeOptions.ToList().IndexOf(EditorWindowTypeOption);
        if (windowTypeIndex < 0)
        {
            windowTypeIndex = 0;
        }

        var settings = _services.Config.GetOrCreateInstanceSettings(SelectedInstance.Id);
        settings.Remark = EditorRemark.Trim();
        settings.VersionIsolation = EditorVersionIsolation;
        settings.SavesDirectory = savesDirectory;
        settings.JavaPath = javaPath;
        settings.MemoryMegabytes = memory;
        settings.InitialMemoryMegabytes = initial;
        settings.ExtraJvmArguments = EditorJvmArguments.Trim();
        settings.ExtraGameArguments = EditorGameArguments.Trim();
        settings.WindowTitle = EditorWindowTitle.Trim();
        settings.TypeInfo = EditorTypeInfo.Trim();
        settings.ServerToEnter = EditorServerToEnter.Trim();
        settings.WindowType = windowTypeIndex == 0 ? -1 : windowTypeIndex - 1;
        settings.WindowWidth = width;
        settings.WindowHeight = height;
        settings.GameSettingsMode = EditorGameSettingsMode == "跟随实例" ? "instance" : "global";

        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        StatusMessage = $"已保存 {SelectedInstance.Name} 的实例设置";
        NotifySelection();
        NotifyDirectories();
        await ReloadSavesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetSettingsAsync()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _services.Config.RemoveInstanceSettings(SelectedInstance.Id);
        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        LoadEditor(SelectedInstance);
        StatusMessage = "已恢复为跟随全局设置";
        await ReloadSavesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task BrowseSavesDirectoryAsync()
    {
        if (FolderPicker is null)
        {
            return;
        }

        var picked = await FolderPicker(SavesDirectory).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            EditorSavesDirectory = picked;
            NotifyDirectories();
        }
    }

    [RelayCommand]
    private async Task BrowseJavaPathAsync()
    {
        if (JavaFilePicker is null)
        {
            return;
        }

        var start = string.IsNullOrWhiteSpace(EditorJavaPath)
            ? _services.Config.EffectiveGameDirectory
            : Path.GetDirectoryName(EditorJavaPath);
        var picked = await JavaFilePicker(start).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(picked))
        {
            EditorJavaPath = picked;
        }
    }

    // ------------------------- 目录入口 -------------------------

    [RelayCommand]
    private void OpenInstanceFolder() => OpenDirectory(SelectedInstance?.Directory);

    [RelayCommand]
    private void OpenModsFolder() => OpenDirectory(ModsDirectory);

    [RelayCommand]
    private void OpenResourcePacksFolder() => OpenDirectory(ResourcePacksDirectory);

    [RelayCommand]
    private void OpenShaderPacksFolder() => OpenDirectory(ShaderPacksDirectory);

    [RelayCommand]
    private void OpenSavesFolder() => OpenDirectory(SavesDirectory);

    [RelayCommand]
    private void OpenSave(LocalSaveInfo? save)
    {
        if (save is null)
        {
            return;
        }

        OpenDirectory(save.Directory);
    }

    [RelayCommand]
    private async Task CopySaveAsync(LocalSaveInfo? save)
    {
        if (save is null)
        {
            return;
        }

        var result = SaveManager.Duplicate(save.Directory);
        StatusMessage = result.Message;
        await ReloadSavesAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DeleteSaveAsync(LocalSaveInfo? save)
    {
        if (save is null)
        {
            return;
        }

        var deleted = SaveManager.DeleteToRecycleBin(
            save.Directory,
            _services.Config.EffectiveGameDirectory,
            _services.Logger);
        StatusMessage = deleted
            ? $"已删除存档到回收站：{save.Name}"
            : $"删除存档失败：{save.Name}";
        if (deleted)
        {
            await ReloadSavesAsync().ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task RefreshSavesAsync() => await ReloadSavesAsync().ConfigureAwait(true);

    /// <summary>重新扫描存档目录；结果按选中实例/目录变化做代次校验，旧任务直接丢弃。</summary>
    private async Task ReloadSavesAsync()
    {
        _savesCts?.Cancel();
        _savesCts?.Dispose();
        var cts = new CancellationTokenSource();
        _savesCts = cts;
        var token = cts.Token;
        var generation = ++_savesGeneration;

        var directory = SavesDirectory;
        if (SelectedInstance is null || string.IsNullOrWhiteSpace(directory))
        {
            Saves.Clear();
            IsLoadingSaves = false;
            return;
        }

        IsLoadingSaves = true;
        try
        {
            var list = await Task.Run(() => SaveManager.Scan(directory, token), token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _savesGeneration)
            {
                return;
            }

            Saves.Clear();
            foreach (var save in list)
            {
                Saves.Add(save);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 切页/切实例导致取消：保留已有列表，不写错误
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation == _savesGeneration)
            {
                Saves.Clear();
                StatusMessage = $"读取存档目录失败：{ex.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_savesCts, cts))
            {
                _savesCts = null;
                IsLoadingSaves = false;
            }

            cts.Dispose();
        }
    }

    /// <summary>离开版本页：取消正在进行的存档扫描（切页即取消）。</summary>
    public void Deactivate()
    {
        _savesCts?.Cancel();
        _savesCts?.Dispose();
        _savesCts = null;
        _savesGeneration++;
        IsLoadingSaves = false;
    }

    public void Activate()
    {
        if (SelectedInstance is not null)
        {
            _ = ReloadSavesAsync();
        }
    }

    private void OpenDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(directory);
            _services.System.OpenFolder(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"无法打开目录：{ex.Message}";
        }
    }

    // ------------------------- 导入整合包 -------------------------

    /// <summary>从本地压缩包导入整合包（对应 PCL-CE 整合包导入入口）。</summary>
    [RelayCommand]
    private async Task ImportModpackAsync()
    {
        if (ModpackFilePicker is null)
        {
            StatusMessage = "当前环境无法打开文件选择器";
            return;
        }

        string? path;
        try
        {
            _services.Logger.Info("导入整合包：打开文件选择器");
            path = await ModpackFilePicker(_services.Config.EffectiveGameDirectory).ConfigureAwait(true);
            _services.Logger.Info($"导入整合包：选择结果 {path ?? "<取消>"}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法打开文件选择器：{ex.Message}";
            _services.Logger.Error("导入整合包：文件选择器失败", ex);
            return;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            BeginImport(path);
        }
    }

    /// <summary>从已解压的文件夹导入整合包（PCL-CE 没有该入口，这里补齐文件夹形式）。</summary>
    [RelayCommand]
    private async Task ImportModpackFolderAsync()
    {
        if (FolderPicker is null)
        {
            StatusMessage = "当前环境无法打开文件夹选择器";
            return;
        }

        string? path;
        try
        {
            path = await FolderPicker(_services.Config.EffectiveGameDirectory).ConfigureAwait(true);
            _services.Logger.Info($"导入整合包：选择文件夹 {path ?? "<取消>"}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"无法打开文件夹选择器：{ex.Message}";
            _services.Logger.Error("导入整合包：文件夹选择器失败", ex);
            return;
        }

        if (!string.IsNullOrWhiteSpace(path))
        {
            BeginImport(path);
        }
    }

    /// <summary>先识别格式，再走命名对话框；不支持的格式当场说明原因，不进入命名步骤。</summary>
    private void BeginImport(string path)
    {
        CompInstallService.ModpackInspection inspection;
        try
        {
            inspection = CompInstallService.InspectModpack(path);
        }
        catch (Exception ex) when (ex is CompException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"无法导入：{ex.Message}";
            return;
        }

        if (!inspection.IsSupported)
        {
            StatusMessage = $"无法导入：{inspection.FormatName} 暂未实现，当前支持 Modrinth / CurseForge";
            return;
        }

        _pendingImportPath = path;
        _isImportDialog = true;
        _isRenameDialog = false;
        NameDialogTitle = "导入整合包";
        NameDialogText = SanitizeInstanceName(inspection.SuggestedName, Path.GetFileNameWithoutExtension(path));
        NameDialogHint = $"格式：{inspection.FormatName}。确认后新建实例，并按清单下载所需文件。";
        IsNameDialogVisible = true;
    }

    /// <summary>整合包名称可能带空格/非法字符，过滤为合法的 versions 目录名。</summary>
    private static string SanitizeInstanceName(string? suggested, string fallback)
    {
        var name = string.IsNullOrWhiteSpace(suggested) ? fallback : suggested;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, ' ');
        }

        name = name.Trim();
        return InstanceManager.IsValidInstanceId(name) ? name : "导入整合包";
    }

    private async Task ConfirmImportAsync(string name)
    {
        var path = _pendingImportPath;
        _isImportDialog = false;
        _pendingImportPath = null;
        if (path is null)
        {
            IsNameDialogVisible = false;
            return;
        }

        IsNameDialogVisible = false;
        NameDialogHint = "";
        IsImporting = true;
        ImportPercentage = 0;
        ImportStageText = "准备导入…";
        try
        {
            await _services.CompInstall.ImportModpackAsync(
                    path,
                    _services.Config.EffectiveGameDirectory,
                    name,
                    DownloadPolicy.FromConfig(_services.Config),
                    _services.Config.DownloadThreads,
                    new Progress<InstallProgress>(value =>
                    {
                        ImportPercentage = value.Percentage * 100;
                        ImportStageText = string.IsNullOrEmpty(value.CurrentItem)
                            ? value.Stage
                            : $"{value.Stage} · {value.CurrentItem}";
                    }),
                    CancellationToken.None)
                .ConfigureAwait(true);

            await RefreshAsync().ConfigureAwait(true);
            SelectedInstance = Instances.FirstOrDefault(x => x.Id.Equals(name, StringComparison.OrdinalIgnoreCase));
            StatusMessage = $"已导入整合包：{name}";
            DetailResetRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusMessage = $"导入失败：{ex.Message}";
            _services.Logger.Error("导入整合包失败", ex);
            await RefreshAsync().ConfigureAwait(true);
        }
        finally
        {
            IsImporting = false;
            ImportPercentage = 0;
            ImportStageText = "";
        }
    }

    // ------------------------- 复制 / 重命名 -------------------------

    [RelayCommand]
    private void StartCopy()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _isRenameDialog = false;
        NameDialogTitle = $"复制实例 {SelectedInstance.Name}";
        NameDialogText = SelectedInstance.Id + " - Copy";
        NameDialogHint = "新实例将复制当前版本目录、存档与模组";
        IsNameDialogVisible = true;
    }

    [RelayCommand]
    private void StartRename()
    {
        if (SelectedInstance is null)
        {
            return;
        }

        _isRenameDialog = true;
        NameDialogTitle = $"重命名实例 {SelectedInstance.Name}";
        NameDialogText = SelectedInstance.Id;
        NameDialogHint = "将同时重命名实例目录与版本 JSON/JAR";
        IsNameDialogVisible = true;
    }

    [RelayCommand]
    private void CancelNameDialog()
    {
        IsNameDialogVisible = false;
        NameDialogHint = "";
        _isImportDialog = false;
        _pendingImportPath = null;
    }

    [RelayCommand]
    private async Task ConfirmNameDialogAsync()
    {
        var rawName = NameDialogText;
        var nameError = InstanceNameValidator.ValidateName(rawName);
        if (nameError is not null)
        {
            NameDialogHint = nameError;
            return;
        }

        var newId = rawName.Trim();

        if (_isImportDialog)
        {
            var collisionError = InstanceNameValidator.ValidateCollision(
                _services.Config.EffectiveGameDirectory, newId);
            if (collisionError is not null)
            {
                NameDialogHint = collisionError;
                return;
            }

            await ConfirmImportAsync(newId).ConfigureAwait(true);
            return;
        }

        if (SelectedInstance is null)
        {
            return;
        }

        var sourceId = SelectedInstance.Id;
        var result = _isRenameDialog
            ? _services.Instances.Rename(_services.Config.EffectiveGameDirectory, sourceId, newId)
            : _services.Instances.Copy(_services.Config.EffectiveGameDirectory, sourceId, newId);

        if (!result.Success)
        {
            NameDialogHint = result.Message;
            return;
        }

        if (_isRenameDialog)
        {
            var settings = _services.Config.FindInstanceSettings(sourceId);
            if (settings is not null)
            {
                _services.Config.RemoveInstanceSettings(sourceId);
                _services.Config.InstanceSettings[newId] = settings;
            }

            if (_services.Config.SelectedVersion.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
            {
                _services.Config.SelectedVersion = newId;
            }

            _services.Config.MoveLaunchCount(sourceId, newId);
        }
        else
        {
            // 复制出的新实例从 0 开始计数
            _services.Config.RemoveLaunchCount(newId);
        }

        IsNameDialogVisible = false;
        NameDialogHint = "";
        StatusMessage = result.Message;
        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        await RefreshAsync().ConfigureAwait(true);
        SelectedInstance = Instances.FirstOrDefault(x => x.Id.Equals(newId, StringComparison.OrdinalIgnoreCase));
        // 复制/重命名后选中的是新实例，详情内容整体更换：滚回顶部
        DetailResetRequested?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------- 选中变化 -------------------------

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        IsConfirmingDelete = false;

        if (value is null)
        {
            _suppressEditorReload = true;
            ClearEditor();
            _suppressEditorReload = false;
        }
        else
        {
            LoadEditor(value);
        }

        NotifySelection();
        StatusMessage = "";
        _ = ReloadSavesAsync();
    }

    partial void OnEditorVersionIsolationChanged(bool value)
    {
        if (!_suppressEditorReload)
        {
            NotifyDirectories();
            _ = ReloadSavesAsync();
        }
    }

    partial void OnEditorSavesDirectoryChanged(string value)
    {
        if (!_suppressEditorReload)
        {
            NotifyDirectories();
            _ = ReloadSavesAsync();
        }
    }

    private void LoadEditor(MinecraftInstance instance)
    {
        var settings = _services.Config.FindInstanceSettings(instance.Id);
        _suppressEditorReload = true;
        EditorRemark = settings?.Remark ?? "";
        EditorVersionIsolation = settings?.VersionIsolation ?? true;
        EditorSavesDirectory = settings?.SavesDirectory ?? "";
        EditorJavaPath = settings?.JavaPath ?? "";
        EditorMemoryText = settings is null || settings.MemoryMegabytes == 0 ? "" : settings.MemoryMegabytes.ToString();
        EditorInitialMemoryText = settings is null || settings.InitialMemoryMegabytes < 0
            ? ""
            : settings.InitialMemoryMegabytes.ToString();
        EditorJvmArguments = settings?.ExtraJvmArguments ?? "";
        EditorGameArguments = settings?.ExtraGameArguments ?? "";
        EditorWindowTitle = settings?.WindowTitle ?? "";
        EditorTypeInfo = settings?.TypeInfo ?? "";
        EditorServerToEnter = settings?.ServerToEnter ?? "";
        EditorWindowTypeOption = settings is null || settings.WindowType < 0
            ? "跟随全局"
            : WindowTypeOptions[Math.Min(settings.WindowType + 1, WindowTypeOptions.Count - 1)];
        EditorWindowWidthText = settings is null || settings.WindowWidth == 0 ? "" : settings.WindowWidth.ToString();
        EditorWindowHeightText = settings is null || settings.WindowHeight == 0 ? "" : settings.WindowHeight.ToString();
        EditorGameSettingsMode = settings?.GameSettingsMode == "instance" ? "跟随实例" : "跟随全局";
        _suppressEditorReload = false;
        NotifyDirectories();
    }

    private void ClearEditor()
    {
        EditorRemark = "";
        EditorVersionIsolation = true;
        EditorSavesDirectory = "";
        EditorJavaPath = "";
        EditorMemoryText = "";
        EditorInitialMemoryText = "";
        EditorJvmArguments = "";
        EditorGameArguments = "";
        EditorWindowTitle = "";
        EditorTypeInfo = "";
        EditorServerToEnter = "";
        EditorWindowTypeOption = "跟随全局";
        EditorWindowWidthText = "";
        EditorWindowHeightText = "";
        EditorGameSettingsMode = "跟随全局";
        NotifyDirectories();
    }

    private bool TryParseNumber(string text, int minimum, int maximum, out int value, string label)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            value = minimum;
            return true;
        }

        if (!int.TryParse(trimmed, out value) || value < minimum || value > maximum)
        {
            StatusMessage = $"{label}需为 {minimum}~{maximum} 之间的整数，留空表示跟随全局";
            return false;
        }

        return true;
    }

    private void NotifySelection()
    {
        OnPropertyChanged(nameof(SelectedName));
        OnPropertyChanged(nameof(SelectedDescription));
        OnPropertyChanged(nameof(SelectedPath));
        OnPropertyChanged(nameof(SelectedGameVersion));
        OnPropertyChanged(nameof(SelectedLoaderLabel));
        OnPropertyChanged(nameof(SelectedIconKey));
        OnPropertyChanged(nameof(SelectedHasBitmapIcon));
        OnPropertyChanged(nameof(SelectedUsesFallbackIcon));
        OnPropertyChanged(nameof(SelectedTypeLabel));
        OnPropertyChanged(nameof(SelectedLaunchCountText));
        OnPropertyChanged(nameof(SelectedLastWrite));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(IsSelectedCurrent));
        OnPropertyChanged(nameof(CanSetCurrent));
    }

    /// <summary>同步列表的「当前」角标（当前版本来自全局配置）。</summary>
    private void UpdateCurrentMarkers()
    {
        var currentId = _services.Config.SelectedVersion;
        foreach (var instance in Instances)
        {
            instance.IsCurrent = !string.IsNullOrEmpty(currentId) &&
                                 instance.Id.Equals(currentId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private void NotifyDirectories()
    {
        OnPropertyChanged(nameof(ContentRoot));
        OnPropertyChanged(nameof(ModsDirectory));
        OnPropertyChanged(nameof(ResourcePacksDirectory));
        OnPropertyChanged(nameof(ShaderPacksDirectory));
        OnPropertyChanged(nameof(SavesDirectory));
        OnPropertyChanged(nameof(SavesEmptyText));
    }
}
