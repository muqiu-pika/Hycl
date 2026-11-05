using System.Text.Json;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>收藏夹条目：保存项目的完整快照，离线也能直接展示。</summary>
public sealed record FavoriteEntry(CompProject Project, DateTimeOffset SavedAt);

/// <summary>
/// 本地收藏夹（对应 PCL-CE CompFavorites，但改为保存完整项目信息而不是仅 Id）：
/// 写入 %APPDATA%\Hycl\favorites.json，读取/展示完全离线，不依赖 CurseForge / Modrinth 接口。
/// </summary>
public sealed class FavoritesStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly string _filePath;
    private readonly LauncherLogger _logger;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private readonly List<FavoriteEntry> _items = [];

    public FavoritesStore(string filePath, LauncherLogger logger)
    {
        _filePath = filePath;
        _logger = logger;
        Load();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<FavoriteEntry> Items => _items;

    public bool IsEmpty => _items.Count == 0;

    private static string Key(CompProject project) => $"{project.FromCurseForge}:{project.Id}";

    public bool Contains(CompProject project) =>
        _items.Any(item => Key(item.Project).Equals(Key(project), StringComparison.OrdinalIgnoreCase));

    /// <summary>加入 / 取消收藏；项目类型以当前分区为准（Modrinth 数据包会被识别成模组）。</summary>
    public async Task<bool> ToggleAsync(CompProject project, CompResourceType type)
    {
        var key = Key(project);
        var existing = _items.FirstOrDefault(item =>
            Key(item.Project).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            _items.Remove(existing);
            await SaveAsync().ConfigureAwait(false);
            return false;
        }

        _items.Add(new FavoriteEntry(project with { Type = type }, DateTimeOffset.Now));
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    public async Task RemoveAsync(IEnumerable<FavoriteEntry> entries)
    {
        var keys = entries.Select(entry => Key(entry.Project)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removed = _items.RemoveAll(item => keys.Contains(Key(item.Project)));
        if (removed == 0)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(false);
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
            _items.Clear();
            if (saved?.Items is null)
            {
                return;
            }

            foreach (var entry in saved.Items)
            {
                if (entry?.Project is null || string.IsNullOrWhiteSpace(entry.Project.Id))
                {
                    continue;
                }

                _items.Add(entry);
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Error("读取收藏夹失败，已按空收藏夹继续", ex);
            _items.Clear();
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath) ?? ".");
            var tempFile = _filePath + ".tmp";
            var json = JsonSerializer.Serialize(new SaveModel { Items = _items.ToList() }, SerializerOptions);
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
        public List<FavoriteEntry>? Items { get; set; }
    }
}
