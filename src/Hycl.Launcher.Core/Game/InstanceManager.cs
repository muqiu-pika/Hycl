using CommunityToolkit.Mvvm.ComponentModel;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Services;
using System.Text.Json.Nodes;

namespace Hycl.Launcher.Core.Game;

/// <summary>已安装的版本实例。</summary>
public sealed partial class MinecraftInstance : ObservableObject
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string GameVersion { get; init; } = "";

    public string Loader { get; init; } = "原版";

    public string Type { get; init; } = "release";

    public string Directory { get; init; } = "";

    /// <summary>该实例所属的游戏根目录（跨文件夹搜索结果切换文件夹时使用）。</summary>
    public string GameDirectory { get; init; } = "";

    /// <summary>所属游戏文件夹的展示名（跨文件夹搜索时显示在副标题里）。</summary>
    public string FolderName { get; init; } = "";

    public DateTime LastWriteTime { get; init; }

    /// <summary>是否为整合包（带 modpack 标记文件或 PCL\Logo.png）。</summary>
    public bool IsModpack { get; init; }

    /// <summary>当前选择的版本（由 InstancesViewModel 维护，用于列表角标）。</summary>
    [ObservableProperty]
    private bool _isCurrent;

    /// <summary>跨文件夹搜索结果里标记所属文件夹（副标题追加文件夹名）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ListSubText))]
    private bool _showFolderName;

    /// <summary>该实例的累计启动次数（由启动页在检测到游戏窗口后写入并持久化）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchCountText))]
    [NotifyPropertyChangedFor(nameof(ListSubText))]
    private int _launchCount;

    public string LaunchCountText => $"{LaunchCount} 次";

    /// <summary>版本管理列表的副标题：版本信息 + 启动次数（跨文件夹搜索时附文件夹名）。</summary>
    public string ListSubText => ShowFolderName && FolderName.Length > 0
        ? $"{Description} · {FolderName} · 启动 {LaunchCount} 次"
        : $"{Description} · 启动 {LaunchCount} 次";

    public bool IsModded => Loader != "原版" || !Id.Equals(GameVersion, StringComparison.OrdinalIgnoreCase);

    public string Description => IsModded ? $"{Loader} · {GameVersion}" : GameVersion;

    public string BadgeText => Loader switch
    {
        "原版" => "原版",
        "Fabric" => "Fabric",
        "Forge" => "Forge",
        "NeoForge" => "NeoForge",
        "Quilt" => "Quilt",
        "OptiFine" => "OptiFine",
        _ => Loader
    };

    /// <summary>
    /// 列表图标键名，映射与 PCL-CE 的 McInstance 图标规则一致：
    /// 原版=草方块、快照=命令方块、旧版本=圆石、Forge=铁砧、NeoForge/Fabric/Quilt=各自 Logo、
    /// OptiFine=草径、LiteLoader=蛋、Cleanroom=LabyMod 各自图标；整合包=金块。
    /// 无法识别时返回 "generic"，由界面改用通用矢量兜底图标。
    /// </summary>
    public string IconKey
    {
        get
        {
            if (IsModpack)
            {
                return "modpack";
            }

            return Loader switch
            {
                "NeoForge" => "neoforge",
                "Forge" => "forge",
                "Fabric" => "fabric",
                "Quilt" => "quilt",
                "OptiFine" => "optifine",
                "LiteLoader" => "liteloader",
                "Cleanroom" => "cleanroom",
                "LabyMod" => "labymod",
                "原版" => Type switch
                {
                    "release" => "grass-block",
                    "snapshot" => "command-block",
                    "old_beta" or "old_alpha" => "cobblestone",
                    _ => "generic"
                },
                _ => "generic"
            };
        }
    }

    /// <summary>有对应位图资源；false 时界面使用通用矢量兜底图标。</summary>
    public bool HasBitmapIcon => !IconKey.Equals("generic", StringComparison.Ordinal);

    public bool UsesFallbackIcon => !HasBitmapIcon;
}

/// <summary>实例复制/重命名结果。</summary>
public readonly record struct InstanceOperationResult(bool Success, string Message);

