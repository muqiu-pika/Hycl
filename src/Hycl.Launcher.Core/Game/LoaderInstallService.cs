using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Loaders;
using Hycl.Launcher.Core.Game.Mods;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game;

/// <summary>一次加载器安装的计划（主加载器 + 附属内容）。</summary>
public sealed record LoaderInstallPlan
{
    /// <summary>主加载器种类；vanilla 表示仅原版。</summary>
    public required string Kind { get; init; }

    public required string GameDirectory { get; init; }

    public required string GameVersion { get; init; }

    /// <summary>实例目录名。</summary>
    public required string TargetName { get; init; }

    /// <summary>主加载器版本；null 表示自动选择最新稳定版。</summary>
    public string? LoaderVersion { get; init; }

    /// <summary>主加载器为 LabyMod 时的通道条目。</summary>
    public LabyModEntry? LabyMod { get; init; }

    /// <summary>附属 OptiFine（仅原版 / Forge 可搭配）。</summary>
    public OptiFineEntry? OptiFine { get; init; }

    /// <summary>附属 LiteLoader 条目。</summary>
    public LiteLoaderEntry? LiteLoader { get; init; }

    /// <summary>前置 Mod（Modrinth slug，如 fabric-api / optifabric），安装后放入 mods。</summary>
    public IReadOnlyList<string> CompanionModSlugs { get; init; } = [];

    public DownloadPolicy Policy { get; init; } = DownloadPolicy.Default;

    /// <summary>目标实例是否启用版本隔离；附属内容（OptiFine Mod、前置 Mod）的 mods 目录据此解析。</summary>
    public bool VersionIsolation { get; init; } = true;

    public int Threads { get; init; } = 16;

    public bool DownloadAssets { get; init; } = true;

    public string? JavaPath { get; init; }
}

/// <summary>
/// 加载器安装门面：按计划把原版、主加载器、附属内容组装成一次完整安装。
/// </summary>
public sealed class LoaderInstallService
{
    public const string VanillaKind = "vanilla";

    private readonly VersionInstaller _installer;
    private readonly LoaderProviderRegistry _providers;
    private readonly ModrinthProvider _modrinth;
    private readonly LauncherLogger _logger;

    public LoaderInstallService(
        VersionInstaller installer,
        LoaderProviderRegistry providers,
        ModrinthProvider modrinth,
        LauncherLogger logger)
    {
        _installer = installer;
        _providers = providers;
        _modrinth = modrinth;
        _logger = logger;
    }

    /// <summary>执行安装计划；返回实际安装的实例名。</summary>
    public async Task<string> InstallAsync(
        LoaderInstallPlan plan,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        plan = await ResolveMissingEntriesAsync(plan, cancellationToken).ConfigureAwait(false);
        Validate(plan);
        var kind = plan.Kind.ToLowerInvariant();
        var installedName = kind switch
        {
            VanillaKind => await InstallVanillaAsync(plan, progress, cancellationToken).ConfigureAwait(false),
            "forge" or "neoforge" or "cleanroom" =>
                await InstallForgelikeAsync(plan, kind, progress, cancellationToken).ConfigureAwait(false),
            "fabric" or "quilt" or "legacyfabric" =>
                await _installer.InstallFabricLikeAsync(
                        plan.GameDirectory,
                        kind,
                        plan.GameVersion,
                        plan.LoaderVersion,
                        plan.TargetName,
                        plan.Policy,
                        plan.Threads,
                        progress,
                        cancellationToken,
                        plan.DownloadAssets)
                    .ConfigureAwait(false),
            "optifine" => await _installer
                .InstallOptiFineAsync(ToOptiFineRequest(plan), progress, cancellationToken)
                .ConfigureAwait(false),
            "liteloader" => await _installer.InstallLiteLoaderAsync(
                    plan.GameDirectory,
                    plan.GameVersion,
                    plan.TargetName,
                    plan.LiteLoader ?? throw new InvalidOperationException("LiteLoader 安装缺少条目"),
                    plan.Policy,
                    plan.Threads,
                    progress,
                    cancellationToken,
                    plan.DownloadAssets)
                .ConfigureAwait(false),
            "labymod" => await _installer.InstallLabyModAsync(
                    plan.GameDirectory,
                    plan.GameVersion,
                    plan.TargetName,
                    plan.LabyMod ?? await ResolveLabyModEntryAsync(plan, cancellationToken).ConfigureAwait(false),
                    plan.Policy,
                    plan.Threads,
                    progress,
                    cancellationToken,
                    plan.DownloadAssets)
                .ConfigureAwait(false),
            _ => throw new ArgumentException($"未知的加载器种类：{plan.Kind}", nameof(plan))
        };

        // 附属 OptiFine（与 Forge 共存时装进 mods）
        if (plan.OptiFine is not null && kind is "forge")
        {
            _logger.Info($"附属 OptiFine {plan.OptiFine.NameVersion}：以 Mod 形式装入 mods");
            await _installer
                .InstallOptiFineAsModAsync(ToOptiFineRequest(plan), progress, cancellationToken)
                .ConfigureAwait(false);
        }

        // 前置 Mod（Fabric API / Legacy Fabric API / OptiFabric）
        foreach (var slug in plan.CompanionModSlugs)
        {
            await DownloadCompanionModAsync(plan, installedName, slug, cancellationToken).ConfigureAwait(false);
        }

        return installedName;
    }

