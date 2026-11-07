using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;
using Hycl.Launcher.Core.Services;
using Hycl.Launcher.Core.ViewModels.Pages;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>
/// 导入前展示的整合包文件项：required 强制安装（勾选框禁用），optional 由用户勾选。
/// 对应 PCL-CE 安装前逐个询问「下载 / 跳过」的清单。
/// </summary>
public sealed partial class ModpackFileOption : ObservableObject
{
    public required long FileId { get; init; }

    /// <summary>展示名（优先 CurseForge displayName，回退文件名）。</summary>
    public required string Name { get; init; }

    public string FileName { get; init; } = "";

    public string Info { get; init; } = "";

    public bool IsRequired { get; init; }

    public bool CanToggle => !IsRequired;

    public string BadgeText => IsRequired ? "必需" : "可选";

    public bool HasFileName => FileName.Length > 0 && !FileName.Equals(Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>勾选状态：required 恒为勾选；optional 默认勾选（与 PCL-CE 默认按钮「下载」一致）。</summary>
    [ObservableProperty]
    private bool _isSelected = true;
}

/// <summary>
/// 版本管理：实例列表、切换当前实例、实例设置（名称/备注/隔离/内存/JVM/窗口/Java）、
/// 复制、重命名、删除与目录入口。
/// </summary>
public sealed partial class InstancesViewModel : ObservableObject, IPageLifecycle
{
    private readonly LauncherServices _services;
    private readonly InstallTaskViewModel _installTask;
    private bool _suppressEditorReload;
    private CancellationTokenSource? _savesCts;
    private int _savesGeneration;