/// <summary>
/// 扫描 versions 目录，解析实例信息（对应 PCL-CE 的版本管理）。
/// </summary>
public sealed class InstanceManager
{
    private readonly LauncherLogger _logger;
    private readonly VersionResolver _resolver;

    public InstanceManager(LauncherLogger logger, VersionResolver resolver)
    {
        _logger = logger;
        _resolver = resolver;
    }

    public async Task<IReadOnlyList<MinecraftInstance>> ScanAsync(
        string gameDirectory,
        CancellationToken cancellationToken = default)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return Array.Empty<MinecraftInstance>();
        }

        var results = new List<MinecraftInstance>();
        foreach (var directory in Directory.EnumerateDirectories(versionsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                var info = await _resolver.LoadAsync(versionsDirectory, id, cancellationToken).ConfigureAwait(false);
                if (info is null)
                {
                    continue;
                }

                var gameVersion = await ResolveGameVersionAsync(versionsDirectory, info, cancellationToken).ConfigureAwait(false);
                var loader = DetectLoader(info, id);
                var lastWrite = File.GetLastWriteTime(jsonPath);
                results.Add(new MinecraftInstance
                {
                    Id = id,
                    Name = id,
                    GameVersion = gameVersion,
                    Loader = loader,
                    Type = info.Type ?? "release",
                    Directory = directory,
                    GameDirectory = gameDirectory,
                    FolderName = GetFolderDisplayName(gameDirectory),
                    LastWriteTime = lastWrite,
                    IsModpack = DetectModpack(directory)
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.Warn($"扫描实例失败：{directory} · {ex.Message}");
            }
        }

        return results
            .OrderByDescending(x => x.LastWriteTime)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>删除实例：Windows 进回收站，其他平台移入 .hycl-trash 兜底目录（与 PCL-CE 回收站策略一致）。</summary>
    public bool Delete(string gameDirectory, string instanceId)
    {
        if (instanceId.IndexOfAny(new[] { '\\', '/' }) >= 0 || instanceId.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        var directory = Path.Combine(gameDirectory, "versions", instanceId);
        if (!Directory.Exists(directory))
        {
            return false;
        }

        if (RecycleBin.DeleteDirectory(directory, gameDirectory, instanceId, _logger))
        {
            _logger.Info($"已删除实例：{instanceId}");
            return true;
        }

        return false;
    }

    /// <summary>实例名必须是 versions 下的单层目录名：不含分隔符、.. 与非法字符。</summary>
    public static bool IsValidInstanceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or "..")
        {
            return false;
        }

        if (id.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0)
        {
            return false;
        }

        if (id.Contains("..", StringComparison.Ordinal) || id.EndsWith('.'))
        {
            return false;
        }

        return true;
    }

    /// <summary>实例目录（versions/&lt;id&gt;）。</summary>
    public static string GetInstanceDirectory(string gameDirectory, string instanceId) =>
        Path.Combine(gameDirectory, "versions", instanceId);

    /// <summary>
    /// 游戏文件夹的展示名：平台默认 .minecraft 显示为「官方启动器文件夹」，
    /// 其余取目录名（如 D:\minecraft to go\.minecraft → .minecraft）。
    /// </summary>
    public static string GetFolderDisplayName(string folderPath)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        var defaultDirectory = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(PlatformInfo.DefaultMinecraftDirectory));
        if (full.Equals(defaultDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return "官方启动器文件夹";
        }

        var leaf = Path.GetFileName(full);
        return string.IsNullOrWhiteSpace(leaf) ? full : leaf;
    }

    /// <summary>
    /// 从用户选择的文件夹出发，找出其中（含子文件夹）可作为游戏根目录的文件夹：
    /// 判定标准是存在 versions 子目录。选中的文件夹本身符合时直接返回它，
    /// 否则向下最多搜索 <paramref name="maxDepth"/> 层，供「从文件夹导入」把
    /// 一个父目录下的多个 .minecraft 一并加入文件夹列表。
    /// </summary>
    public static IReadOnlyList<string> DiscoverGameFolders(string folderPath, int maxDepth = 3)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return Array.Empty<string>();
        }

        string root;
        try
        {
            root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Array.Empty<string>();
        }

        if (!Directory.Exists(root))
        {
            return Array.Empty<string>();
        }

        // 用户直接选中 versions 目录时，提升到其父级游戏目录
        if (Path.GetFileName(root).Equals("versions", StringComparison.OrdinalIgnoreCase) &&
            Directory.EnumerateDirectories(root).Any())
        {
            root = Path.GetDirectoryName(root) ?? root;
        }

        var results = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        while (queue.Count > 0 && visited.Count < 256)
        {
            var (directory, depth) = queue.Dequeue();
            if (!visited.Add(directory))
            {
                continue;
            }

            if (Directory.Exists(Path.Combine(directory, "versions")))
            {
                results.Add(directory);
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    if (IsDiscoverySkipped(child))
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 无权限/被占用的目录直接跳过，不影响其它分支
            }
        }

        return results.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>递归发现时跳过明显不是游戏目录的重型子目录与链接目录。</summary>
    private static bool IsDiscoverySkipped(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name.StartsWith('.') && !name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        switch (name.ToLowerInvariant())
        {
            case "versions":
            case "libraries":
            case "assets":
            case "runtime":
            case "logs":
            case "crash-reports":
            case "backups":
            case "plugins":
            case "cache":
                return true;
        }

        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// 实例内容根目录：版本隔离时是实例自身目录，否则是游戏根目录。
    /// saves/mods/resourcepacks/shaderpacks 都基于该目录解析。
    /// </summary>
    public static string GetContentRoot(string gameDirectory, string instanceId, bool isolated) =>
        isolated ? GetInstanceDirectory(gameDirectory, instanceId) : gameDirectory;

    /// <summary>复制实例目录；新实例名必须合法且尚不存在。</summary>
    public InstanceOperationResult Copy(string gameDirectory, string sourceId, string newId)
    {
        if (!IsValidInstanceId(newId))
        {
            return new InstanceOperationResult(false, "实例名称包含非法字符");
        }

        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        var source = Path.Combine(versionsDirectory, sourceId);
        var target = Path.Combine(versionsDirectory, newId);
        if (!Directory.Exists(source))
        {
            return new InstanceOperationResult(false, "源实例不存在");
        }

        if (Directory.Exists(target))
        {
            return new InstanceOperationResult(false, "同名实例已存在");
        }

        try
        {
            CopyDirectory(source, target);
            RenameCoreFiles(target, sourceId, newId);
            _logger.Info($"已复制实例：{sourceId} → {newId}");
            return new InstanceOperationResult(true, $"已复制为 {newId}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"复制实例失败：{sourceId} → {newId}", ex);
            return new InstanceOperationResult(false, $"复制失败：{ex.Message}");
        }
    }

    /// <summary>重命名实例目录，并同步改名 json/jar/natives 与版本 JSON 的 id 字段。</summary>
    public InstanceOperationResult Rename(string gameDirectory, string oldId, string newId)
    {
        if (!IsValidInstanceId(newId))
        {
            return new InstanceOperationResult(false, "实例名称包含非法字符");
        }

        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        var source = Path.Combine(versionsDirectory, oldId);
        var target = Path.Combine(versionsDirectory, newId);
        if (!Directory.Exists(source))
        {
            return new InstanceOperationResult(false, "实例不存在");
        }

        if (oldId.Equals(newId, StringComparison.OrdinalIgnoreCase))
        {
            return new InstanceOperationResult(true, "名称未变化");
        }

        if (Directory.Exists(target))
        {
            return new InstanceOperationResult(false, "同名实例已存在");
        }

        try
        {
            Directory.Move(source, target);
            RenameCoreFiles(target, oldId, newId);
            _logger.Info($"已重命名实例：{oldId} → {newId}");
            return new InstanceOperationResult(true, $"已重命名为 {newId}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"重命名实例失败：{oldId} → {newId}", ex);
            return new InstanceOperationResult(false, $"重命名失败：{ex.Message}");
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), true);
        }
    }

    /// <summary>把实例目录内的主 json / jar / natives 从旧名改为新名，并更新 json 的 id。</summary>
    private void RenameCoreFiles(string instanceDirectory, string oldId, string newId)
    {
        var oldJson = Path.Combine(instanceDirectory, oldId + ".json");
        var newJson = Path.Combine(instanceDirectory, newId + ".json");
        if (File.Exists(oldJson))
        {
            File.Move(oldJson, newJson, true);
        }

        var oldJar = Path.Combine(instanceDirectory, oldId + ".jar");
        var newJar = Path.Combine(instanceDirectory, newId + ".jar");
        if (File.Exists(oldJar))
        {
            File.Move(oldJar, newJar, true);
        }

        var oldNatives = Path.Combine(instanceDirectory, oldId + "-natives");
        if (Directory.Exists(oldNatives))
        {
            Directory.Move(oldNatives, Path.Combine(instanceDirectory, newId + "-natives"));
        }

        if (!File.Exists(newJson))
        {
            return;
        }

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(newJson)) as JsonObject;
            if (node is not null && node["id"] is JsonValue)
            {
                node["id"] = newId;
                File.WriteAllText(newJson, node.ToJsonString());
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.Warn($"更新实例 JSON 的 id 失败：{newJson} · {ex.Message}");
        }
    }

    private async Task<string> ResolveGameVersionAsync(
        string versionsDirectory,
        VersionInfo info,
        CancellationToken cancellationToken)
    {
        var current = info;
        var gameVersion = info.Id;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < 5 && !string.IsNullOrWhiteSpace(current.InheritsFrom); depth++)
        {
            if (!visited.Add(current.InheritsFrom))
            {
                break;
            }

            var parent = await _resolver.LoadAsync(versionsDirectory, current.InheritsFrom, cancellationToken).ConfigureAwait(false);
            if (parent is null)
            {
                gameVersion = current.InheritsFrom;
                break;
            }

            current = parent;
            gameVersion = parent.Id;
        }

        return gameVersion;
    }

    /// <summary>
    /// 整合包判定：实例目录带整合包清单文件，或带 PCL 自定义图标（PCL\Logo.png）。
    /// </summary>
    private static bool DetectModpack(string instanceDirectory)
    {
        string[] markers =
        [
            Path.Combine(instanceDirectory, "PCL", "Logo.png"),
            Path.Combine(instanceDirectory, "modpack.json"),
            Path.Combine(instanceDirectory, "manifest.json"),
            Path.Combine(instanceDirectory, "mcbbs.packmeta"),
            Path.Combine(instanceDirectory, "mmc-pack.json"),
            Path.Combine(instanceDirectory, "instance.cfg")
        ];

        return markers.Any(File.Exists);
    }

    private static string DetectLoader(VersionInfo info, string instanceId)
    {
        // 整合包（如 RLCraft）的版本 JSON 只在 libraries 里出现 net.minecraftforge，
        // 因此加载器识别必须把依赖名一起纳入文本匹配。
        var libraries = string.Join(' ', info.Libraries.Select(x => x.Name));
        var text = $"{instanceId} {info.Id} {info.InheritsFrom} {info.MainClass} {libraries}".ToLowerInvariant();
        if (text.Contains("neoforge", StringComparison.Ordinal))
        {
            return "NeoForge";
        }

        if (text.Contains("cleanroom", StringComparison.Ordinal))
        {
            return "Cleanroom";
        }

        if (text.Contains("labymod", StringComparison.Ordinal))
        {
            return "LabyMod";
        }

        if (text.Contains("forge", StringComparison.Ordinal))
        {
            return "Forge";
        }

        if (text.Contains("fabric", StringComparison.Ordinal))
        {
            return "Fabric";
        }

        if (text.Contains("quilt", StringComparison.Ordinal))
        {
            return "Quilt";
        }

        if (text.Contains("optifine", StringComparison.Ordinal))
        {
            return "OptiFine";
        }

        if (text.Contains("liteloader", StringComparison.Ordinal))
        {
            return "LiteLoader";
        }

        return "原版";
    }
}