    /// <summary>附属条目缺失时自动选择最新稳定版（分区一键安装路径）。</summary>
    private async Task<LoaderInstallPlan> ResolveMissingEntriesAsync(
        LoaderInstallPlan plan,
        CancellationToken cancellationToken)
    {
        var kind = plan.Kind.ToLowerInvariant();
        if (kind == "optifine" && plan.OptiFine is null)
        {
            var versions = await _providers.OptiFine
                .GetVersionsForAsync(plan.GameVersion, plan.Policy, cancellationToken)
                .ConfigureAwait(false);
            var selected = versions.FirstOrDefault(x => x.IsStable) ?? versions.FirstOrDefault();
            plan = plan with { OptiFine = selected?.Payload as OptiFineEntry };
        }
        else if (kind == "liteloader" && plan.LiteLoader is null)
        {
            var versions = await _providers.LiteLoader
                .GetVersionsForAsync(plan.GameVersion, plan.Policy, cancellationToken)
                .ConfigureAwait(false);
            var selected = versions.FirstOrDefault(x => x.IsStable) ?? versions.FirstOrDefault();
            plan = plan with { LiteLoader = selected?.Payload as LiteLoaderEntry };
        }

        return plan;
    }

    private static void Validate(LoaderInstallPlan plan)
    {
        var kind = plan.Kind.ToLowerInvariant();
        if (plan.OptiFine is not null && !LoaderCompat.IsOptiFineCompatibleWith(kind, plan.GameVersion))
        {
            throw new InvalidOperationException($"{plan.Kind} 与 OptiFine 不兼容，请分开安装");
        }

        if (plan.LiteLoader is not null && kind is not VanillaKind and not "forge")
        {
            throw new InvalidOperationException("LiteLoader 仅可与原版或 Forge 搭配");
        }
    }

    private async Task<string> InstallVanillaAsync(
        LoaderInstallPlan plan,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var versions = await _installer.GetVersionListAsync(plan.Policy, cancellationToken).ConfigureAwait(false);
        var summary = versions.FirstOrDefault(x => x.Id.Equals(plan.GameVersion, StringComparison.OrdinalIgnoreCase))
                      ?? throw new DownloadException($"版本清单中找不到 {plan.GameVersion}");
        await _installer.InstallVanillaAsync(
                plan.GameDirectory,
                summary,
                plan.Policy,
                plan.Threads,
                progress,
                cancellationToken,
                plan.DownloadAssets)
            .ConfigureAwait(false);
        return plan.TargetName;
    }

