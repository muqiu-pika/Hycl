using System.Text.Json;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>收藏夹条目：保存项目的完整快照，离线也能直接展示。</summary>
public sealed record FavoriteEntry(CompProject Project, DateTimeOffset SavedAt);

/// <summary>
/// 命名收藏夹（对应 PCL-CE CompFavorites 的 FavData）：Favs 以项目 Id 为主，Notes 按 Id 记录备注。
/// </summary>
public sealed class FavoriteFolder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public List<string> Favs { get; set; } = [];

    public Dictionary<string, string> Notes { get; set; } = [];
}

/// <summary>收藏夹内的一条展示数据：快照 + 备注。</summary>
public sealed record FavoriteItem(CompProject Project, string Note, DateTimeOffset SavedAt);

/// <summary>
/// 本地收藏夹（对应 PCL-CE CompFavorites）：支持多个命名收藏夹、每个项目可加备注；
/// 项目本身仍保留完整快照，断网时也能展示标题/图标/作者。
/// 写入 %APPDATA%\Hycl\favorites.json。
/// </summary>
public sealed class FavoritesStore
{
    public const string DefaultFolderName = "默认收藏夹";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly LauncherLogger _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly List<FavoriteFolder> _folders = [];
    private readonly Dictionary<string, FavoriteEntry> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public FavoritesStore(string filePath, LauncherLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
        EnsureDefaultFolder();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<FavoriteFolder> Folders => _folders;

    public bool IsEmpty => _folders.All(folder => folder.Favs.Count == 0);

    public FavoriteFolder? FindFolder(string folderId) =>
        _folders.FirstOrDefault(folder => folder.Id == folderId);

    public FavoriteFolder GetFolder(string folderId) =>
        FindFolder(folderId) ?? EnsureDefaultFolder();

    public FavoriteEntry? FindSnapshot(string projectId) =>
        string.IsNullOrWhiteSpace(projectId) ? null : _snapshots.GetValueOrDefault(projectId);

    public bool Contains(string projectId) =>
        _folders.Any(folder => folder.Favs.Contains(projectId, StringComparer.OrdinalIgnoreCase));

    public bool ContainsInFolder(string folderId, string projectId) =>
        GetFolder(folderId).Favs.Contains(projectId, StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> FolderIdsContaining(string projectId) =>
        _folders.Where(folder => folder.Favs.Contains(projectId, StringComparer.OrdinalIgnoreCase))
            .Select(folder => folder.Id)
            .ToList();

    public string GetNote(string folderId, string projectId) =>
        GetFolder(folderId).Notes.GetValueOrDefault(projectId) ?? "";

    /// <summary>收藏夹内条目：快照齐全时直接展示；仅存 Id 的旧数据给出占位项目。</summary>
    public IReadOnlyList<FavoriteItem> GetItems(string folderId)
    {
        var folder = GetFolder(folderId);
        var items = new List<FavoriteItem>(folder.Favs.Count);
        foreach (var id in folder.Favs)
        {
            var snapshot = FindSnapshot(id);
            var project = snapshot?.Project ?? new CompProject { Id = id, Title = id };
            items.Add(new FavoriteItem(
                project,
                folder.Notes.GetValueOrDefault(id) ?? "",
                snapshot?.SavedAt ?? DateTimeOffset.MinValue));
        }

        return items;
    }

    /// <summary>加入 / 取消收藏；返回 true 表示本次加入，false 表示本次移除。</summary>
    public async Task<bool> ToggleAsync(string folderId, CompProject project, CompResourceType type)
    {
        var folder = GetFolder(folderId);
        if (folder.Favs.Contains(project.Id, StringComparer.OrdinalIgnoreCase))
        {
            folder.Favs.RemoveAll(id => id.Equals(project.Id, StringComparison.OrdinalIgnoreCase));
            folder.Notes.Remove(project.Id);
            await SaveAsync().ConfigureAwait(false);
            return false;
        }

        folder.Favs.Add(project.Id);
        _snapshots[project.Id] = new FavoriteEntry(project with { Type = type }, DateTimeOffset.Now);
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>从指定收藏夹移除条目（保留快照，其它收藏夹不受影响）。</summary>
    public async Task RemoveAsync(string folderId, IEnumerable<string> projectIds)
    {
        var folder = GetFolder(folderId);
        var removed = folder.Favs.RemoveAll(id =>
            projectIds.Any(target => target.Equals(id, StringComparison.OrdinalIgnoreCase)));
        if (removed == 0)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(false);
    }

    public async Task SetNoteAsync(string folderId, string projectId, string note)
    {
        var folder = GetFolder(folderId);
        var trimmed = note.Trim();
        if (trimmed.Length == 0)
        {
            folder.Notes.Remove(projectId);
        }
        else
        {
            folder.Notes[projectId] = trimmed;
        }

        await SaveAsync().ConfigureAwait(false);
    }

    public async Task<FavoriteFolder> CreateFolderAsync(string name)
    {
        var folder = new FavoriteFolder { Name = UniqueName(name) };
        _folders.Add(folder);
        await SaveAsync().ConfigureAwait(false);
        return folder;
    }

    public async Task RenameFolderAsync(string folderId, string name)
    {
        var folder = FindFolder(folderId);
        if (folder is null)
        {
            return;
        }

        folder.Name = UniqueName(name, folder.Id);
        await SaveAsync().ConfigureAwait(false);
    }

    /// <summary>删除收藏夹；与 PCL-CE 一致，至少保留一个收藏夹。</summary>
    public async Task<bool> RemoveFolderAsync(string folderId)
    {
        if (_folders.Count <= 1)
        {
            return false;
        }

        var removed = _folders.RemoveAll(folder => folder.Id == folderId);
        if (removed == 0)
        {
            return false;
        }

        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    private FavoriteFolder EnsureDefaultFolder()
    {
        if (_folders.Count > 0)
        {
            return _folders[0];
        }

        var folder = new FavoriteFolder { Name = DefaultFolderName };
        _folders.Add(folder);
        return folder;
    }

    /// <summary>收藏夹名保持唯一，重名时追加序号（下拉框按名称选择，需要可区分）。</summary>
    private string UniqueName(string name, string? exceptId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? DefaultFolderName : name.Trim();
        var candidate = baseName;
        var index = 2;
        while (_folders.Any(folder => folder.Id != exceptId && folder.Name == candidate))
        {
            candidate = $"{baseName} ({index++})";
        }

        return candidate;
    }

    private void Load()
    {
        if (!File.Exists(_filePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var saved = JsonSerializer.Deserialize<SaveModel>(json, SerializerOptions);
            _folders.Clear();
            _snapshots.Clear();

            if (saved?.Folders is { Count: > 0 })
            {
                foreach (var folder in saved.Folders)
                {
                    if (folder is null)
                    {
                        continue;
                    }

                    folder.Id = string.IsNullOrWhiteSpace(folder.Id) ? Guid.NewGuid().ToString("N") : folder.Id;
                    folder.Name = string.IsNullOrWhiteSpace(folder.Name) ? DefaultFolderName : folder.Name;
                    folder.Favs = folder.Favs.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
                    folder.Notes ??= [];
                    _folders.Add(folder);
                }

                foreach (var (id, entry) in saved.Snapshots ?? [])
                {
                    if (entry?.Project is null || string.IsNullOrWhiteSpace(entry.Project.Id))
                    {
                        continue;
                    }

                    _snapshots[entry.Project.Id] = entry;
                }

                return;
            }

            // 兼容旧格式（单列表 + 完整快照）：迁移到默认收藏夹，快照全部保留。
            var migratedFolder = new FavoriteFolder { Name = DefaultFolderName };
            foreach (var entry in saved?.Items ?? [])
            {
                if (entry?.Project is null || string.IsNullOrWhiteSpace(entry.Project.Id))
                {
                    continue;
                }

                if (!migratedFolder.Favs.Contains(entry.Project.Id))
                {
                    migratedFolder.Favs.Add(entry.Project.Id);
                }

                _snapshots[entry.Project.Id] = entry;
            }

            _folders.Add(migratedFolder);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Error("读取收藏夹失败，已按空收藏夹继续", ex);
            _folders.Clear();
            _snapshots.Clear();
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
            var tempFile = _filePath + ".tmp";
            var model = new SaveModel
            {
                Folders = _folders.ToList(),
                Snapshots = _snapshots.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            };
            var json = JsonSerializer.Serialize(model, SerializerOptions);
            await File.WriteAllTextAsync(tempFile, json).ConfigureAwait(false);
            File.Move(tempFile, _filePath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error("保存收藏夹失败", ex);
        }
        finally
        {
            _saveLock.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private sealed class SaveModel
    {
        public List<FavoriteFolder>? Folders { get; set; }

        public Dictionary<string, FavoriteEntry>? Snapshots { get; set; }

        /// <summary>旧版单列表格式，仅用于迁移。</summary>
        public List<FavoriteEntry>? Items { get; set; }
    }
}
