using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;

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
    DataPacks,
    Schematics,
    Servers
}

/// <summary>内容列表里的一行（模组 / 资源包 / 光影 / 数据包 / 投影 / 截图）。</summary>
public sealed partial class LocalContentItemViewModel : ObservableObject
{
    private readonly string _searchText;

    public LocalContentItemViewModel(LocalContentFile file, LocalContentMetadata metadata)
    {
        File = file;
        Metadata = metadata;
        _searchText = string.Join('\n',
            file.Name,
            System.IO.Path.GetFileNameWithoutExtension(file.Name),
            metadata.Name,
            metadata.Version,
            metadata.Description,
            metadata.ModId,
            string.Join(' ', metadata.Tags));
    }

    public LocalContentFile File { get; }

    public LocalContentMetadata Metadata { get; }

    public string Name => Metadata.Name.Length > 0 ? Metadata.Name : File.Name;

    public string Path => File.Path;

    public string FileName => File.Name;

    public string VersionText => Metadata.Version.Length > 0 ? Metadata.Version : "—";

    public string Description => Metadata.Description;

    public string? PreviewUrl => File.IsDirectory ? null : Path;

    public bool IsDirectory => File.IsDirectory;

    public bool HasIcon => IconUrl.Length > 0 && !IsImage;

    public bool IsImage => !IsDirectory &&
                           (Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            Name.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            Name.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

    public bool IsEnabled => File.IsEnabled;

    public string StateText => IsEnabled ? "" : "已禁用";

    /// <summary>禁用/启用按钮文案随状态变化：已启用显示「禁用」，已禁用显示「启用」。</summary>
    public string ToggleText => IsEnabled ? "禁用" : "启用";

    public string Info => $"{InstanceContentService.FormatSize(File.Size)} · {File.Modified:yyyy-MM-dd HH:mm}";

    public string MetaText => $"{Info} · {VersionText} · {SourceText}";

    /// <summary>无社区图标且非图片文件时回退到通用图标。</summary>
    public bool ShowFallbackIcon => !HasIcon && !IsImage;

    /// <summary>名称/描述/标签/文件名/modid 的合并索引，供搜索框过滤。</summary>
    public string SearchText => _searchText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText))]
    private string _latestVersion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    private string _sourceText = "本地";

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>社区项目图标 URL（识别成功后由更新检测填充，HyclImage 自带磁盘缓存）。</summary>
    [ObservableProperty]
    private string _iconUrl = "";

    /// <summary>最近一次洞察检查的缓存数据（一键更新按此下载最新兼容版本）。</summary>
    public ContentInsightEntry? Insight { get; set; }

    public string UpdateText => IsUpdateAvailable
        ? (LatestVersion.Length > 0 ? $"可更新 {LatestVersion}" : "可更新")
        : "";

    public bool Matches(string query) => SearchText.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 通用本地内容列表：模组（支持启用/禁用）、资源包、光影包、数据包、投影原理图、截图。
/// 工具条（搜索/导入/下载入口/全选/导出/模组关系）+ 分类计数 + 排序都在这里统一处理。
/// </summary>
public sealed partial class InstanceFilesViewModel : ObservableObject
{
    public const string SortByName = "资源名称";
    public const string SortByFileName = "文件名";
    public const string SortBySize = "大小";

    private readonly LauncherServices _services;
    private readonly List<LocalContentItemViewModel> _allItems = [];
    private string _gameDirectory = "";
    private string _directory = "";
    private string[] _extensions = [];
    private bool _includeDirectories;
    private bool _supportsDisable;
    private CompResourceType? _resourceType;
    private string _gameVersion = "";
    private string _loader = "";
    private CancellationTokenSource? _updateCts;
    private int _loadGeneration;

    public InstanceFilesViewModel(LauncherServices services)
    {
        _services = services;
        SortOptions = [SortByName, SortByFileName, SortBySize];
    }

    /// <summary>UI 注入的文件选择器（从文件安装；返回 null 表示用户取消）。</summary>
    public Func<string?, Task<string?>>? FilePicker { get; set; }

    /// <summary>UI 注入的保存位置选择器（导出信息；返回 null 表示用户取消）。</summary>
    public Func<string?, Task<string?>>? SaveInfoPicker { get; set; }

    /// <summary>进入下载页并选中对应资源类别（由主窗口接线）。</summary>
    public Action<CompResourceType>? DownloadRequested { get; set; }

    public ObservableCollection<LocalContentItemViewModel> Items { get; } = new();

    public ObservableCollection<ModRelationLine> Relations { get; } = new();

    public IReadOnlyList<string> SortOptions { get; }

    [ObservableProperty]
    private string _sectionTitle = "本地内容";

    [ObservableProperty]
    private string _emptyText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _showToggle;

    [ObservableProperty]
    private bool _isShowingUpdates;

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private string _selectedSort = SortByName;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private bool _isCheckingUpdates;

    /// <summary>批量更新进行中（一键更新按钮防重入）。</summary>
    [ObservableProperty]
    private bool _isUpdating;

    partial void OnIsUpdatingChanged(bool value) => OnPropertyChanged(nameof(UpdateAllText));

    [ObservableProperty]
    private string _updateCheckText = "";

    [ObservableProperty]
    private bool _isRelationsDialogVisible;

    [ObservableProperty]
    private string _relationsSummary = "";

    public string Directory => _directory;

    public bool HasItems => Items.Count > 0;

    public string CountText => _allItems.Count == 0
        ? ""
        : Items.Count == _allItems.Count
            ? $"共 {_allItems.Count} 项"
            : $"当前 {Items.Count} / 共 {_allItems.Count} 项";

    public string AllCountText => $"全部({_allItems.Count})";

    public string UpdatableCountText => $"可更新({_allItems.Count(item => item.IsUpdateAvailable && item.IsEnabled)})";

    /// <summary>可更新资源数量（一键更新按钮徽标用；禁用的资源不计入，对齐 PCL-CE 的 enabled 门槛）。</summary>
    public int UpdatableCount => _allItems.Count(item => item.IsUpdateAvailable && item.IsEnabled);

    public bool HasUpdatable => UpdatableCount > 0 && CanCheckUpdates;

    /// <summary>一键更新按钮文案：显示待更新数量；批量进行中显示进度状态。</summary>
    public string UpdateAllText => IsUpdating ? "正在批量更新…" : $"一键更新 ({UpdatableCount})";

    public string EmptyTitle => _allItems.Count == 0 ? "还没有资源" : "没有匹配的资源";