    private async Task<string> InstallForgelikeAsync(
        LoaderInstallPlan plan,
        string kind,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        ILoaderVersionProvider provider = kind switch
        {
            "forge" => _providers.Forge,
            "neoforge" => _providers.NeoForge,
            "cleanroom" => _providers.Cleanroom,
            _ => throw new ArgumentException(kind)
        };

        var versions = await provider
            .GetVersionsForAsync(plan.GameVersion, plan.Policy, cancellationToken)
            .ConfigureAwait(false);
        var selected = plan.LoaderVersion is null
            ? versions.FirstOrDefault(x => x.IsStable) ?? versions.FirstOrDefault()
            : versions.FirstOrDefault(x => x.VersionName.Equals(plan.LoaderVersion, StringComparison.OrdinalIgnoreCase))
              ?? versions.FirstOrDefault();
        if (selected?.Payload is not ForgelikeEntry entry)
        {
            throw new DownloadException($"{plan.Kind} 在 {plan.GameVersion} 下没有可用版本");
        }

        return await _installer.InstallForgelikeAsync(
                new VersionInstaller.ForgelikeInstallRequest
                {
                    GameDirectory = plan.GameDirectory,
                    GameVersion = plan.GameVersion,
                    TargetName = plan.TargetName,
                    Entry = entry,
                    Policy = plan.Policy,
                    Threads = plan.Threads,
                    DownloadAssets = plan.DownloadAssets,
                    JavaPath = plan.JavaPath
                },
                progress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<LabyModEntry> ResolveLabyModEntryAsync(
        LoaderInstallPlan plan,
        CancellationToken cancellationToken)
    {
        var versions = await _providers.LabyMod
            .GetVersionsForAsync(plan.GameVersion, plan.Policy, cancellationToken)
            .ConfigureAwait(false);
        var selected = plan.LoaderVersion is null
            ? versions.FirstOrDefault(x => x.IsStable) ?? versions.FirstOrDefault()
            : versions.FirstOrDefault(x => x.VersionName.Equals(plan.LoaderVersion, StringComparison.OrdinalIgnoreCase));
        if (selected?.Payload is not LabyModEntry entry)
        {
            throw new DownloadException($"LabyMod 在 {plan.GameVersion} 下没有可用版本");
        }

        return entry;
    }

    private VersionInstaller.OptiFineInstallRequest ToOptiFineRequest(LoaderInstallPlan plan)
        => new()
        {
            GameDirectory = plan.GameDirectory,
            GameVersion = plan.GameVersion,
            TargetName = plan.TargetName,
            Entry = plan.OptiFine ?? throw new InvalidOperationException("OptiFine 安装缺少条目"),
            Policy = plan.Policy,
            VersionIsolation = plan.VersionIsolation,
            Threads = plan.Threads,
            DownloadAssets = plan.DownloadAssets,
            JavaPath = plan.JavaPath
        };

    private async Task DownloadCompanionModAsync(
        LoaderInstallPlan plan,
        string installedName,
        string slug,
        CancellationToken cancellationToken)
    {
        try
        {
            var versions = await _modrinth.GetVersionsAsync(slug, cancellationToken).ConfigureAwait(false);
            var loaders = slug == "legacy-fabric-api"
                ? new[] { "fabric", "legacyfabric" }
                : new[] { "fabric", "quilt" };
            var recommended = _modrinth.Recommend(versions, plan.GameVersion, loaders);
            if (recommended is null)
            {
                _logger.Warn($"未找到与 {plan.GameVersion} 匹配的前置 Mod：{slug}");
                return;
            }

            var modsDirectory = Path.Combine(
                InstanceManager.GetContentRoot(plan.GameDirectory, installedName, plan.VersionIsolation),
                "mods");
            await _modrinth.DownloadModAsync(recommended, modsDirectory, cancellationToken).ConfigureAwait(false);
            _logger.Info($"前置 Mod {recommended.VersionNumber} 已放入 {modsDirectory}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DownloadException or HttpRequestException or IOException)
        {
            // 前置 Mod 失败不阻断主安装
            _logger.Warn($"前置 Mod 下载失败（可忽略）：{ex.Message}");
        }
    }
}
