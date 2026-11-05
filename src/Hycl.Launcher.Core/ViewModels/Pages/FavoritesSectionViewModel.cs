using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game.Comp;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>收藏夹中的一条记录（含本地保存的项目快照）。</summary>
public sealed partial class FavoriteItemViewModel : ObservableObject
{
    private readonly FavoriteEntry _entry;

    public FavoriteItemViewModel(FavoriteEntry entry)
    {
        _entry = entry;
        var project = entry.Project;
        Title = CompLocalization.DisplayTitle(project);
        Description = CompLocalization.DisplayDescription(project);
        Author = CompLocalization.DisplayAuthor(project);
        IconUrl = project.IconUrl;
        SourceText = project.FromCurseForge ? "CurseForge" : "Modrinth";
        TypeText = CompLocalization.TypeName(project.Type);
        SavedText = $"收藏于 {entry.SavedAt.LocalDateTime:yyyy-MM-dd}";
    }

    public FavoriteEntry Entry => _entry;

    public CompProject Project => _entry.Project;

    public string Title { get; }

    public string Description { get; }

    public string Author { get; }

    public string? IconUrl { get; }

    public string SourceText { get; }

    public string TypeText { get; }

    public string SavedText { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// 收藏夹分区：六类资源统一列表、离线可用、可单条移除与批量移除。
/// 数据来自本地 favorites.json，打开项目时切回对应社区分区加载版本文件。
/// </summary>
public partial class FavoritesSectionViewModel : ObservableObject
{
    public static readonly IReadOnlyList<string> FilterOptions =
        ["全部", "模组", "整合包", "资源包", "光影", "数据包", "世界"];

    private readonly FavoritesStore _store;
    private readonly Action<CompProject, CompResourceType> _open;
    private readonly Action<string>? _toast;

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

    public string Description => "收藏的项目保存在本地，断网也能查看；点击「打开」可查看版本并安装。";

    public ObservableCollection<FavoriteItemViewModel> Items { get; } = [];

    [ObservableProperty]
    private string _selectedFilter = "全部";

    [ObservableProperty]
    private bool _isEmpty = true;

    [ObservableProperty]
    private string _emptyText = "收藏夹为空。在「社区资源」的项目详情里点「收藏」即可加入。";

    [ObservableProperty]
    private string _summaryText = "";

    /// <summary>已勾选的条目数（批量移除按钮是否可用）。</summary>
    [ObservableProperty]
    private int _checkedCount;

    public bool HasChecked => CheckedCount > 0;

    public string RemoveCheckedText => CheckedCount == 0 ? "移除选中" : $"移除选中（{CheckedCount}）";

    partial void OnSelectedFilterChanged(string value) => Reload();

    partial void OnCheckedCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasChecked));
        OnPropertyChanged(nameof(RemoveCheckedText));
    }

    partial void OnIsEmptyChanged(bool value) => OnPropertyChanged(nameof(HasItems));

    public bool HasItems => !IsEmpty;

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

        await _store.RemoveAsync([item.Entry]).ConfigureAwait(true);
        _toast?.Invoke($"已从收藏夹移除：{item.Title}");
    }

    [RelayCommand]
    private async Task RemoveCheckedAsync()
    {
        var checkedItems = Items.Where(item => item.IsChecked).ToList();
        if (checkedItems.Count == 0)
        {
            return;
        }

        await _store.RemoveAsync(checkedItems.Select(item => item.Entry)).ConfigureAwait(true);
        _toast?.Invoke($"已移除 {checkedItems.Count} 个收藏");
    }

    private void Reload()
    {
        foreach (var item in Items)
        {
            item.PropertyChanged -= OnItemPropertyChanged;
        }

        Items.Clear();
        var filterType = FilterOptions.ToList().IndexOf(SelectedFilter);
        foreach (var entry in _store.Items.OrderByDescending(item => item.SavedAt))
        {
            if (filterType > 0 && (int)entry.Project.Type != FilterTypeIndexToValue(filterType))
            {
                continue;
            }

            var item = new FavoriteItemViewModel(entry);
            item.PropertyChanged += OnItemPropertyChanged;
            Items.Add(item);
        }

        IsEmpty = Items.Count == 0;
        EmptyText = _store.IsEmpty
            ? "收藏夹为空。在「社区资源」的项目详情里点「收藏」即可加入。"
            : "当前筛选下没有收藏项目。";
        SummaryText = SelectedFilter == "全部"
            ? $"共 {Items.Count} 个收藏"
            : $"{SelectedFilter}：{Items.Count} 个收藏";
        UpdateCheckedState();
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