    public string EmptyDescription
    {
        get
        {
            if (_allItems.Count == 0)
            {
                return EmptyText.Length > 0 ? EmptyText : "当前目录下还没有资源。";
            }

            return IsShowingUpdates && SearchText.Trim().Length == 0
                ? "没有检测到可更新的资源。"
                : "试试调整搜索关键词，或切换回「全部」。";
        }
    }

    public bool CanCheckUpdates =>
        _resourceType is CompResourceType.Mod or CompResourceType.ResourcePack
            or CompResourceType.Shader or CompResourceType.DataPack
        && _allItems.Count > 0;

    public bool ShowUpdateFilter => CanCheckUpdates;

    public bool ShowDownloadEntry => _resourceType is not null;

    public bool ShowModRelations => _resourceType == CompResourceType.Mod;

    public bool AllSelected => Items.Count > 0 && Items.All(item => item.IsSelected);

    public string AllSelectText => AllSelected ? "取消全选" : "全选";

    public string SelectionText => $"已选 {Items.Count(item => item.IsSelected)} / {Items.Count}";

    public string SortDirectionText => SortDescending ? "降序" : "升序";

    public void Configure(
        string gameDirectory,
        string directory,
        string emptyText,
        string[] extensions,
        bool includeDirectories,
        bool supportsDisable,
        CompResourceType? resourceType = null,
        string gameVersion = "",
        string loader = "",
        string sectionTitle = "本地内容")
    {
        CancelUpdateCheck();
        _loadGeneration++;
        _gameDirectory = gameDirectory;
        _directory = directory;
        EmptyText = emptyText;
        _extensions = extensions;
        _includeDirectories = includeDirectories;
        _supportsDisable = supportsDisable;
        _resourceType = resourceType;
        _gameVersion = gameVersion;
        _loader = loader;
        SectionTitle = sectionTitle;
        ShowToggle = supportsDisable;
        StatusMessage = "";
        UpdateCheckText = "";
        IsCheckingUpdates = false;
        IsShowingUpdates = false;
        SearchText = "";
        SelectedSort = SortByName;
        SortDescending = false;
        IsRelationsDialogVisible = false;
        Relations.Clear();
        DetachItems();
        _allItems.Clear();
        Items.Clear();
        NotifyListState();
        OnPropertyChanged(nameof(Directory));
        OnPropertyChanged(nameof(ShowDownloadEntry));
        OnPropertyChanged(nameof(ShowModRelations));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(ShowUpdateFilter));
    }

    public Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        var generation = ++_loadGeneration;
        CancelUpdateCheck();
        if (string.IsNullOrWhiteSpace(_directory))
        {
            DetachItems();
            _allItems.Clear();
            Items.Clear();
            NotifyListState();
            return;
        }

        IsLoading = true;
        try
        {
            var directory = _directory;
            var extensions = _extensions;
            var includeDirectories = _includeDirectories;
            var snapshot = await Task.Run(
                () => InstanceContentService.Scan(directory, extensions, includeDirectories)).ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                return;
            }

            var items = await Task.Run(() => snapshot
                .Select(file => new LocalContentItemViewModel(
                    file,
                    LocalContentMetadataReader.Read(file.Path, file.IsDirectory)))
                .ToList()).ConfigureAwait(true);
            if (generation != _loadGeneration)
            {
                return;
            }

            DetachItems();
            _allItems.Clear();
            _allItems.AddRange(items);
            foreach (var item in _allItems)
            {
                item.PropertyChanged += OnItemPropertyChanged;
            }

            StatusMessage = "";
            UpdateCheckText = "";
            ApplyFilter();
            if (CanCheckUpdates && generation == _loadGeneration)
            {
                _ = RunUpdateCheckAsync(generation);
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsLoading = false;
            }
        }
    }

    private void OnItemPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LocalContentItemViewModel.IsSelected))
        {
            OnPropertyChanged(nameof(AllSelected));
            OnPropertyChanged(nameof(AllSelectText));
            OnPropertyChanged(nameof(SelectionText));
        }
        else if (e.PropertyName == nameof(LocalContentItemViewModel.IsUpdateAvailable))
        {
            OnPropertyChanged(nameof(UpdatableCountText));
            if (IsShowingUpdates)
            {
                ApplyFilter();
            }
        }
    }

    private void DetachItems()
    {
        foreach (var item in _allItems)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }
    }

    private void ApplyFilter()
    {
        var query = SearchText.Trim();
        IEnumerable<LocalContentItemViewModel> filtered = _allItems;
        if (IsShowingUpdates)
        {
            filtered = filtered.Where(item => item.IsUpdateAvailable);
        }

        if (query.Length > 0)
        {
            filtered = filtered.Where(item => item.Matches(query));
        }

        var sorted = SortItems(filtered);
        Items.Clear();
        foreach (var item in sorted)
        {
            Items.Add(item);
        }

        NotifyListState();
    }

    private IEnumerable<LocalContentItemViewModel> SortItems(IEnumerable<LocalContentItemViewModel> items)
    {
        // 有更新的资源始终排最前面（对齐 PCL-CE 的可更新置顶），再按所选维度排序
        var ordered = SelectedSort switch
        {
            SortByFileName => SortDescending
                ? items.OrderByDescending(item => item.FileName, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(item => item.FileName, StringComparer.OrdinalIgnoreCase),
            SortBySize => SortDescending
                ? items.OrderByDescending(item => item.File.Size)
                : items.OrderBy(item => item.File.Size),
            _ => SortDescending
                ? items.OrderByDescending(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
        };
        return ordered
            .ThenByDescending(item => item.IsUpdateAvailable)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase);
    }

    private void NotifyListState()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(AllCountText));
        OnPropertyChanged(nameof(UpdatableCountText));
        OnPropertyChanged(nameof(UpdatableCount));
        OnPropertyChanged(nameof(HasUpdatable));
        OnPropertyChanged(nameof(UpdateAllText));
        OnPropertyChanged(nameof(IsUpdating));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(AllSelectText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(CanCheckUpdates));
        OnPropertyChanged(nameof(ShowUpdateFilter));
        OnPropertyChanged(nameof(ShowDownloadEntry));
        OnPropertyChanged(nameof(ShowModRelations));
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnSelectedSortChanged(string value) => ApplyFilter();

    partial void OnSortDescendingChanged(bool value)
    {
        OnPropertyChanged(nameof(SortDirectionText));
        ApplyFilter();
    }

    partial void OnIsShowingUpdatesChanged(bool value) => ApplyFilter();

    /// <summary>
    /// 重新检测：清除本分区的洞察缓存后重扫（对齐 PCL-CE 的手动刷新=删除 LocalComp.json），
    /// 用于缓存里缺图标/来源不全时强制走一轮完整识别。
    /// </summary>
    [RelayCommand]
    private async Task RecheckAsync()
    {
        if (IsCheckingUpdates || _directory.Length == 0)
        {
            return;
        }

        try
        {
            var cacheKey = ContentInsightCache.MakeKey(_directory, _resourceType?.ToString() ?? "local");
            var file = Path.Combine(_services.Paths.CacheDirectory, "content-insight", cacheKey + ".json");
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 删不掉就让它自然过期，不强求
        }

        StatusMessage = "已清除缓存，正在重新检测…";
        await LoadAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private void ShowAll()
    {
        IsShowingUpdates = false;
    }

    [RelayCommand]
    private void ShowUpdates()
    {
        if (CanCheckUpdates)
        {
            IsShowingUpdates = true;
        }
    }

    [RelayCommand]
    private void SelectAll()
    {
        var select = !AllSelected;
        foreach (var item in Items)
        {
            item.IsSelected = select;
        }

        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(AllSelectText));
        OnPropertyChanged(nameof(SelectionText));
    }

    [RelayCommand]
    private void ToggleSortDirection()
    {
        SortDescending = !SortDescending;
    }

    [RelayCommand]
    private void DownloadNew()
    {
        if (_resourceType is { } type)
        {
            DownloadRequested?.Invoke(type);
        }
    }

    [RelayCommand]
    private async Task InstallFromFileAsync()
    {
        var picker = FilePicker;
        if (picker is null || _directory.Length == 0)
        {
            StatusMessage = "当前目录不可用，无法从文件安装。";
            return;
        }

        var picked = await picker(_directory).ConfigureAwait(true);
        if (picked is null)
        {
            return;
        }

        var source = Path.GetFullPath(picked);
        if (!File.Exists(source))
        {
            StatusMessage = "所选文件不存在，可能已被移动。";
            return;
        }

        var extension = Path.GetExtension(source);
        var allowed = _extensions
            .Select(item => item.StartsWith('.') ? item : $".{item}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(extension))
        {
            StatusMessage = $"文件类型不匹配：当前分区只接受 {string.Join(" / ", _extensions)}";
            return;
        }

        if (extension.Equals(".jar", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var archive = System.IO.Compression.ZipFile.OpenRead(source);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                           or InvalidDataException)
            {
                StatusMessage = $"文件校验失败（不是有效的压缩包）：{ex.Message}";
                return;
            }
        }

        var fileName = Path.GetFileName(source);
        var target = Path.Combine(_directory, fileName);
        if (File.Exists(target) || File.Exists(target + ".disabled"))
        {
            StatusMessage = $"已存在同名文件，未导入：{fileName}";
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(_directory);
            File.Copy(source, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"导入失败：{ex.Message}";
            _services.Logger.Error("从文件安装资源失败", ex);
            return;
        }

        StatusMessage = $"已导入：{fileName}";
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ExportInfoAsync()
    {
        var picker = SaveInfoPicker;
        if (picker is null)
        {
            StatusMessage = "当前平台不支持选择保存位置。";
            return;
        }

        var target = await picker($"{SectionTitle}-{DateTime.Now:yyyyMMdd-HHmm}.csv").ConfigureAwait(true);
        if (target is null)
        {
            return;
        }

        if (!target.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
        {
            target += ".csv";
        }

        var lines = new List<string> { "名称,版本,来源,文件名,路径,大小,是否启用,可更新" };
        foreach (var item in Items)
        {
            lines.Add(string.Join(',', new[]
            {
                Csv(item.Name),
                Csv(item.VersionText),
                Csv(item.SourceText),
                Csv(System.IO.Path.GetFileName(item.Path)),
                Csv(item.Path),
                Csv(InstanceContentService.FormatSize(item.File.Size)),
                Csv(item.IsEnabled ? "是" : "否"),
                Csv(item.UpdateText.Length > 0 ? item.UpdateText : "否")
            }));
        }

        try
        {
            await File.WriteAllTextAsync(
                    target,
                    string.Join(Environment.NewLine, lines),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: true))
                .ConfigureAwait(true);
            StatusMessage = $"已导出 {Items.Count} 项到 {target}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = $"导出信息失败：{ex.Message}";
            _services.Logger.Error("导出资源列表信息失败", ex);
        }
    }

    [RelayCommand]
    private void ShowRelations()
    {
        var entries = _allItems
            .Where(item => !item.IsDirectory && item.Metadata.ModId.Length > 0)
            .Select(item => (item.File, item.Metadata))
            .ToList();
        var lines = ModRelationAnalyzer.Analyze(entries);
        Relations.Clear();
        foreach (var line in lines
                     .OrderByDescending(item => item.IsProblem)
                     .ThenBy(item => item.ModName, StringComparer.OrdinalIgnoreCase))
        {
            Relations.Add(line);
        }

        var problems = lines.Count(item => item.IsProblem);
        RelationsSummary = entries.Count == 0
            ? "没有可解析的模组元数据（仅支持 Fabric / Forge / NeoForge 模组）。"
            : problems == 0
                ? $"共 {lines.Count} 条依赖关系，未发现缺失或冲突。"
                : $"共 {lines.Count} 条依赖关系，其中 {problems} 条缺失前置或冲突。";
        IsRelationsDialogVisible = true;
    }

    [RelayCommand]
    private void CloseModRelations()
    {
        IsRelationsDialogVisible = false;
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
            // 文件用「打开所在文件夹并定位」，而不是按关联程序打开（jar 无法关联且会误触发）
            _services.System.RevealFile(item.Path);
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

    private static string Csv(string value)
        => value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    /// <summary>
    /// 真实更新检测（带缓存）：计算 SHA-1 / CurseForge 指纹 → 未变动的文件直接复用 24h 内的本地缓存
    /// （不重复请求）→ 其余文件走 Modrinth 批量反查（已装版本 + 最新兼容版本）→ 未命中 Modrinth 的
    /// 文件走 CurseForge 指纹反查（含与当前版本兼容的最新文件）→ 识别到的项目批量取图标。
    /// 结果持久化到数据目录 cache/content-insight，重开页面秒显且省请求。
    /// </summary>
    private async Task RunUpdateCheckAsync(int generation)
    {
        var candidates = _allItems.Where(item => !item.IsDirectory).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        CancelUpdateCheck();
        var cts = new CancellationTokenSource();
        _updateCts = cts;
        var token = cts.Token;
        IsCheckingUpdates = true;
        UpdateCheckText = "正在计算文件指纹…";
        try
        {
            var hashed = await Task.Run(async () =>
            {
                var list = new List<(LocalContentItemViewModel Item, ModpackFileHashes Hash)>();
                foreach (var item in candidates)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        list.Add((item, await ModpackHash.ComputeAsync(item.Path, token).ConfigureAwait(false)));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 单个不可读文件跳过，不影响其余项
                    }
                }

                return list;
            }, token).ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            if (generation != _loadGeneration)
            {
                return;
            }

            // ① 缓存复用：路径/大小/修改时间/SHA-1 一致且 24h 内检查过的文件，直接回填结果
            var cacheKey = ContentInsightCache.MakeKey(_directory, _resourceType?.ToString() ?? "local");
            var cachedEntries = ContentInsightCache.Load(_services.Paths.CacheDirectory, cacheKey);
            var working = new Dictionary<string, LookupWorking>(StringComparer.OrdinalIgnoreCase);
            var reusedCount = 0;
            foreach (var (item, hash) in hashed)
            {
                // 只复用「已识别到项目」的缓存条目；未匹配的「本地」条目（旧版本崩溃时写下的）
                // 每次都重新走双源匹配——对齐 PCL-CE 的 CompLoaded 语义，网络恢复后可自动补识别
                var cached = cachedEntries.FirstOrDefault(entry =>
                    entry.Source != "本地"
                    && ContentInsightCache.MatchesFile(entry, item.Path, item.File.Size, item.File.Modified.Ticks, hash.Sha1)
                    && ContentInsightCache.IsFresh(entry));
                if (cached is null)
                {
                    continue;
                }

                reusedCount++;
                var state = FromEntry(cached);
                working[hash.Sha1] = state;
                ApplyLookup(item, state, cached.LatestVersionId.Length > 0 && cached.LatestVersionId != cached.InstalledVersionId);
            }

            // ② 网络检测（参考 PCL-CE 的双源并行）：Modrinth 与 CurseForge 同时查全部待检文件，
            //    任一来源失败不影响另一个；两边都命中的以 Modrinth 为准（更新定向更准），
            //    CurseForge 命中用于补来源/图标，并作为 Modrinth 缺图标时的兜底
            var pending = hashed.Where(entry => !working.ContainsKey(entry.Hash.Sha1)).ToList();
            Dictionary<string, CurseForgeFingerprintInfo>? cfBySha1ForIcons = null;
            if (pending.Count > 0)
            {
                UpdateCheckText = "正在通过 Modrinth 与 CurseForge 并行校验…";
                var modrinth = new ModrinthClient(_services.CompRequester);
                var curseForge = new CurseForgeClient(_services.CompRequester, _services.Logger);
                var sha1s = pending.Select(entry => entry.Hash.Sha1).ToList();
                var murmur2s = pending.Select(entry => entry.Hash.Murmur2).Distinct().ToList();

                var modrinthTask = Task.Run<(IReadOnlyDictionary<string, ModrinthHashMatch>? Installed, IReadOnlyDictionary<string, ModrinthLatestFile>? Latest)>(
                    async () =>
                    {
                        try
                        {
                            var installedTask = modrinth.LookupBySha1Async(sha1s, token);
                            var latestTask = modrinth.LookupLatestBySha1Async(sha1s, _gameVersion, UpdateLoaders(), token);
                            await Task.WhenAll(installedTask, latestTask).ConfigureAwait(false);
                            return (Installed: await installedTask.ConfigureAwait(false), Latest: await latestTask.ConfigureAwait(false));
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _services.Logger.Warn($"Modrinth 反查失败（该来源本轮跳过，由 CurseForge 补齐）：{ex.Message}");
                            return (Installed: null, Latest: null);
                        }
                    },
                    token);

                var curseForgeTask = Task.Run(
                    async () =>
                    {
                        try
                        {
                            return await curseForge
                                .LookupFingerprintInfosAsync(murmur2s, _gameVersion, token)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            _services.Logger.Warn($"CurseForge 指纹反查失败（该来源本轮跳过，由 Modrinth 补齐）：{ex.Message}");
                            return null;
                        }
                    },
                    token);
                await Task.WhenAll(modrinthTask, curseForgeTask).ConfigureAwait(true);
                token.ThrowIfCancellationRequested();
                if (generation != _loadGeneration)
                {
                    return;
                }

                var modrinthResult = modrinthTask.Result;
                var installed = modrinthResult.Installed;
                var latest = modrinthResult.Latest;
                // CurseForge 反查结果按 SHA-1 对齐（与 PCL-CE 的 group by hash 同款）：
                // fingerprints 响应的 exactMatches[].id 实测是 projectId 而非指纹，
                // 不能用 murmur2 对齐；BuildFile 已把命中的 sha1 写进 File.Hash
                var cfInfos = curseForgeTask.Result;
                var cfBySha1 = cfInfos is null
                    ? new Dictionary<string, CurseForgeFingerprintInfo>(StringComparer.OrdinalIgnoreCase)
                    : cfInfos
                        .Where(info => !string.IsNullOrEmpty(info.File.Hash))
                        .GroupBy(info => info.File.Hash!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                _services.Logger.Info($"[本地资源] 双源匹配完成：Modrinth 命中 {(installed?.Count ?? 0)}，CurseForge 命中 {(cfInfos?.Count ?? 0)} / 待检 {pending.Count}");
                // 图标兜底字典：Modrinth 命中但无图标的文件，用同文件的 CurseForge 记录补图标
                cfBySha1ForIcons = cfInfos is null
                    ? null
                    : cfInfos
                        .Where(info => !string.IsNullOrEmpty(info.File.Hash))
                        .GroupBy(info => info.File.Hash!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

                foreach (var (item, hash) in pending)
                {
                    if (installed is not null && installed.TryGetValue(hash.Sha1, out var match))
                    {
                        var state = new LookupWorking
                        {
                            Source = "Modrinth",
                            ProjectId = match.ProjectId,
                            InstalledVersionId = match.VersionId
                        };
                        var updateAvailable = false;
                        if (latest is not null && latest.TryGetValue(hash.Sha1, out var newest))
                        {
                            state.LatestVersionId = newest.VersionId;
                            state.LatestVersionName = newest.VersionNumber.Length > 0
                                ? newest.VersionNumber
                                : Path.GetFileNameWithoutExtension(newest.FileName);
                            state.LatestFileName = newest.FileName;
                            state.LatestDownloadUrl = newest.DownloadUrl;
                            state.LatestSha1 = newest.Sha1;
                            // 对齐 PCL-CE：版本不同且发布日期确实更新才计为可更新
                            //（装了比“最新”还新的测试版时不误报）
                            updateAvailable = newest.VersionId != match.VersionId
                                              && (match.DatePublished is null
                                                  || newest.DatePublished is null
                                                  || newest.DatePublished > match.DatePublished);
                        }

                        working[hash.Sha1] = state;
                        ApplyLookup(item, state, updateAvailable);
                    }
                    else if (cfBySha1.TryGetValue(hash.Sha1, out var info))
                    {
                        var state = new LookupWorking
                        {
                            Source = "CurseForge",
                            ProjectId = info.ProjectId.ToString(),
                            InstalledVersionId = info.File.Id,
                            FromCf = true
                        };
                        var updateAvailable = false;
                        if (info.LatestCompatible is { } newest
                            && newest.Id != info.File.Id
                            // 对齐 PCL-CE：发布日期必须真的更新，安装文件比“最新”还新时不误报
                            && (info.File.ReleaseDate is null
                                || newest.ReleaseDate is null
                                || newest.ReleaseDate > info.File.ReleaseDate))
                        {
                            state.LatestVersionId = newest.Id;
                            state.LatestVersionName = newest.DisplayName.Length > 0
                                ? newest.DisplayName
                                : Path.GetFileNameWithoutExtension(newest.FileName);
                            state.LatestFileName = newest.FileName;
                            state.LatestDownloadUrl = newest.DownloadUrls.FirstOrDefault() ?? "";
                            state.LatestSha1 = newest.Hash ?? "";
                            updateAvailable = true;
                        }

                        working[hash.Sha1] = state;
                        ApplyLookup(item, state, updateAvailable);
                    }
                }
            }

            // ③ 图标：识别到的项目批量取图标 URL（交给 HyclImage 的磁盘缓存加载）。
            //    缓存复用的条目若缺图标（旧版本缓存/图标阶段曾失败）也补一次——图标接口很轻
            var missingIconCount = working.Values.Count(state => state.IconUrl.Length == 0 && state.ProjectId.Length > 0);
            if (missingIconCount > 0)
            {
                try
                {
                    UpdateCheckText = "正在获取资源图标…";
                    var modrinthIds = working.Values
                        .Where(state => !state.FromCf && state.ProjectId.Length > 0)
                        .Select(state => state.ProjectId)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    var modrinthIcons = modrinthIds.Count > 0
                        ? await new ModrinthClient(_services.CompRequester).GetProjectIconUrlsAsync(modrinthIds, token).ConfigureAwait(true)
                        : new Dictionary<string, string>();
                    var cfIds = working.Values
                        .Where(state => state.FromCf && long.TryParse(state.ProjectId, out var id) && id > 0)
                        .Select(state => long.Parse(state.ProjectId))
                        .Distinct()
                        .ToList();
                    var cfIcons = cfIds.Count > 0
                        ? await new CurseForgeClient(_services.CompRequester, _services.Logger).GetModIconUrlsAsync(cfIds, token).ConfigureAwait(true)
                        : new Dictionary<long, string>();
                    foreach (var (item, hash) in hashed)
                    {
                        if (!working.TryGetValue(hash.Sha1, out var state) || state.IconUrl.Length > 0)
                        {
                            continue;
                        }

                        var icon = state.FromCf
                            ? (long.TryParse(state.ProjectId, out var id) && cfIcons.TryGetValue(id, out var cfIcon) ? cfIcon : "")
                            : (modrinthIcons.TryGetValue(state.ProjectId, out var mrIcon) ? mrIcon : "");
                        // Modrinth 命中但没有图标的文件：用同文件的 CurseForge 记录兜底
                        if (icon.Length == 0
                            && !state.FromCf
                            && cfBySha1ForIcons is not null
                            && cfBySha1ForIcons.TryGetValue(hash.Sha1, out var cfInfo)
                            && cfIcons.TryGetValue(cfInfo.ProjectId, out var fallbackIcon))
                        {
                            icon = fallbackIcon;
                        }

                        if (icon.Length > 0)
                        {
                            state.IconUrl = icon;
                            item.IconUrl = icon;
                        }
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 图标缺失只影响观感，不提示错误
                    _services.Logger.Warn($"获取资源图标失败（忽略）：{ex.Message}");
                }
            }

            // ④ 缓存落盘：全部文件（含缓存复用与图标回填后）都刷新检查时间，
            //    图标补齐的结果也一并持久化，下次打开直接秒显
            var entries = new List<ContentInsightEntry>(hashed.Count);
            foreach (var (item, hash) in hashed)
            {
                var state = working.TryGetValue(hash.Sha1, out var value)
                    ? value
                    : new LookupWorking();
                entries.Add(new ContentInsightEntry
                {
                    Path = item.Path,
                    Size = item.File.Size,
                    MTimeTicks = item.File.Modified.Ticks,
                    Sha1 = hash.Sha1,
                    Source = state.Source,
                    ProjectId = state.ProjectId,
                    IconUrl = state.IconUrl,
                    InstalledVersionId = state.InstalledVersionId,
                    LatestVersionId = state.LatestVersionId,
                    LatestVersionName = state.LatestVersionName,
                    LatestFileName = state.LatestFileName,
                    LatestDownloadUrl = state.LatestDownloadUrl,
                    LatestSha1 = state.LatestSha1,
                    IsFromCurseForge = state.FromCf,
                    CheckedAtTicks = DateTime.UtcNow.Ticks
                });
                item.Insight = entries[^1];
            }

            ContentInsightCache.Save(_services.Paths.CacheDirectory, cacheKey, entries);

            if (generation != _loadGeneration)
            {
                return;
            }

            var updatable = _allItems.Count(item => item.IsUpdateAvailable);
            OnPropertyChanged(nameof(UpdatableCountText));
            OnPropertyChanged(nameof(UpdatableCount));
            OnPropertyChanged(nameof(HasUpdatable));
            ApplyFilter();

            var networkCount = hashed.Count - reusedCount;
            UpdateCheckText = $"已校验 {hashed.Count} 个文件，{updatable} 个可更新"
                              + (reusedCount > 0 && networkCount == 0 ? "（缓存复用，无需请求）" : "")
                              + (updatable == 0 && networkCount > 0 ? "（未识别的本地资源不参与更新判断）" : "");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // 切换分区/刷新导致的取消：不提示错误
        }
        catch (Exception ex)
        {
            UpdateCheckText = $"更新检测失败：{ex.Message}";
            _services.Logger.Warn($"本地资源更新检测失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_updateCts, cts))
            {
                _updateCts = null;
                IsCheckingUpdates = false;
            }

            cts.Dispose();
        }
    }

    private sealed class LookupWorking
    {
        public string Source { get; set; } = "本地";
        public string ProjectId { get; set; } = "";
        public string IconUrl { get; set; } = "";
        public string InstalledVersionId { get; set; } = "";
        public string LatestVersionId { get; set; } = "";
        public string LatestVersionName { get; set; } = "";
        public string LatestFileName { get; set; } = "";
        public string LatestDownloadUrl { get; set; } = "";
        public string LatestSha1 { get; set; } = "";
        public bool FromCf { get; set; }
    }

    private static LookupWorking FromEntry(ContentInsightEntry entry) => new()
    {
        Source = entry.Source,
        ProjectId = entry.ProjectId,
        IconUrl = entry.IconUrl,
        InstalledVersionId = entry.InstalledVersionId,
        LatestVersionId = entry.LatestVersionId,
        LatestVersionName = entry.LatestVersionName,
        LatestFileName = entry.LatestFileName,
        LatestDownloadUrl = entry.LatestDownloadUrl,
        LatestSha1 = entry.LatestSha1,
        FromCf = entry.IsFromCurseForge
    };

    private static ContentInsightEntry ToEntry(LocalContentItemViewModel item, ModpackFileHashes hash, LookupWorking state)
        => new()
        {
            Path = item.Path,
            Size = item.File.Size,
            MTimeTicks = item.File.Modified.Ticks,
            Sha1 = hash.Sha1,
            Source = state.Source,
            ProjectId = state.ProjectId,
            IconUrl = state.IconUrl,
            InstalledVersionId = state.InstalledVersionId,
            LatestVersionId = state.LatestVersionId,
            LatestVersionName = state.LatestVersionName,
            LatestFileName = state.LatestFileName,
            LatestDownloadUrl = state.LatestDownloadUrl,
            LatestSha1 = state.LatestSha1,
            IsFromCurseForge = state.FromCf,
            CheckedAtTicks = DateTime.UtcNow.Ticks
        };

    private static void ApplyLookup(LocalContentItemViewModel item, LookupWorking state, bool updateAvailable)
    {
        item.SourceText = state.Source;
        if (state.IconUrl.Length > 0)
        {
            item.IconUrl = state.IconUrl;
        }

        item.IsUpdateAvailable = updateAvailable;
        item.LatestVersion = updateAvailable ? state.LatestVersionName : "";
    }

    /// <summary>
    /// 一键更新全部可更新资源：按缓存里记录的最新兼容版本逐个下载替换（复用全局下载线程数/
    /// 断点续传），旧文件名不同（版本号变化）时把旧文件移入回收站。单个失败不阻断其余更新。
    /// </summary>
    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        if (IsUpdating || IsCheckingUpdates)
        {
            return;
        }

        var targets = _allItems.Where(item => item.IsUpdateAvailable && item.IsEnabled && item.Insight is not null).ToList();
        if (targets.Count == 0)
        {
            StatusMessage = "没有可更新的资源";
            return;
        }

        IsUpdating = true;
        var gameDirectory = _gameDirectory.Length > 0 ? _gameDirectory : _services.Config.EffectiveGameDirectory;
        var updated = 0;
        var failed = 0;
        try
        {
            foreach (var item in targets)
            {
                var entry = item.Insight!;
                var latestFile = new CompFile
                {
                    ProjectId = entry.ProjectId,
                    Id = entry.LatestVersionId,
                    DisplayName = entry.LatestVersionName,
                    FileName = entry.LatestFileName,
                    Version = entry.LatestVersionName,
                    Hash = entry.LatestSha1.Length > 0 ? entry.LatestSha1 : null,
                    Size = 0,
                    DownloadUrls = entry.LatestDownloadUrl.Length > 0 ? [entry.LatestDownloadUrl] : [],
                    Type = _resourceType ?? CompResourceType.Mod,
                    FromCurseForge = entry.IsFromCurseForge
                };
                StatusMessage = $"正在更新 {item.Name} → {entry.LatestVersionName}…";
                try
                {
                    await _services.CompInstall
                        .InstallFileToDirectoryAsync(latestFile, _directory, null, CancellationToken.None)
                        .ConfigureAwait(true);
                    // 新版本文件名与旧文件不同（版本号变化）时，删除旧文件避免同 mod 双份共存
                    if (!Path.GetFileName(item.Path).Equals(latestFile.FileName, StringComparison.OrdinalIgnoreCase))
                    {
                        Services.RecycleBin.DeleteFile(item.Path, gameDirectory, item.FileName, _services.Logger);
                    }

                    updated++;
                }
                catch (Exception ex)
                {
                    failed++;
                    _services.Logger.Warn($"更新 {item.Name} 失败：{ex.Message}");
                    StatusMessage = $"更新 {item.Name} 失败：{ex.Message}";
                }
            }
        }
        finally
        {
            IsUpdating = false;
            StatusMessage = $"批量更新完成：成功 {updated} 个" + (failed > 0 ? $"，失败 {failed} 个" : "");
            await LoadAsync().ConfigureAwait(true);
        }
    }

    private IReadOnlyList<string>? UpdateLoaders()
    {
        if (_resourceType != CompResourceType.Mod)
        {
            return null;
        }

        return _loader switch
        {
            "Forge" or "Cleanroom" => ["forge"],
            "NeoForge" => ["neoforge"],
            "Fabric" or "Legacy Fabric" => ["fabric"],
            "Quilt" => ["quilt"],
            "LiteLoader" => ["liteloader"],
            _ => null
        };
    }

    private void CancelUpdateCheck()
    {
        var current = _updateCts;
        _updateCts = null;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已在其它路径释放
        }
    }
}

/// <summary>导出内容列表的一行：标题、补充信息与勾选状态（扁平树，用缩进表示层级）。</summary>
public sealed partial class ExportTreeItemViewModel : ObservableObject
{
    public ExportTreeItemViewModel(
        string title,
        string? detail = null,
        bool isChild = false,
        bool isChecked = true,
        bool isEnabled = true,
        string? packName = null,
        IReadOnlyList<ExportTreeItemViewModel>? children = null)
    {
        Title = title;
        Detail = detail;
        IsChild = isChild;
        _isChecked = isChecked;
        _isEnabled = isEnabled;
        PackName = packName;
        Children = children ?? [];
    }

    public string Title { get; }

    public string? Detail { get; }

    public bool HasDetail => !string.IsNullOrEmpty(Detail);

    public bool IsChild { get; }

    /// <summary>资源包条目对应的顶层文件名/文件夹名；其它行为 null。</summary>
    public string? PackName { get; }

    public IReadOnlyList<ExportTreeItemViewModel> Children { get; }

    [ObservableProperty]
    private bool _isChecked;

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsCheckedChanged(bool value)
    {
        // 分组（模组 / 资源包）勾选时联动子项：勾选状态与可编辑状态一起同步
        foreach (var child in Children)
        {
            child.IsEnabled = value;
            child.IsChecked = value;
        }
    }
}

/// <summary>
/// 导出实例为整合包：选择格式与内容 → 选择保存位置 → 在线反查 → 打包并校验。
/// 清单里能按项目 ID 还原的内容写入 files[]，其余内容原样进入 overrides。
/// </summary>
public sealed partial class InstanceExportViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string> _notify;
    private readonly List<ExportTreeItemViewModel> _resourcePackItems = [];
    private MinecraftInstance? _instance;
    private string _contentRoot = "";
    private CancellationTokenSource? _exportCts;
    private CancellationTokenSource? _previewCts;
    private ExportTreeItemViewModel? _modsGroup;
    private ExportTreeItemViewModel? _resourcePacksGroup;
    private ExportTreeItemViewModel? _packDataItem;
    private ExportTreeItemViewModel? _modConfigItem;
    private ExportTreeItemViewModel? _mapsItem;
    private ExportTreeItemViewModel? _jeiItem;
    private ExportTreeItemViewModel? _patchouliItem;
    private ExportTreeItemViewModel? _shaderGroup;
    private ExportTreeItemViewModel? _dataPackGroup;
    private ExportTreeItemViewModel? _savesGroup;
    private ExportTreeItemViewModel? _screenshotsGroup;
    private ExportTreeItemViewModel? _schematicsGroup;
    private ExportTreeItemViewModel? _serversGroup;

    public InstanceExportViewModel(LauncherServices services, Action<string> notify)
    {
        _services = services;
        _notify = notify;
    }

    /// <summary>由 UI 层注入的保存文件选择器（取消返回 null）。</summary>
    public Func<string?, Task<string?>>? SaveFilePicker { get; set; }

    public IReadOnlyList<string> FormatOptions { get; } =
    [
        "Modrinth（.mrpack）",
        "CurseForge（manifest.json）",
        "MultiMC / Prism（.zip）"
    ];

    [ObservableProperty]
    private string _selectedFormatOption = "Modrinth（.mrpack）";

    [ObservableProperty]
    private string _exportName = "";

    [ObservableProperty]
    private string _exportVersion = "1.0.0";

    /// <summary>游戏本体一行显示的版本与加载器信息，例如「正式版 1.21.6, NeoForge 26.0.1」。</summary>
    [ObservableProperty]
    private string _gameBodyText = "";

    /// <summary>「导出内容列表」的扁平化勾选树。</summary>
    public System.Collections.ObjectModel.ObservableCollection<ExportTreeItemViewModel> ExportTree { get; } = new();

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string _statusMessage = "";

    private ModpackExportFormat Format => SelectedFormatOption switch
    {
        "CurseForge（manifest.json）" => ModpackExportFormat.CurseForge,
        "MultiMC / Prism（.zip）" => ModpackExportFormat.MultiMc,
        _ => ModpackExportFormat.Modrinth
    };

    public string SuggestedFileName
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(ExportName) ? "instance" : ExportName.Trim();
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                name = name.Replace(invalid, '-');
            }

            name = name.Trim().TrimEnd('.');
            if (name.Length == 0)
            {
                name = "instance";
            }

            return $"{name}.{(Format == ModpackExportFormat.Modrinth ? "mrpack" : "zip")}";
        }
    }

    public void Configure(MinecraftInstance? instance, string contentRoot)
    {
        _instance = instance;
        _contentRoot = contentRoot;
        ExportName = instance is null ? "" : $"{instance.Id}-导出";
        StatusMessage = "";
        Progress = 0;
        ProgressText = "";
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        BuildExportTree(null);
        if (instance is not null)
        {
            _ = RefreshPreviewAsync();
        }
    }

    partial void OnSelectedFormatOptionChanged(string value)
        => OnPropertyChanged(nameof(SuggestedFileName));

    partial void OnExportNameChanged(string value)
        => OnPropertyChanged(nameof(SuggestedFileName));

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    private async Task RefreshPreviewAsync()
    {
        var instance = _instance;
        if (instance is null)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        try
        {
            var preview = await _services.ModpackExport
                .PreviewAsync(instance, _contentRoot, cts.Token)
                .ConfigureAwait(true);
            if (cts.IsCancellationRequested || !ReferenceEquals(_instance, instance))
            {
                return;
            }

            GameBodyText = preview.GameTitle;
            BuildExportTree(preview);
        }
        catch (OperationCanceledException)
        {
            // 切换到其它实例/分区：保留已有内容
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"读取导出预览失败：{ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_previewCts, cts))
            {
                _previewCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>构建「导出内容列表」：游戏本体 / 模组（含个人数据子项）/ 资源包（按文件）/ 其它分区。</summary>
    private void BuildExportTree(ModpackExportPreview? preview)
    {
        ExportTree.Clear();
        _resourcePackItems.Clear();
        _modsGroup = null;
        _resourcePacksGroup = null;

        if (_instance is null)
        {
            GameBodyText = "";
            return;
        }

        var gameTitle = preview?.GameTitle ?? GameBodyText;
        if (string.IsNullOrWhiteSpace(gameTitle))
        {
            gameTitle = _instance.Description;
        }

        // 游戏本体由清单声明，导出时不可去掉，因此勾选框固定勾选且不可编辑
        ExportTree.Add(new ExportTreeItemViewModel(
            "游戏本体",
            gameTitle,
            isChecked: true,
            isEnabled: false));

        var packDataItem = new ExportTreeItemViewModel(
            "整合包重要数据", "脚本、KubeJS、默认配置、数据包", isChild: true);
        var modConfigItem = new ExportTreeItemViewModel(
            "模组设置", "config 与 options.txt", isChild: true);
        var mapsItem = new ExportTreeItemViewModel(
            "已绘制的地图", "Xaero / JourneyMap / MapWriter", isChild: true);
        var jeiItem = new ExportTreeItemViewModel(
            "JEI 个人信息", "config/jei", isChild: true);
        var patchouliItem = new ExportTreeItemViewModel(
            "帕秋莉手册个人信息", "patchouli_books", isChild: true);
        _packDataItem = packDataItem;
        _modConfigItem = modConfigItem;
        _mapsItem = mapsItem;
        _jeiItem = jeiItem;
        _patchouliItem = patchouliItem;

        var modsGroup = new ExportTreeItemViewModel(
            "模组",
            preview is null ? "本实例的全部模组" : $"{preview.ModCount} 个文件",
            children:
            [
                packDataItem,
                modConfigItem,
                mapsItem,
                jeiItem,
                patchouliItem
            ]);
        _modsGroup = modsGroup;
        ExportTree.Add(modsGroup);
        foreach (var child in modsGroup.Children)
        {
            ExportTree.Add(child);
        }

        var packChildren = new List<ExportTreeItemViewModel>();
        if (preview is not null)
        {
            foreach (var pack in preview.ResourcePacks)
            {
                var detail = pack.IsDirectory ? $"文件夹 · {pack.FileCount} 个文件" : "资源包文件";
                var item = new ExportTreeItemViewModel(
                    pack.Name, detail, isChild: true, packName: pack.Name);
                packChildren.Add(item);
                _resourcePackItems.Add(item);
            }
        }

        var resourcePacksGroup = new ExportTreeItemViewModel(
            "资源包",
            packChildren.Count == 0 ? "没有资源包" : $"{packChildren.Count} 项",
            isEnabled: packChildren.Count > 0,
            children: packChildren);
        _resourcePacksGroup = resourcePacksGroup;
        ExportTree.Add(resourcePacksGroup);
        foreach (var child in packChildren)
        {
            ExportTree.Add(child);
        }

        _shaderGroup = AddSimpleGroup("光影包", "shaderpacks", isChecked: true);
        _dataPackGroup = AddSimpleGroup("数据包", "存档内的 datapacks", isChecked: true);
        _savesGroup = AddSimpleGroup("存档", "saves 下的全部世界", isChecked: false);
        _screenshotsGroup = AddSimpleGroup("截图", "screenshots", isChecked: false);
        _schematicsGroup = AddSimpleGroup("投影原理图", "schematics", isChecked: false);
        _serversGroup = AddSimpleGroup("服务器列表", "servers.dat", isChecked: false);
    }

    private ExportTreeItemViewModel AddSimpleGroup(string title, string detail, bool isChecked)
    {
        var item = new ExportTreeItemViewModel(title, detail, isChecked: isChecked);
        ExportTree.Add(item);
        return item;
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        if (_instance is null || IsExporting)
        {
            return;
        }

        var name = ExportName.Trim();
        if (name.Length == 0)
        {
            StatusMessage = "请填写整合包名称。";
            _notify(StatusMessage);
            return;
        }

        if (InstanceNameValidator.ValidateName(name) is not null)
        {
            StatusMessage = "整合包名称包含非法字符，请修改后重试。";
            _notify(StatusMessage);
            return;
        }

        var version = ExportVersion.Trim();
        if (version.Length == 0)
        {
            StatusMessage = "请填写整合包版本。";
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

        var extension = Format == ModpackExportFormat.Modrinth ? ".mrpack" : ".zip";
        if (!target.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            target += extension;
        }

        var author = string.IsNullOrWhiteSpace(_services.Config.UserName)
            ? "Hycl"
            : _services.Config.UserName;
        var modsChecked = _modsGroup?.IsChecked ?? true;
        var resourcePacksChecked = _resourcePacksGroup?.IsChecked ?? true;
        var options = new ModpackExportOptions
        {
            Format = Format,
            Name = name,
            Version = version,
            Author = author,
            IncludeMods = modsChecked,
            IncludePackData = modsChecked && (_packDataItem?.IsChecked ?? true),
            IncludeModConfig = modsChecked && (_modConfigItem?.IsChecked ?? true),
            IncludeMaps = modsChecked && (_mapsItem?.IsChecked ?? true),
            IncludeJei = modsChecked && (_jeiItem?.IsChecked ?? true),
            IncludePatchouli = modsChecked && (_patchouliItem?.IsChecked ?? true),
            IncludeResourcePacks = resourcePacksChecked,
            SelectedResourcePacks = resourcePacksChecked
                ? new HashSet<string>(
                    _resourcePackItems.Where(item => item.IsChecked).Select(item => item.PackName!),
                    StringComparer.OrdinalIgnoreCase)
                : null,
            IncludeShaderPacks = _shaderGroup?.IsChecked ?? true,
            IncludeDataPacks = _dataPackGroup?.IsChecked ?? true,
            IncludeSaves = _savesGroup?.IsChecked ?? false,
            IncludeScreenshots = _screenshotsGroup?.IsChecked ?? false,
            IncludeSchematics = _schematicsGroup?.IsChecked ?? false,
            IncludeServers = _serversGroup?.IsChecked ?? false
        };

        IsExporting = true;
        Progress = 0;
        ProgressText = "准备导出…";
        StatusMessage = "";
        _exportCts = new CancellationTokenSource();
        try
        {
            var report = new Progress<ModpackExportProgress>(value =>
            {
                Progress = value.Percent;
                ProgressText = value.Stage;
            });
            var result = await _services.ModpackExport
                .ExportAsync(_instance, _contentRoot, target, options, report, _exportCts.Token)
                .ConfigureAwait(true);
            Progress = 100;
            ProgressText = "导出完成";
            StatusMessage = $"已导出：{result.OutputPath}（清单 {result.ManifestFileCount} 项，overrides {result.OverrideFileCount} 项）"
                            + (result.Warning is null ? "" : $" {result.Warning}");
            _notify($"整合包已导出：{Path.GetFileName(result.OutputPath)}");
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "已取消导出。";
            ProgressText = "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or CompException)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            ProgressText = "";
            _services.Logger.Error("导出整合包失败", ex);
            _notify(StatusMessage);
        }
        finally
        {
            _exportCts?.Dispose();
            _exportCts = null;
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
