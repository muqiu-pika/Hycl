using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game.Comp;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>收藏夹中的一条记录（含本地保存的项目快照与备注）。</summary>
public sealed partial class FavoriteItemViewModel : ObservableObject
{
    private readonly FavoriteItem _item;

    public FavoriteItemViewModel(FavoriteItem item, string folderId)
    {
        _item = item;
        FolderId = folderId;
        var project = item.Project;
        Title = CompLocalization.DisplayTitle(project);
        Description = CompLocalization.DisplayDescription(project);
        Author = CompLocalization.DisplayAuthor(project);
        IconUrl = project.IconUrl;
        SourceText = project.FromCurseForge ? "CurseForge" : "Modrinth";
        TypeText = CompLocalization.TypeName(project.Type);
        SavedText = item.SavedAt == DateTimeOffset.MinValue
            ? "收藏于 未知"
            : $"收藏于 {item.SavedAt.LocalDateTime:yyyy-MM-dd}";
        Note = item.Note;
    }

    public FavoriteItem Item => _item;

    public CompProject Project => _item.Project;

    public string FolderId { get; }

    public string Title { get; }

    public string Description { get; }

    public string Author { get; }

    public string? IconUrl { get; }

    public string SourceText { get; }

    public string TypeText { get; }

    public string SavedText { get; }

    [ObservableProperty]
    private string _note;

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// 收藏夹分区（对应 PCL-CE 收藏夹页）：按命名收藏夹分组浏览，可新建 / 改名 / 删除收藏夹，
/// 可给条目加备注、取消收藏；数据来自本地 favorites.json，断网也能显示。
/// </summary>
public partial class FavoritesSectionViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> FilterOptions =
        ["全部", "模组", "整合包", "资源包", "光影", "数据包", "世界"];

    private readonly FavoritesStore _store;
    private readonly Action<CompProject, CompResourceType> _open;
    private readonly Action<string>? _toast;
    private bool _isReloading;

    public FavoritesSectionViewModel(
        FavoritesStore store,
        Action<CompProject, CompResourceType> open,
        Action<string>? toast)
    {
        _store = store;
        _open = open;
        _toast = toast;
        _store.Changed += (_, _) => Reload();
        Reload();
    }

    public string Title => "收藏夹";

    public string IconKey => "star";

    public string Description => "收藏夹保存在本地，断网也能查看；按收藏夹分组管理，可备注、改名或删除。";

    public ObservableCollection<FavoriteItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private IReadOnlyList<string> _folderNames = [];

    [ObservableProperty]
    private string _selectedFolderName = "";

    [ObservableProperty]
    private string _selectedFilter = "全部";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyText = "收藏夹为空。在「社区资源」的项目详情里点「收藏」即可加入。";

    [ObservableProperty]
    private string _summaryText = "";

    [ObservableProperty]
    private bool _isFolderDialogVisible;

    [ObservableProperty]
    private string _folderDialogTitle = "";

    [ObservableProperty]
    private string _folderDialogText = "";

    private string? _renamingFolderId;

    /// <summary>已勾选的条目数（批量移除按钮是否可用）。</summary>
    [ObservableProperty]
    private int _checkedCount;

    public bool HasChecked => CheckedCount > 0;

    public string RemoveCheckedText => CheckedCount == 0 ? "移除选中" : $"移除选中（{CheckedCount}）";

    public bool CanDeleteFolder => _store.Folders.Count > 1;

    partial void OnSelectedFolderNameChanged(string value)
    {
        if (!_isReloading)
        {
            Reload();
        }
    }

