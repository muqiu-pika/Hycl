using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Game.Comp;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>下载页左侧分区（安装包）的通用数据模型。</summary>
public partial class LoaderSectionViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<VersionGroupViewModel>>> _loader;
    private bool _loaded;
    private CancellationTokenSource? _refreshCts;

    public LoaderSectionViewModel(
        string kind,
        string title,
        string hint,
        Func<CancellationToken, Task<IReadOnlyList<VersionGroupViewModel>>> loader)
    {
        Kind = kind;
        Title = title;
        Hint = hint;
        _loader = loader;
        Groups = new ObservableCollection<VersionGroupViewModel>();
    }

    public string Kind { get; }

    public string Title { get; }

    public string Hint { get; }

    public ObservableCollection<VersionGroupViewModel> Groups { get; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isFailed;

    [ObservableProperty]
    private string _errorText = "";

    /// <summary>分区版本列表单选：只有被点中的条目保持选中，切换时清除上一条。</summary>
    public void SelectItem(VersionItemViewModel? item)
    {
        foreach (var candidate in Groups.SelectMany(group => group.Items))
        {
            candidate.IsSelected = ReferenceEquals(candidate, item);
        }
    }

    public async Task RefreshAsync(bool force = false)
    {
        if (_loaded && !force)
        {
            return;
        }

        CancelPending();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        IsLoading = true;
        IsFailed = false;
        ErrorText = "";
        try
        {
            var groups = await _loader(cts.Token).ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            Groups.Clear();
            foreach (var group in groups)
            {
                Groups.Add(group);
            }

            _loaded = true;
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // 切换分区/离开下载页时取消：保留已有列表，不写错误
        }
        catch (Exception ex)
        {
            IsFailed = true;
            ErrorText = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
                IsLoading = false;
            }

            cts.Dispose();
        }
    }

    /// <summary>取消在途的版本列表请求（切换分区或离开下载页时调用）。</summary>
    public void CancelPending()
    {
        var current = _refreshCts;
        _refreshCts = null;
        if (current is null)
        {
            return;
        }

        try
        {
            current.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已被其它路径释放
        }

        current.Dispose();
        IsLoading = false;
    }
}

/// <summary>一次安装/导入任务的结果；已有任务在跑时 Busy 为 true。</summary>
public sealed record InstallRunResult(string? InstalledId, bool Cancelled, Exception? Error)
{
    public bool Busy { get; init; }
}

