using System.Text.Json;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.Game.Comp;

/// <summary>解析出的可安装前置（项目 + 已选文件）。</summary>
public sealed record CompDependencyInstall(CompProject Project, CompFile File);

/// <summary>无法解析的必需前置。</summary>
public sealed record CompDependencyUnresolved(string ProjectId, bool FromCurseForge, string Reason);

/// <summary>前置解析结果（对应 PCL-CE ModDependencyResolutionResult 的 Hycl 子集）。</summary>
public sealed record CompDependencyResolution(
    IReadOnlyList<CompDependencyInstall> ToInstall,
    IReadOnlyList<CompDependencyUnresolved> Unresolved);

/// <summary>
/// 社区资源必需前置解析（对应 PCL-CE ModDependencyResolver）：
/// 从资源文件的 required 依赖出发递归解析，按目标 MC 版本与加载器筛选文件，
/// 已存在同文件名文件时视为已安装；单个依赖失败只记为未解析，不中断整体流程。
/// </summary>
public sealed class CompDependencyResolver
{
    private const int MaxDepth = 32;

    private readonly CurseForgeClient _curseForge;
    private readonly ModrinthClient _modrinth;
    private readonly LauncherLogger _logger;

    public CompDependencyResolver(
        CurseForgeClient curseForge,
        ModrinthClient modrinth,
        LauncherLogger logger)
    {
        _curseForge = curseForge;
        _modrinth = modrinth;
        _logger = logger;
    }

    /// <param name="rootFile">用户要下载的资源文件。</param>
    /// <param name="gameVersion">目标实例的原版版本号。</param>
    /// <param name="loaders">目标实例的加载器（小写；空表示不限定）。</param>
    /// <param name="targetModsDirectory">用户选择的保存目录（扫描其中已存在的文件）。</param>
    public async Task<CompDependencyResolution> ResolveAsync(
        CompFile rootFile,
        string gameVersion,
        IReadOnlyList<string> loaders,
        string targetModsDirectory,
        CancellationToken cancellationToken = default)
    {
        var state = new ResolutionState(rootFile, gameVersion, loaders, targetModsDirectory, _logger);
        foreach (var dependency in rootFile.Dependencies.Where(d => d.Required))
        {
            await ResolveDependencyAsync(state, dependency, rootFile.FromCurseForge, depth: 0, cancellationToken)
                .ConfigureAwait(false);
        }

        return state.Build();
    }

    private async Task ResolveDependencyAsync(
        ResolutionState state,
        CompDependencyRef dependency,
        bool fromCurseForge,
        int depth,
        CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
        {
            state.AddUnresolved(dependency.ProjectId, fromCurseForge, "依赖层级过深");
            return;
        }

        if (!state.Visited.Add(dependency.ProjectId))
        {
            return;
        }

        try
        {
            var project = await GetProjectAsync(dependency.ProjectId, fromCurseForge, cancellationToken).ConfigureAwait(false);
            var files = await GetFilesAsync(dependency.ProjectId, fromCurseForge, cancellationToken).ConfigureAwait(false);
            var selected = SelectBestFile(files, state.GameVersion, state.Loaders);
            if (selected is null)
            {
                state.AddUnresolved(dependency.ProjectId, fromCurseForge, $"没有适配 {state.GameVersion} 的文件");
                return;
            }

            if (state.IsInstalled(selected.FileName))
            {
                state.AddSatisfied(project);
                return;
            }

            state.AddInstall(project, selected);
            foreach (var nested in selected.Dependencies.Where(d => d.Required))
            {
                await ResolveDependencyAsync(state, nested, fromCurseForge, depth + 1, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is CompException or HttpRequestException or IOException
                                       or TaskCanceledException or JsonException or InvalidOperationException)
        {
            _logger.Warn($"前置 {dependency.ProjectId} 解析失败：{ex.Message}");
            state.AddUnresolved(dependency.ProjectId, fromCurseForge, ex.Message);
        }
    }

    private async Task<CompProject> GetProjectAsync(
        string projectId,
        bool fromCurseForge,
        CancellationToken cancellationToken)
        => fromCurseForge
            ? await _curseForge.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false)
            : await _modrinth.GetProjectAsync(projectId, cancellationToken).ConfigureAwait(false);

    private async Task<IReadOnlyList<CompFile>> GetFilesAsync(
        string projectId,
        bool fromCurseForge,
        CancellationToken cancellationToken)
        => fromCurseForge
            ? await _curseForge.GetFilesAsync(projectId, CompResourceType.Mod, cancellationToken).ConfigureAwait(false)
            : await _modrinth.GetFilesAsync(projectId, CompResourceType.Mod, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// 选择最合适的文件（对应 PCL-CE SelectBestFile）：必须精确适配目标 MC 版本；
    /// 加载器未知时放宽；同日期的文件优先正式版。
    /// </summary>
    private static CompFile? SelectBestFile(
        IEnumerable<CompFile> files,
        string gameVersion,
        IReadOnlyList<string> loaders)
    {
        var candidates = files
            .Where(file => file.GameVersions.Any(v => v.Equals(gameVersion, StringComparison.OrdinalIgnoreCase)))
            .Where(file => loaders.Count == 0
                           || file.ModLoaders.Count == 0
                           || file.ModLoaders.Any(loader => loaders.Contains(loader, StringComparer.OrdinalIgnoreCase)))
            .ToList();
        return candidates
            .OrderBy(file => (int)file.Status)
            .ThenByDescending(file => file.ReleaseDate ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    private sealed class ResolutionState
    {
        private readonly List<CompDependencyInstall> _toInstall = [];
        private readonly List<CompDependencyUnresolved> _unresolved = [];
        private readonly HashSet<string> _installKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _unresolvedKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _satisfiedKeys = new(StringComparer.OrdinalIgnoreCase);
        private readonly string _targetDirectory;
        private readonly LauncherLogger _logger;

        public ResolutionState(
            CompFile rootFile,
            string gameVersion,
            IReadOnlyList<string> loaders,
            string targetDirectory,
            LauncherLogger logger)
        {
            GameVersion = gameVersion;
            Loaders = loaders;
            _targetDirectory = targetDirectory;
            _logger = logger;
            Source = rootFile.FromCurseForge;
        }

        public string GameVersion { get; }

        public IReadOnlyList<string> Loaders { get; }

        public bool Source { get; }

        public HashSet<string> Visited { get; } = new(StringComparer.OrdinalIgnoreCase);

        public bool IsInstalled(string fileName)
            => !string.IsNullOrWhiteSpace(fileName)
               && File.Exists(Path.Combine(_targetDirectory, fileName));

        public void AddInstall(CompProject project, CompFile file)
        {
            if (!_installKeys.Add($"{project.Id}:{file.Id}"))
            {
                return;
            }

            _toInstall.Add(new CompDependencyInstall(project, file));
            _logger.Info($"前置已解析：{project.Title} · {file.FileName}");
        }

        public void AddUnresolved(string projectId, bool fromCurseForge, string reason)
        {
            if (!_unresolvedKeys.Add($"{fromCurseForge}:{projectId}"))
            {
                return;
            }

            _unresolved.Add(new CompDependencyUnresolved(projectId, fromCurseForge, reason));
        }

        public void AddSatisfied(CompProject project)
        {
            if (_satisfiedKeys.Add(project.Id))
            {
                _logger.Info($"前置已存在，跳过：{project.Title}");
            }
        }

        public CompDependencyResolution Build() => new(_toInstall, _unresolved);
    }
}
