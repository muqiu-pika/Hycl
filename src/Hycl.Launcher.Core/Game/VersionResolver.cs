using System.Text.Json;
using Hycl.Launcher.Core.Game.Models;
using Hycl.Launcher.Core.Platform;
using Hycl.Launcher.Core.Serialization;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>合并 inheritsFrom 后的完整版本信息。</summary>
public sealed class ResolvedVersion
{
    public string InstanceId { get; init; } = "";

    public string RootId { get; init; } = "";

    public string VersionDirectory { get; init; } = "";

    public VersionInfo Info { get; init; } = new();

    public string ClientJarPath { get; init; } = "";

    public string NativesDirectory => Path.Combine(VersionDirectory, "natives");

    public bool IsModded => !InstanceId.Equals(RootId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 版本 JSON 加载与 inheritsFrom 链合并（对应 PCL-CE 的版本继承处理）。
/// </summary>
public sealed class VersionResolver
{
    private readonly LauncherLogger _logger;

    public VersionResolver(LauncherLogger logger)
    {
        _logger = logger;
    }

    public async Task<ResolvedVersion?> ResolveAsync(
        string gameDirectory,
        string instanceId,
        CancellationToken cancellationToken = default)
    {
        var versionsDirectory = Path.Combine(gameDirectory, "versions");
        var chain = new List<VersionInfo>();
        var currentId = instanceId;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(currentId); depth++)
        {
            if (!visited.Add(currentId))
            {
                _logger.Warn($"版本继承出现循环：{instanceId}");
                break;
            }

            var info = await LoadAsync(versionsDirectory, currentId, cancellationToken).ConfigureAwait(false);
            if (info is null)
            {
                return null;
            }

            chain.Add(info);
            currentId = info.InheritsFrom ?? "";
        }

        if (chain.Count == 0)
        {
            return null;
        }

        var merged = Merge(chain, instanceId);
        var rootId = chain[^1].Id;
        var clientJar = Path.Combine(versionsDirectory, rootId, rootId + ".jar");
        if (!File.Exists(clientJar))
        {
            var childJar = Path.Combine(versionsDirectory, instanceId, instanceId + ".jar");
            clientJar = File.Exists(childJar) ? childJar : clientJar;
        }

        return new ResolvedVersion
        {
            InstanceId = instanceId,
            RootId = rootId,
            VersionDirectory = Path.Combine(versionsDirectory, instanceId),
            Info = merged,
            ClientJarPath = clientJar
        };
    }

    public async Task<VersionInfo?> LoadAsync(
        string versionsDirectory,
        string versionId,
        CancellationToken cancellationToken = default)
    {
        var jsonPath = Path.Combine(versionsDirectory, versionId, versionId + ".json");
        if (!File.Exists(jsonPath))
        {
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(jsonPath, cancellationToken).ConfigureAwait(false);
            var info = JsonSerializer.Deserialize(json, NetJsonContext.Default.VersionInfo);
            if (info is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(info.Id))
            {
                info.Id = versionId;
            }

            return info;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.Error($"读取版本 JSON 失败：{jsonPath}", ex);
            return null;
        }
    }

    private static VersionInfo Merge(IReadOnlyList<VersionInfo> chain, string instanceId)
    {
        var merged = new VersionInfo
        {
            Id = instanceId,
            Arguments = new VersionArguments()
        };

        // 父版本先合并，子版本覆盖标量、追加数组
        for (var index = chain.Count - 1; index >= 0; index--)
        {
            var current = chain[index];
            merged.MainClass = current.MainClass ?? merged.MainClass;
            merged.MinecraftArguments = current.MinecraftArguments ?? merged.MinecraftArguments;
            merged.AssetIndex = current.AssetIndex ?? merged.AssetIndex;
            merged.Assets = current.Assets ?? merged.Assets;
            merged.Downloads = current.Downloads ?? merged.Downloads;
            merged.Logging = current.Logging ?? merged.Logging;
            merged.JavaVersion = current.JavaVersion ?? merged.JavaVersion;
            merged.Type = current.Type ?? merged.Type;
            merged.ReleaseTime = current.ReleaseTime ?? merged.ReleaseTime;
            merged.ComplianceLevel = current.ComplianceLevel ?? merged.ComplianceLevel;

            if (current.Arguments is not null)
            {
                merged.Arguments.Game.AddRange(current.Arguments.Game);
                merged.Arguments.Jvm.AddRange(current.Arguments.Jvm);
            }

            merged.Libraries.AddRange(current.Libraries);
        }

        merged.MainClass ??= "net.minecraft.client.main.Main";
        merged.Type ??= "release";
        return merged;
    }

    public static string GetDefaultGameDirectory() => PlatformInfo.DefaultMinecraftDirectory;
}
