using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Services;
using System.Text.Json.Nodes;

namespace Hycl.Launcher.Core.Game;

/// <summary>已安装的版本实例。</summary>
public sealed class MinecraftInstance
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string GameVersion { get; init; } = "";

    public string Loader { get; init; } = "原版";

    public string Type { get; init; } = "release";

    public string Directory { get; init; } = "";

    public DateTime LastWriteTime { get; init; }

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

    public string IconText => Loader switch
    {
        "原版" => "M",
        "Fabric" => "Fb",
        "Forge" => "Fg",
        "NeoForge" => "NF",
        "Quilt" => "Q",
        "OptiFine" => "OF",
        _ => "M"
    };
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
                    LastWriteTime = lastWrite
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

        try
        {
            Directory.Delete(directory, true);
            _logger.Info($"已删除实例：{instanceId}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.Error($"删除实例失败：{instanceId}", ex);
            return false;
        }
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

    private static string DetectLoader(VersionInfo info, string instanceId)
    {
        var text = $"{instanceId} {info.Id} {info.InheritsFrom} {info.MainClass}".ToLowerInvariant();
        if (text.Contains("neoforge", StringComparison.Ordinal))
        {
            return "NeoForge";
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