    partial void OnSelectedFilterChanged(string value) => Reload();

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(RemoveCheckedText));
    }

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(HasItems));

    public bool HasItems => !IsEmpty;

    private FavoriteFolder CurrentFolder =>
        _store.Folders.FirstOrDefault(folder => folder.Name == SelectedFolderName) ?? _store.Folders[0];

    /// <summary>只读本地文件重新载入（离线刷新，不发起网络请求）。</summary>
    [RelayCommand]
    public void Refresh() => Reload();

    [RelayCommand]
    private void Open(FavoriteItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        _open(item.Project, item.Project.Type);
    }

    [RelayCommand]
    private async Task RemoveAsync(FavoriteItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await _store.RemoveAsync(item.FolderId, [item.Project.Id]).ConfigureAwait(true);
        _toast?.Invoke($"已从「{SelectedFolderName}」移除：{item.Title}");
    }

    [RelayCommand]
    private async Task RemoveCheckedAsync()
    {
        var checkedItems = Items.Where(item => item.IsChecked).ToList();
        if (checkedItems.Count == 0)
        {
            return;
        }

        await _store.RemoveAsync(CurrentFolder.Id, checkedItems.Select(item => item.Project.Id)).ConfigureAwait(true);
        _toast?.Invoke($"已移除 {checkedItems.Count} 个收藏");
    }

    [RelayCommand]
    private async Task SaveNoteAsync(FavoriteItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        await _store.SetNoteAsync(item.FolderId, item.Project.Id, item.Note).ConfigureAwait(true);
        _toast?.Invoke(item.Note.Trim().Length == 0 ? "已清除备注" : "备注已保存");
    }

    [RelayCommand]
    private void BeginCreateFolder()
    {
        _renamingFolderId = null;
        FolderDialogTitle = "新建收藏夹";
        FolderDialogText = "";
        IsFolderDialogVisible = true;
    }

    [RelayCommand]
    private void BeginRenameFolder()
    {
        _renamingFolderId = CurrentFolder.Id;
        FolderDialogTitle = "重命名收藏夹";
        FolderDialogText = CurrentFolder.Name;
        IsFolderDialogVisible = true;
    }

    [RelayCommand]
    private async Task CommitFolderDialogAsync()
    {
        var name = FolderDialogText.Trim();
        if (name.Length == 0)
        {
            _toast?.Invoke("收藏夹名称不能为空");
            return;
        }

        if (_renamingFolderId is not null)
        {
            await _store.RenameFolderAsync(_renamingFolderId, name).ConfigureAwait(true);
            _isReloading = true;
            SelectedFolderName = _store.GetFolder(_renamingFolderId).Name;
            _isReloading = false;
        }
        else
        {
            var folder = await _store.CreateFolderAsync(name).ConfigureAwait(true);
            _isReloading = true;
            SelectedFolderName = folder.Name;
            _isReloading = false;
        }

        IsFolderDialogVisible = false;
        Reload();
    }

    [RelayCommand]
    private void CancelFolderDialog() => IsFolderDialogVisible = false;

    [RelayCommand]
    private async Task DeleteFolderAsync()
    {
        var folder = CurrentFolder;
        var removed = await _store.RemoveFolderAsync(folder.Id).ConfigureAwait(true);
        if (!removed)
        {
            _toast?.Invoke("至少要保留一个收藏夹");
            return;
        }

        _toast?.Invoke($"已删除收藏夹：{folder.Name}");
        Reload();
    }

    private void Reload()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();

        var names = _store.Folders.Select(folder => folder.Name).ToList();
        FolderNames = names;
        if (!names.Contains(SelectedFolderName))
        {
            _isReloading = true;
            SelectedFolderName = names.FirstOrDefault() ?? FavoritesStore.DefaultFolderName;
            _isReloading = false;
        }

        var folder = CurrentFolder;
        var filterType = FilterOptions.ToList().IndexOf(SelectedFilter);
        foreach (var item in _store.GetItems(folder.Id).OrderByDescending(item => item.SavedAt))
        {
            if (filterType > 0 && (int)item.Project.Type != FilterTypeIndexToValue(filterType))
            {
                continue;
            }

            var viewModel = new FavoriteItemViewModel(item, folder.Id);
            viewModel.PropertyChanged += OnItemPropertyChanged;
            Items.Add(viewModel);
        }

        IsEmpty = Items.Count == 0;
        EmptyText = _store.IsEmpty
            ? "收藏夹为空。在「社区资源」的项目详情里点「收藏」即可加入。"
            : SelectedFilter == "全部"
                ? $"「{folder.Name}」还没有收藏项目。"
                : $"「{folder.Name}」在当前筛选下没有收藏项目。";
        SummaryText = SelectedFilter == "全部"
            ? $"「{folder.Name}」共 {Items.Count} 个收藏"
            : $"「{folder.Name}」· {SelectedFilter}：{Items.Count} 个收藏";
        UpdateCheckedState();
        OnPropertyChanged(nameof(CanDeleteFolder));
    }

    private static int FilterTypeIndexToValue(int filterIndex) => filterIndex switch
    {
        1 => (int)CompResourceType.Mod,
        2 => (int)CompResourceType.ModPack,
        3 => (int)CompResourceType.ResourcePack,
        4 => (int)CompResourceType.Shader,
        5 => (int)CompResourceType.DataPack,
        6 => (int)CompResourceType.World,
        _ => -1
    };

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FavoriteItemViewModel.IsChecked))
        {
            UpdateCheckedState();
        }
    }

    private void UpdateCheckedState()
    {
        CheckedCount = Items.Count(item => item.IsChecked);
    }
}
