using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Services;

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