    public InstancesViewModel(LauncherServices services, InstallTaskViewModel installTask)
    {
        _services = services;
        _installTask = installTask;
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
        ModpackFileOptions.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasModpackFileOptions));
            OnPropertyChanged(nameof(ModpackFileSummary));
        };
        Files = new InstanceFilesViewModel(services);
        Files.DownloadRequested = type => DownloadResourceRequested?.Invoke(
            type,
            SelectedGameVersion,
            SelectedLoaderLabel,
            SelectedInstance?.Id ?? "");
        Export = new InstanceExportViewModel(services, message => StatusMessage = message);
        Servers = new InstanceServersViewModel(services, message => StatusMessage = message);
    }

    /// <summary>
    /// 从资源列表进入下载页（由主窗口接线）：携带当前实例的版本号/加载器/实例 Id，
    /// 让下载页自动选中对应类别并套用兼容筛选。
    /// </summary>
    public Action<CompResourceType, string, string, string>? DownloadResourceRequested { get; set; }

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

    /// <summary>导入前可选项清单（CurseForge：required 禁勾，optional 默认勾选）。</summary>
    public ObservableCollection<ModpackFileOption> ModpackFileOptions { get; } = new();

    public bool HasModpackFileOptions => ModpackFileOptions.Count > 0;

    public string ModpackFileSummary => ModpackFileOptions.Count == 0
        ? ""
        : $"共 {ModpackFileOptions.Count} 个文件：必需 {ModpackFileOptions.Count(o => o.IsRequired)} 个（强制安装），可选 {ModpackFileOptions.Count(o => !o.IsRequired)} 个";

    /// <summary>正在拉取清单文件元数据（网络请求期间禁用清单区并给出提示）。</summary>
    [ObservableProperty]
    private bool _isLoadingModpackFiles;

    [ObservableProperty]
    private string _modpackFileLoadError = "";

    public bool HasModpackFileLoadError => ModpackFileLoadError.Length > 0;

    /// <summary>本次导入是否允许用户选择 optional（CurseForge 且有 optional 文件）。</summary>
    private bool _hasOptionalSelection;

    partial void OnModpackFileLoadErrorChanged(string value)
        => OnPropertyChanged(nameof(HasModpackFileLoadError));

    /// <summary>取消当前整合包导入（会清理临时文件并回滚本次新建的实例目录）。</summary>
    [RelayCommand]
    private void CancelImport()
    {
        if (!IsImporting)
        {
            return;
        }

        ImportStageText = "正在取消导入…";
        _installTask.Cancel();
    }

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

    public string DataPacksDirectory => SelectedInstance is null ? "" : Path.Combine(ContentRoot, "datapacks");

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

    // ------------------------- 实例管理分区（对应 PCL-CE PageInstanceLeft） -------------------------

    /// <summary>当前分区键：overview/setup/modify/export/saves/screenshots/mods/resourcepacks/shaderpacks/schematics/servers。</summary>
    private string _sectionKey = "overview";

    public string InstanceSectionKey => _sectionKey;

    public bool IsOverviewSection => _sectionKey == "overview";

    public bool IsSetupSection => _sectionKey == "setup";

    public bool IsModifySection => _sectionKey == "modify";

    public bool IsExportSection => _sectionKey == "export";

    public bool IsSavesSection => _sectionKey == "saves";

    public bool IsScreenshotsSection => _sectionKey == "screenshots";

    public bool IsModsSection => _sectionKey == "mods";

    public bool IsResourcePacksSection => _sectionKey == "resourcepacks";

    public bool IsShaderPacksSection => _sectionKey == "shaderpacks";

    public bool IsDataPacksSection => _sectionKey == "datapacks";

    public bool IsSchematicsSection => _sectionKey == "schematics";

    public bool IsFilesSection => _sectionKey is "screenshots" or "mods" or "resourcepacks" or "shaderpacks"
        or "datapacks" or "schematics";

    public bool IsServersSection => _sectionKey == "servers";

    public InstanceFilesViewModel Files { get; }

    public InstanceExportViewModel Export { get; }

    public InstanceServersViewModel Servers { get; }

    /// <summary>由 UI 层注入的导出保存位置选择器（取消返回 null）。</summary>
    public Func<string?, Task<string?>>? ExportFilePicker
    {
        get => Export.SaveFilePicker;
        set => Export.SaveFilePicker = value;
    }

    [RelayCommand]
    private void SelectInstanceSection(string key)
    {
        if (_sectionKey == key)
        {
            return;
        }

        _sectionKey = key;
        NotifySection();
        RefreshSectionContent();
    }

    private void NotifySection()
    {
        OnPropertyChanged(nameof(InstanceSectionKey));
        OnPropertyChanged(nameof(IsOverviewSection));
        OnPropertyChanged(nameof(IsSetupSection));
        OnPropertyChanged(nameof(IsModifySection));
        OnPropertyChanged(nameof(IsExportSection));
        OnPropertyChanged(nameof(IsSavesSection));
        OnPropertyChanged(nameof(IsScreenshotsSection));
        OnPropertyChanged(nameof(IsModsSection));
        OnPropertyChanged(nameof(IsResourcePacksSection));
        OnPropertyChanged(nameof(IsShaderPacksSection));
        OnPropertyChanged(nameof(IsDataPacksSection));
        OnPropertyChanged(nameof(IsSchematicsSection));
        OnPropertyChanged(nameof(IsFilesSection));
        OnPropertyChanged(nameof(IsServersSection));
    }

    /// <summary>按当前分区准备内容 VM：只给当前分区扫描目录，避免每次选中实例都全量扫描。</summary>
    private void RefreshSectionContent()
    {
        var instance = SelectedInstance;
        if (instance is null)
        {
            Files.Configure("", "", "选择左侧版本后查看本地内容。", [], false, false);
            Files.Items.Clear();
            Export.Configure(null, "");
            Servers.Configure("");
            return;
        }

        var contentRoot = ContentRoot;
        var gameDirectory = _services.Config.EffectiveGameDirectory;
        var gameVersion = instance.GameVersion;
        var loader = instance.Loader;
        switch (_sectionKey)
        {
            case "export":
                Export.Configure(instance, contentRoot);
                break;
            case "servers":
                Servers.Configure(contentRoot);
                break;
            case "screenshots":
                Files.Configure(
                    gameDirectory, Path.Combine(contentRoot, "screenshots"),
                    "screenshots 目录下还没有截图。",
                    [".png", ".jpg", ".jpeg"], false, false,
                    sectionTitle: "截图");
                _ = Files.RefreshAsync();
                break;
            case "mods":
                Files.Configure(
                    gameDirectory, Path.Combine(contentRoot, "mods"),
                    "mods 目录下还没有模组。",
                    [".jar", ".zip"], false, true,
                    CompResourceType.Mod, gameVersion, loader, "模组管理");
                _ = Files.RefreshAsync();
                break;
            case "resourcepacks":
                Files.Configure(
                    gameDirectory, Path.Combine(contentRoot, "resourcepacks"),
                    "resourcepacks 目录下还没有资源包。",
                    [".zip"], true, false,
                    CompResourceType.ResourcePack, gameVersion, loader, "资源包管理");
                _ = Files.RefreshAsync();
                break;
            case "shaderpacks":
                Files.Configure(
                    gameDirectory, Path.Combine(contentRoot, "shaderpacks"),
                    "shaderpacks 目录下还没有光影包。",
                    [".zip"], true, false,
                    CompResourceType.Shader, gameVersion, loader, "光影管理");
                _ = Files.RefreshAsync();
                break;
            case "datapacks":
                Files.Configure(
                    gameDirectory, Path.Combine(contentRoot, "datapacks"),
                    "datapacks 目录下还没有数据包。",
                    [".zip"], true, false,
                    CompResourceType.DataPack, gameVersion, loader, "数据包管理");
                _ = Files.RefreshAsync();
                break;
            case "schematics":
                Files.Configure(
                    gameDirectory, Path.Combine(contentRoot, "schematics"),
                    "schematics 目录下还没有投影原理图。",
                    [".schematic", ".schem", ".litematic", ".nbt"], false, false,
                    sectionTitle: "投影原理图");
                _ = Files.RefreshAsync();
                break;
            default:
                break;
        }
    }

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
        var refreshStart = PerfTrace.Now;
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
        finally
        {
            PerfTrace.MarkSince("instances_refresh_done", refreshStart);
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
            await BeginImportAsync(path).ConfigureAwait(true);
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
            await BeginImportAsync(path).ConfigureAwait(true);
        }
    }

    /// <summary>先识别格式，再走命名对话框；不支持的格式当场说明原因，不进入命名步骤。</summary>
    private async Task BeginImportAsync(string path)
    {
        ModpackInspection inspection;
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
            StatusMessage = $"无法导入：{inspection.UnsupportedMessage}";
            return;
        }

        ModpackFileOptions.Clear();
        ModpackFileLoadError = "";
        IsLoadingModpackFiles = false;
        _hasOptionalSelection = false;

        ModpackDefinition? definition = null;
        if (inspection.Kind == "curseforge")
        {
            try
            {
                definition = ModpackReader.ParseModpack(path);
            }
            catch (Exception ex) when (ex is CompException or IOException or UnauthorizedAccessException)
            {
                ModpackFileLoadError = $"无法读取整合包清单：{ex.Message}";
            }
        }

        _pendingImportPath = path;
        _isImportDialog = true;
        _isRenameDialog = false;
        NameDialogTitle = "导入整合包";
        NameDialogText = SanitizeInstanceName(inspection.SuggestedName, Path.GetFileNameWithoutExtension(path));
        var detail = string.IsNullOrWhiteSpace(inspection.LoaderLabel)
            ? inspection.FormatName
            : $"{inspection.FormatName}，{inspection.LoaderLabel}";
        NameDialogHint = $"格式：{detail}。确认后新建实例，并按清单下载所需文件。必需文件强制安装，可选文件可在下方取消勾选。";
        IsNameDialogVisible = true;

        // CurseForge：optional 文件需要逐个询问（PCL-CE 的「下载 / 跳过」清单），这里先拉元数据填充勾选列表
        if (definition is not null && definition.OptionalCurseForgeFileIds.Count > 0)
        {
            _hasOptionalSelection = true;
            await LoadModpackFileOptionsAsync(path, definition).ConfigureAwait(true);
        }
    }

    /// <summary>拉取 CurseForge 文件元数据并填充勾选清单；失败时保留按 Id 的占位项，仍可取消勾选。</summary>
    private async Task LoadModpackFileOptionsAsync(string path, ModpackDefinition definition)
    {
        IsLoadingModpackFiles = true;
        ModpackFileLoadError = "";
        try
        {
            var optionalSet = definition.OptionalCurseForgeFileIds.ToHashSet();
            var requiredIds = definition.CurseForgeFileIds
                .Where(id => !optionalSet.Contains(id))
                .Distinct()
                .ToList();
            var optionalIds = definition.CurseForgeFileIds
                .Where(id => optionalSet.Contains(id))
                .Distinct()
                .ToList();
            var ordered = requiredIds.Concat(optionalIds).ToList();

            IReadOnlyList<CompFile> files;
            try
            {
                files = await _services.CompInstall
                    .GetCurseForgeFileMetadataAsync(ordered, CancellationToken.None)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                ModpackFileLoadError = $"无法读取文件清单（{ex.Message}），文件名不可用，可选文件将按默认勾选安装。";
                files = [];
            }

            // 用户可能在等待期间取消对话框或换了另一个整合包，避免回填到错误的清单
            if (!_isImportDialog || !string.Equals(_pendingImportPath, path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var metadataById = files.ToDictionary(file => long.Parse(file.Id), file => file);
            ModpackFileOptions.Clear();
            foreach (var fileId in ordered)
            {
                var required = !optionalSet.Contains(fileId);
                var file = metadataById.GetValueOrDefault(fileId);
                var name = file is null
                    ? $"文件 #{fileId}"
                    : string.IsNullOrWhiteSpace(file.DisplayName) ? file.FileName : file.DisplayName;
                var info = file is not null && file.Size > 0
                    ? InstanceContentService.FormatSize(file.Size)
                    : "";
                ModpackFileOptions.Add(new ModpackFileOption
                {
                    FileId = fileId,
                    Name = name,
                    FileName = file?.FileName ?? "",
                    Info = info,
                    IsRequired = required
                });
            }

            if (files.Count < ordered.Count && !HasModpackFileLoadError)
            {
                ModpackFileLoadError = $"有 {ordered.Count - files.Count} 个文件已从 CurseForge 删除，安装时会跳过。";
            }
        }
        finally
        {
            IsLoadingModpackFiles = false;
        }
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
        // required 由导入服务强制安装；这里只收集用户勾选的 optional（未提供选择时保持默认全装）
        IReadOnlyCollection<long>? optionalFileIds = _hasOptionalSelection
            ? ModpackFileOptions
                .Where(option => !option.IsRequired && option.IsSelected)
                .Select(option => option.FileId)
                .ToList()
            : null;
        _isImportDialog = false;
        _hasOptionalSelection = false;
        ModpackFileOptions.Clear();
        ModpackFileLoadError = "";
        IsLoadingModpackFiles = false;
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
            // 走全局安装任务：右下角悬浮下载按钮、下载页任务卡片、取消按钮都与普通下载一致
            var result = await _installTask.RunModpackImportAsync(
                    path,
                    name,
                    optionalFileIds,
                    value =>
                    {
                        ImportPercentage = value.Percentage * 100;
                        ImportStageText = string.IsNullOrEmpty(value.CurrentItem)
                            ? value.Stage
                            : $"{value.Stage} · {value.CurrentItem}";
                    })
                .ConfigureAwait(true);

            await RefreshAsync().ConfigureAwait(true);
            if (result.Busy)
            {
                StatusMessage = "已有下载或安装任务正在进行，请等它结束后再导入整合包";
            }
            else if (result.Cancelled)
            {
                StatusMessage = "已取消导入，临时文件与本次新建的实例目录已清理";
                _services.Logger.Info("用户取消整合包导入");
            }
            else if (result.Error is not null)
            {
                StatusMessage = $"导入失败：{result.Error.Message}";
                _services.Logger.Error("导入整合包失败", result.Error);
            }
            else if (result.InstalledId is not null)
            {
                SelectedInstance = Instances.FirstOrDefault(
                    x => x.Id.Equals(result.InstalledId, StringComparison.OrdinalIgnoreCase));
                StatusMessage = $"已导入整合包：{result.InstalledId}";
                DetailResetRequested?.Invoke(this, EventArgs.Empty);
            }
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
        _hasOptionalSelection = false;
        ModpackFileOptions.Clear();
        ModpackFileLoadError = "";
        IsLoadingModpackFiles = false;
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
            if (IsLoadingModpackFiles)
            {
                NameDialogHint = "正在读取可选文件清单，请稍候…";
                return;
            }

            // 与「下载新版本」一致：开始前先把重名实例按既有规则编号（26.3 → 26.3-1 …），
            // 让用户看到最终名称，而不是导入完成后才从状态栏得知被改名。
            var effectiveName = InstanceNameValidator.GetAvailableName(
                _services.Config.EffectiveGameDirectory, newId);
            if (!effectiveName.Equals(newId, StringComparison.Ordinal))
            {
                NameDialogText = effectiveName;
                StatusMessage = $"已存在同名实例 {newId}，将导入为 {effectiveName}";
            }

            await ConfirmImportAsync(effectiveName).ConfigureAwait(true);
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
        RefreshSectionContent();
    }

    partial void OnEditorVersionIsolationChanged(bool value)
    {
        if (!_suppressEditorReload)
        {
            NotifyDirectories();
            _ = ReloadSavesAsync();
            RefreshSectionContent();
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