/// <summary>
/// 底部安装任务状态：把现有 <see cref="Game.VersionInstaller"/> 的进度映射到
/// Hycl (2) 安装条的绑定字段。
/// </summary>
public partial class InstallTaskViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private CancellationTokenSource? _cts;
    private Task? _actionTask;
    private int _runGeneration;

    public InstallTaskViewModel(LauncherServices services, Action<string>? toast)
    {
        _services = services;
        _toast = toast;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFinishedWithError))]
    private bool _isInstalling;

    [ObservableProperty]
    private string _installingName = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFinishedWithError))]
    private string _stageText = "";

    [ObservableProperty]
    private string _transferText = "";

    [ObservableProperty]
    private double _progressPercent;

    /// <summary>总体进度：按已完成阶段与文件数加权估算，随阶段推进不会回退。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private double _overallProgressPercent;

    /// <summary>进度未知（刚入队/首次进度回调前）：界面显示不定态动画，避免停在 0% 看起来像卡死。</summary>
    public bool IsProgressIndeterminate => IsInstalling && OverallProgressPercent <= 0.5;

    /// <summary>任务成功结束：详情页据此做完成态收尾（绿色勾选 + 收尾渐变）。</summary>
    public bool IsCompleted => !IsInstalling && StageText == "安装完成";

    /// <summary>任务失败或取消结束：详情页使用对应错误/取消文案与收尾样式。</summary>
    public bool IsFinishedWithError => !IsInstalling && (StageText == "安装失败" || StageText == "已取消");

    /// <summary>当前阶段在整条安装链路中的序号与已完成阶段数，例如「第 3 步 · 已完成 2 步」。</summary>
    [ObservableProperty]
    private string _stepCounterText = "";

    /// <summary>当前这一步自身的完成比例，例如「本步 47%」。</summary>
    [ObservableProperty]
    private string _stepProgressText = "";

    /// <summary>当前正在下载的文件名 / 文件计数 / 剩余项数等细粒度信息。</summary>
    [ObservableProperty]
    private string _currentDetailText = "";

    /// <summary>本次安装已耗时（每秒刷新）。</summary>
    [ObservableProperty]
    private string _elapsedText = "";

    /// <summary>本次任务已出现的安装步骤（动态生成，已完成 / 当前 / 失败）。</summary>
    public ObservableCollection<LaunchStepItemViewModel> Steps { get; } = new();

    private string _currentStage = "";
    private int _expectedSteps = 2;
    private InstallProgressTracker _tracker = new(2);
    private DateTime _installStartedAt = DateTime.UtcNow;
    private Timer? _elapsedTimer;

    /// <summary>
    /// 进度节流闸门：worker 线程只累积“最新快照 + 本窗口内新增的步骤名”，
    /// UI 侧约每 120ms 合并应用一次。避免每个文件/每块字节都改十几个绑定文本并触发布局。
    /// </summary>
    private readonly object _progressGate = new();
    private readonly List<string> _pendingStages = new();
    private InstallProgress? _pendingProgress;
    private bool _progressPumpRunning;
    private bool _acceptProgress;
    private string _queuedStage = "准备中";

    /// <summary>最近一次安装/下载的失败原因（成功或开始时清空），供详情页就地提示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _errorText = "";

    /// <summary>是否有失败提示需要就地显示。</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorText);

    /// <summary>任务图标键：版本/加载器/整合包取对应 Minecraft 版本图标（与下载页版本列表同一套）。</summary>
    [ObservableProperty]
    private string _taskIconKey = "grass-block";

    /// <summary>社区资源自身的远程图标地址；为空或加载失败时回退到 <see cref="TaskIconKey"/> / 启动器图标。</summary>
    [ObservableProperty]
    private string? _taskIconUrl;

    /// <summary>安装结束（成功）后携带实际安装的版本目录名。</summary>
    public event EventHandler<string>? Completed;

    public async Task RunVanillaAsync(VersionSummary summary, string displayName)
    {
        await RunAsync(
            displayName,
            async (progress, token) =>
            {
                // 同名实例已存在时自动追加序号（26.3 → 26.3-1 → 26.3-2 …），绝不覆盖旧实例
                var targetName = InstanceNameValidator.GetAvailableName(
                    _services.Config.EffectiveGameDirectory, summary.Id);
                await _services.Installer.InstallVanillaAsync(
                        _services.Config.EffectiveGameDirectory,
                        summary,
                        DownloadPolicy.FromConfig(_services.Config),
                        _services.Config.DownloadThreads,
                        progress,
                        token,
                        targetName: targetName)
                    .ConfigureAwait(true);
                return targetName;
            },
            expectedSteps: 7,
            iconKey: InstallTaskIcons.FromVersionType(summary.Type)).ConfigureAwait(true);
    }

    public async Task RunFabricAsync(string gameVersion, string displayName)
    {
        await RunAsync(
            displayName,
            async (progress, token) =>
            {
                var targetName = InstanceNameValidator.GetAvailableName(
                    _services.Config.EffectiveGameDirectory, $"{gameVersion}-fabric");
                return await _services.Installer.InstallFabricAsync(
                        _services.Config.EffectiveGameDirectory,
                        gameVersion,
                        DownloadPolicy.FromConfig(_services.Config),
                        _services.Config.DownloadThreads,
                        progress,
                        token,
                        targetName)
                    .ConfigureAwait(true);
            },
            expectedSteps: FabricExpectedSteps(gameVersion),
            iconKey: InstallTaskIcons.FromGameVersion(gameVersion)).ConfigureAwait(true);
    }

    /// <summary>Fabric 安装：原版已就绪时 6 步；缺失时内部会补装原版（约多 5 步）。</summary>
    private int FabricExpectedSteps(string gameVersion)
    {
        var baseJson = Path.Combine(
            _services.Config.EffectiveGameDirectory,
            "versions",
            gameVersion,
            gameVersion + ".json");
        return File.Exists(baseJson) ? 6 : 11;
    }

    /// <summary>执行完整的加载器安装计划（主加载器 + 附属内容 + 前置 Mod）。</summary>
    public async Task RunPlanAsync(LoaderInstallPlan plan, string displayName)
    {
        await RunAsync(
            displayName,
            async (progress, token) =>
            {
                // 非法名称仍阻断；同名实例自动追加序号（与导入整合包命名语义一致）
                var targetNameError = InstanceNameValidator.ValidateName(plan.TargetName);
                if (targetNameError is not null)
                {
                    throw new DownloadException(targetNameError);
                }

                plan = plan with
                {
                    TargetName = InstanceNameValidator.GetAvailableName(
                        plan.GameDirectory, plan.TargetName)
                };
                return await _services.LoaderInstall.InstallAsync(plan, progress, token).ConfigureAwait(true);
            },
            expectedSteps: EstimatePlanSteps(plan),
            iconKey: plan.IconKey)
            .ConfigureAwait(true);
    }

    /// <summary>安装社区资源文件（Mod/资源包/光影/数据包 → 实例对应目录）。</summary>
    public async Task RunCompFileAsync(
        CompFile file,
        string instanceId,
        string sectionTitle,
        string? iconUrl = null,
        string iconKey = "forge")
    {
        await RunAsync(
            $"{file.DisplayName}（{sectionTitle}）",
            (progress, token) => _services.CompInstall
                .InstallFileAsync(
                    file,
                    _services.Config.EffectiveGameDirectory,
                    instanceId,
                    _services.Config.IsInstanceIsolated(instanceId),
                    progress,
                    token),
            expectedSteps: 3,
            iconKey: iconKey,
            iconUrl: iconUrl)
            .ConfigureAwait(true);
    }

    /// <summary>把社区资源文件保存到用户选择的目录（目录不存在时自动创建）。</summary>
    public async Task RunCompFileToDirectoryAsync(
        CompFile file,
        string targetDirectory,
        string sectionTitle,
        string? iconUrl = null,
        string iconKey = "forge")
    {
        await RunAsync(
            $"{file.DisplayName}（{sectionTitle}）",
            (progress, token) => _services.CompInstall
                .InstallFileToDirectoryAsync(file, targetDirectory, progress, token),
            expectedSteps: 3,
            iconKey: iconKey,
            iconUrl: iconUrl)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// 先安装前置再下载本体（对应 PCL-CE 依赖下载：前置失败不阻断本体，本体失败才整体失败）。
    /// </summary>
    public async Task RunCompFileWithDependenciesAsync(
        IReadOnlyList<CompFile> dependencies,
        CompFile mainFile,
        string targetDirectory,
        string sectionTitle,
        string? iconUrl = null,
        string iconKey = "forge")
    {
        await RunAsync(
            $"{mainFile.DisplayName}（{sectionTitle}）",
            async (progress, token) =>
            {
                foreach (var dependency in dependencies)
                {
                    try
                    {
                        await _services.CompInstall
                            .InstallFileToDirectoryAsync(dependency, targetDirectory, progress, token)
                            .ConfigureAwait(true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        _services.Logger.Warn($"前置 {dependency.DisplayName} 安装失败：{ex.Message}");
                        _toast?.Invoke($"前置安装失败：{dependency.DisplayName}");
                    }
                }

                return await _services.CompInstall
                    .InstallFileToDirectoryAsync(mainFile, targetDirectory, progress, token)
                    .ConfigureAwait(true);
            },
            expectedSteps: 3 + dependencies.Count * 2,
            iconKey: iconKey,
            iconUrl: iconUrl).ConfigureAwait(true);
    }

    /// <summary>安装整合包（下载 → 解析 → 资源落盘 → 自动安装原版与加载器）。</summary>
    public async Task RunCompModpackAsync(
        CompFile file,
        string instanceName,
        string? iconUrl = null,
        string? gameVersion = null)
    {
        // 整合包以对应的 Minecraft 版本图标为主；版本未知时才用资源自身图标。
        var resolvedVersion = gameVersion ?? file.GameVersions.FirstOrDefault();
        await RunAsync(
            $"整合包 {file.DisplayName}",
            (progress, token) => _services.CompInstall.InstallModpackAsync(
                file,
                _services.Config.EffectiveGameDirectory,
                instanceName,
                DownloadPolicy.FromConfig(_services.Config),
                _services.Config.DownloadThreads,
                progress,
                token),
            expectedSteps: 12,
            iconKey: InstallTaskIcons.FromGameVersion(resolvedVersion),
            iconUrl: resolvedVersion is null ? iconUrl : null)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// 导入本地整合包并注册为全局安装任务：右下角悬浮下载按钮、下载页任务卡片与取消都复用同一套状态。
    /// <paramref name="progressSink"/> 供导入页面同步自己的进度条（在 UI 线程回调）。
    /// </summary>
    public async Task<InstallRunResult> RunModpackImportAsync(
        string sourcePath,
        string instanceName,
        IReadOnlyCollection<long>? optionalFileIds,
        Action<InstallProgress>? progressSink = null,
        string? gameVersion = null)
    {
        return await RunAsync(
            $"整合包 {instanceName}",
            (progress, token) => _services.CompInstall.ImportModpackAsync(
                sourcePath,
                _services.Config.EffectiveGameDirectory,
                instanceName,
                DownloadPolicy.FromConfig(_services.Config),
                _services.Config.DownloadThreads,
                progress,
                token,
                optionalFileIds: optionalFileIds),
            progressSink,
            expectedSteps: 12,
            iconKey: InstallTaskIcons.FromGameVersion(gameVersion)).ConfigureAwait(true);
    }

    /// <summary>
    /// 按安装计划估算阶段数（准备 + 主链 + 附属内容），让「总体进度」在单原版、单加载器
    /// 等常见组合下不会因为固定按 8~10 步估算而长期停在半途。
    /// </summary>
    private static int EstimatePlanSteps(LoaderInstallPlan plan)
    {
        // 准备中 + 原版 6 步 / 加载器 6 步；缺失原版时补装会多出约 5 步
        var vanilla = string.Equals(plan.Kind, LoaderInstallService.VanillaKind, StringComparison.OrdinalIgnoreCase);
        var steps = vanilla ? 7 : 6;
        if (!vanilla)
        {
            var baseJson = Path.Combine(plan.GameDirectory, "versions", plan.GameVersion, plan.GameVersion + ".json");
            if (!File.Exists(baseJson))
            {
                steps += 5;
            }
        }



        if (plan.OptiFine is not null)
        {
            steps += 2;
        }

        if (plan.LiteLoader is not null)
        {
            steps += 1;
        }

        steps += plan.CompanionModSlugs.Count;
        return Math.Max(steps, 2);
    }

    private async Task<InstallRunResult> RunAsync(
        string displayName,
        Func<IProgress<InstallProgress>, CancellationToken, Task<string>> action,
        Action<InstallProgress>? progressSink = null,
        int expectedSteps = 2,
        string iconKey = "grass-block",
        string? iconUrl = null)
    {
        if (IsInstalling)
        {
            return new InstallRunResult(null, false, null) { Busy = true };
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var generation = ++_runGeneration;
        // 新一轮任务：清空上一轮可能残留的节流快照，旧泵醒来会因 generation 不匹配自行退出
        lock (_progressGate)
        {
            _pendingProgress = null;
            _pendingStages.Clear();
            _queuedStage = "准备中";
            _progressPumpRunning = false;
        }
        _acceptProgress = true;
        // 先写入任务图标，保证任务条/悬浮图标一出现就带着对应版本或资源图标
        TaskIconKey = iconKey;
        TaskIconUrl = iconUrl;
        IsInstalling = true;
        InstallingName = displayName;
        StageText = "准备中";
        TransferText = "";
        ProgressPercent = 0;
        OverallProgressPercent = 0;
        ErrorText = "";
        ResetSteps(expectedSteps);
        _installStartedAt = DateTime.UtcNow;
        ElapsedText = "已用 00:00";
        StartElapsedTimer(generation);
        try
        {
            // 同步回调 + 合并节流：不再为每条进度事件单独 Post 到 UI 线程
            var progress = new CallbackProgress<InstallProgress>(
                value => QueueProgress(value, cts, generation, progressSink));
            var actionTask = action(progress, cts.Token);
            _actionTask = actionTask;
            var installedId = await actionTask.ConfigureAwait(true);
            StopProgressUpdates();
            StageText = "安装完成";
            ProgressPercent = 100;
            OverallProgressPercent = 100;
            StepProgressText = "";
            CurrentDetailText = "";
            CompleteAllSteps();
            ErrorText = "";
            _toast?.Invoke($"下载完成：{installedId}");
            // 安装新实例后自动选定该实例（对应 PCL-CE ToolDownloadAutoSelectVersion）
            if (_services.Config.AutoSelectInstance)
            {
                _services.Config.SelectedVersion = installedId;
                await _services.ConfigStore.SaveAsync(cts.Token).ConfigureAwait(true);
            }

            Completed?.Invoke(this, installedId);
            return new InstallRunResult(installedId, false, null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StopProgressUpdates();
            StageText = "已取消";
            TransferText = "";
            StepProgressText = "";
            CurrentDetailText = "";
            MarkActiveStepFailed();
            ErrorText = "";
            _toast?.Invoke($"已取消：{InstallingName}");
            return new InstallRunResult(null, true, null);
        }
        catch (Exception ex)
        {
            StopProgressUpdates();
            StageText = "安装失败";
            StepProgressText = "";
            CurrentDetailText = "";
            MarkActiveStepFailed();
            ErrorText = ex.Message;
            _toast?.Invoke($"安装失败：{ex.Message}");
            _services.Logger.Error("安装失败", ex);
            return new InstallRunResult(null, false, ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                StopProgressUpdates();
                _cts = null;
                _actionTask = null;
                StopElapsedTimer();
                IsInstalling = false;
                // 未成功的任务不把进度停在半途，回到可再次发起的状态
                ProgressPercent = StageText == "安装完成" ? 100 : 0;
                if (StageText != "安装完成")
                {
                    OverallProgressPercent = 0;
                }
            }

            cts.Dispose();
        }
    }

    /// <summary>新一轮任务开始：清空旧步骤，先进「准备中」。</summary>
    private void ResetSteps(int expectedSteps)
    {
        _expectedSteps = Math.Max(expectedSteps, 2);
        _currentStage = "准备中";
        _tracker = new InstallProgressTracker(_expectedSteps, _currentStage);
        Steps.Clear();
        AddStep("准备中");
        StepCounterText = $"第 1/{_expectedSteps} 步 · 已完成 0 步 · 还剩 {_expectedSteps - 1} 步";
        StepProgressText = "";
        CurrentDetailText = "";
    }

    private void AddStep(string title)
    {
        Steps.Add(new LaunchStepItemViewModel(Steps.Count + 1, title)
        {
            Status = LaunchStepStatus.Active
        });
    }

    /// <summary>当前步骤结束：标记为已完成，供总体进度按步数推进。</summary>
    private void CompleteActiveStep()
    {
        if (Steps.Count > 0 && Steps[^1].Status != LaunchStepStatus.Completed)
        {
            Steps[^1].Status = LaunchStepStatus.Completed;
        }
    }

    private void CompleteAllSteps()
    {
        foreach (var step in Steps)
        {
            step.Status = LaunchStepStatus.Completed;
        }

        StepCounterText = $"共 {Steps.Count} 步 · 已完成 {Steps.Count} 步";
    }

    private void MarkActiveStepFailed()
    {
        if (Steps.Count > 0 && Steps[^1].Status == LaunchStepStatus.Active)
        {
            Steps[^1].Status = LaunchStepStatus.Failed;
        }
    }

    private void StartElapsedTimer(int generation)
    {
        StopElapsedTimer();
        _elapsedTimer = new Timer(
            _ => _services.Dispatcher.Post(() =>
            {
                if (generation == _runGeneration && IsInstalling)
                {
                    ElapsedText = FormatElapsed(DateTime.UtcNow - _installStartedAt);
                }
            }),
            null,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(1));
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Dispose();
        _elapsedTimer = null;
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        var totalSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"已用 {minutes:00}:{seconds:00}";
    }

    /// <summary>
    /// 进度回调（worker 线程）：只记录最新快照与步骤切换，并确保只有一个节流泵在运行。
    /// 文件数、字节数高频变化不会被逐个推给 UI。
    /// </summary>
    private void QueueProgress(
        InstallProgress value,
        CancellationTokenSource cts,
        int generation,
        Action<InstallProgress>? progressSink)
    {
        lock (_progressGate)
        {
            if (generation != _runGeneration || !_acceptProgress)
            {
                return;
            }

            if (!string.IsNullOrEmpty(value.Stage)
                && !string.Equals(value.Stage, _queuedStage, StringComparison.Ordinal))
            {
                _queuedStage = value.Stage;
                _pendingStages.Add(value.Stage);
            }

            _pendingProgress = value;
            if (_progressPumpRunning)
            {
                return;
            }

            _progressPumpRunning = true;
        }

        _ = PumpProgressAsync(cts, generation, progressSink);
    }

    /// <summary>每 120ms 把窗口内累积的最新进度合并应用到 UI，避免每文件/每字节触发布局。</summary>
    private async Task PumpProgressAsync(
        CancellationTokenSource cts,
        int generation,
        Action<InstallProgress>? progressSink)
    {
        while (true)
        {
            await Task.Delay(120).ConfigureAwait(false);

            InstallProgress snapshot;
            string[] stages;
            lock (_progressGate)
            {
                if (generation != _runGeneration)
                {
                    // 已被新一轮任务接管：新泵负责后续刷新，旧泵不再写状态标志
                    return;
                }

                if (_pendingProgress is null)
                {
                    _progressPumpRunning = false;
                    return;
                }

                snapshot = _pendingProgress!;
                _pendingProgress = null;
                stages = _pendingStages.Count == 0 ? Array.Empty<string>() : _pendingStages.ToArray();
                _pendingStages.Clear();
            }

            _services.Dispatcher.Post(() => ApplyProgressBatch(snapshot, stages, cts, generation, progressSink));
        }
    }

    /// <summary>任务结束：丢弃窗口内的残留快照，之后到达的旧进度一律忽略。</summary>
    private void StopProgressUpdates()
    {
        _acceptProgress = false;
        lock (_progressGate)
        {
            _pendingProgress = null;
            _pendingStages.Clear();
        }
    }

    /// <summary>
    /// 把一批节流后的进度应用到 UI（UI 线程）。窗口内跨过的中间步骤仍按顺序补记，
    /// 保证「第 N 步 / 共 M 步」与总进度不会少算一步。
    /// </summary>
    private void ApplyProgressBatch(
        InstallProgress snapshot,
        string[] stages,
        CancellationTokenSource cts,
        int generation,
        Action<InstallProgress>? progressSink)
    {
        if (generation != _runGeneration || cts.IsCancellationRequested || !IsInstalling || !_acceptProgress)
        {
            return;
        }

        foreach (var stage in stages)
        {
            if (string.Equals(stage, _currentStage, StringComparison.Ordinal))
            {
                continue;
            }

            CompleteActiveStep();
            _currentStage = stage;
            AddStep(stage);
            if (!string.Equals(stage, snapshot.Stage, StringComparison.Ordinal))
            {
                // 中间步骤在节流窗口内一闪而过：推进步骤序号（本步进度按 0 计）
                _tracker.Update(new InstallProgress(stage, "", 0, 0, 0, 0));
            }
        }

        var aggregated = ApplyProgress(snapshot);
        progressSink?.Invoke(aggregated);
    }

    private InstallProgress ApplyProgress(InstallProgress progress)
    {
        // 阶段切换：上一阶段标记完成，新阶段追加到步骤列表并保持高亮
        if (!string.IsNullOrEmpty(progress.Stage) && !string.Equals(progress.Stage, _currentStage, StringComparison.Ordinal))
        {
            CompleteActiveStep();
            _currentStage = progress.Stage;
            AddStep(progress.Stage);
        }

        // 统一进度模型：本步按文件数（字节兜底），总进度按步骤合成且单调不减
        var aggregated = _tracker.Update(progress);
        StageText = aggregated.Stage;
        ProgressPercent = aggregated.Percentage * 100;
        StepProgressText = $"本步 {ProgressPercent:0}%";

        var files = aggregated.FilesTotal > 0
            ? $"{aggregated.FilesDone}/{aggregated.FilesTotal} 个文件"
            : "";
        var bytes = aggregated.BytesTotal > 0
            ? $"{FormatBytes(aggregated.BytesDone)} / {FormatBytes(aggregated.BytesTotal)}"
            : "";
        TransferText = string.Join(" · ", new[] { files, bytes }.Where(x => x.Length > 0));

        var remaining = aggregated.RemainingFiles > 0
            ? $"剩余 {aggregated.RemainingFiles} 个文件"
            : "";
        var detail = string.Join(" · ", new[] { aggregated.CurrentItem, files, remaining }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        CurrentDetailText = detail.Length > 0 ? detail : "正在处理本地文件…";

        StepCounterText =
            $"第 {aggregated.StepIndex}/{aggregated.StepCount} 步 · " +
            $"已完成 {Math.Max(0, aggregated.StepIndex - 1)} 步 · " +
            $"还剩 {aggregated.RemainingSteps} 步";
        OverallProgressPercent = aggregated.OverallPercent;
        return aggregated;
    }

    private static string FormatBytes(double bytes)
    {
        if (bytes <= 0)
        {
            return "0 B";
        }

        var megabytes = bytes / (1024d * 1024d);
        return megabytes >= 1
            ? $"{megabytes:0.0} MB"
            : $"{Math.Max(1, (int)(bytes / 1024d))} KB";
    }

    public void Cancel() => _cts?.Cancel();

    /// <summary>
    /// 是否仍处于可安全取消的阶段：准备中、获取版本/加载器信息、检查原版、下载…。
    /// 这些阶段只读取元数据或写入可丢弃的临时文件；解压整合包、安装原版/加载器、复制资源
    /// 等阶段取消会留下半成品实例或残缺资源，需继续完成后再处理。
    /// </summary>
    public bool IsInCancellableDownloadStage =>
        IsInstalling
        && _cts is not null
        && (StageText == "准备中"
            || StageText.StartsWith("获取", StringComparison.Ordinal)
            || StageText == "检查原版"
            || StageText.StartsWith("下载", StringComparison.Ordinal));

    /// <summary>页面返回/切换等辅助路径：只在可安全取消阶段取消；解压与安装阶段保持继续。返回是否已发起取消。</summary>
    public bool CancelIfDownloading()
    {
        if (!IsInCancellableDownloadStage)
        {
            return false;
        }

        _cts!.Cancel();
        return true;
    }

    /// <summary>
    /// 窗口关闭：可安全取消的阶段（准备/获取信息/下载）立即取消并等待临时文件清理与目录回滚；
    /// 解压整合包、安装原版/加载器等不可逆阶段不取消，改为等待其完成——强杀会留下半成品实例目录
    /// （回滚逻辑以任务自身收尾为准）。等待有上限，不会长时间卡住关闭流程。
    /// </summary>
    public void CancelForShutdown(int timeoutMilliseconds = 3000)
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        var task = _actionTask;
        if (task is null)
        {
            cts.Cancel();
            return;
        }

        if (!IsInCancellableDownloadStage)
        {
            // 不可逆阶段：等待任务自然完成（本地解压/落盘通常秒级），超时则放弃等待
            try
            {
                task.Wait(timeoutMilliseconds * 3);
            }
            catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
            {
                // 收尾异常不影响关闭
            }

            return;
        }

        cts.Cancel();
        try
        {
            // 安装/下载的收尾逻辑都走 ConfigureAwait(false)，不依赖 UI 线程，因此这里可以安全阻塞等待
            task.Wait(timeoutMilliseconds);
        }
        catch (Exception ex) when (ex is AggregateException or OperationCanceledException)
        {
            // 收尾异常不影响关闭
        }
    }

    [RelayCommand]
    private void CancelInstall() => Cancel();
}

/// <summary>
/// 安装任务图标映射：与下载页版本列表使用同一套图标键
/// （正式版=草方块 / 快照=命令方块 / 远古版本=圆石）。
/// </summary>
internal static class InstallTaskIcons
{
    public static string FromVersionType(string? type)
    {
        if (type is null)
        {
            return "grass-block";
        }

        if (type.StartsWith("old", StringComparison.OrdinalIgnoreCase))
        {
            return "cobblestone";
        }

        return type.Equals("snapshot", StringComparison.OrdinalIgnoreCase) ? "command-block" : "grass-block";
    }

    /// <summary>按版本号推断分类图标（加载器安装/整合包只拿到版本号字符串时使用）。</summary>
    public static string FromGameVersion(string? gameVersion)
    {
        var id = gameVersion?.Trim().ToLowerInvariant() ?? "";
        if (id.Length == 0)
        {
            return "grass-block";
        }

        // 远古版本：a1.2.6 / b1.7.3 / c0.30 / inf-20100618 / rd-132211 / 0.x
        if (id.StartsWith("inf-", StringComparison.Ordinal)
            || id.StartsWith("rd-", StringComparison.Ordinal)
            || (id.Length > 1 && "abc".Contains(id[0]) && char.IsDigit(id[1]))
            || id[0] == '0')
        {
            return "cobblestone";
        }

        // 快照：23w31a / 1.21.2-pre1 / 26.4-snapshot-7
        if (id.Contains("snapshot")
            || id.Contains("-pre")
            || id.Contains("-rc")
            || (char.IsDigit(id[0]) && id.Contains('w')))
        {
            return "command-block";
        }

        return "grass-block";
    }
}
