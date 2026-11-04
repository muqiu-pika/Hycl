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
    private bool _isInstalling;

    [ObservableProperty]
    private string _installingName = "";

    [ObservableProperty]
    private string _stageText = "";

    [ObservableProperty]
    private string _transferText = "";

    [ObservableProperty]
    private double _progressPercent;

    /// <summary>最近一次安装/下载的失败原因（成功或开始时清空），供详情页就地提示。</summary>
    [ObservableProperty]
    private string _errorText = "";

    /// <summary>安装结束（成功）后携带实际安装的版本目录名。</summary>
    public event EventHandler<string>? Completed;

    public async Task RunVanillaAsync(VersionSummary summary, string displayName)
    {
        await RunAsync(
            displayName,
            async (progress, token) =>
            {
                await _services.Installer.InstallVanillaAsync(
                        _services.Config.EffectiveGameDirectory,
                        summary,
                        DownloadPolicy.FromConfig(_services.Config),
                        _services.Config.DownloadThreads,
                        progress,
                        token)
                    .ConfigureAwait(true);
                return summary.Id;
            }).ConfigureAwait(true);
    }

    public async Task RunFabricAsync(string gameVersion, string displayName)
    {
        await RunAsync(
            displayName,
            async (progress, token) =>
                await _services.Installer.InstallFabricAsync(
                        _services.Config.EffectiveGameDirectory,
                        gameVersion,
                        DownloadPolicy.FromConfig(_services.Config),
                        _services.Config.DownloadThreads,
                        progress,
                        token)
                    .ConfigureAwait(true)).ConfigureAwait(true);
    }

    /// <summary>执行完整的加载器安装计划（主加载器 + 附属内容 + 前置 Mod）。</summary>
    public async Task RunPlanAsync(LoaderInstallPlan plan, string displayName)
    {
        await RunAsync(
            displayName,
            (progress, token) => _services.LoaderInstall.InstallAsync(plan, progress, token))
            .ConfigureAwait(true);
    }

    /// <summary>安装社区资源文件（Mod/资源包/光影/数据包 → 实例对应目录）。</summary>
    public async Task RunCompFileAsync(CompFile file, string instanceId, string sectionTitle)
    {
        await RunAsync(
            $"{file.DisplayName}（{sectionTitle}）",
            (progress, token) => _services.CompInstall
                .InstallFileAsync(file, _services.Config.EffectiveGameDirectory, instanceId, progress, token))
            .ConfigureAwait(true);
    }

    /// <summary>把社区资源文件保存到用户选择的目录（目录不存在时自动创建）。</summary>
    public async Task RunCompFileToDirectoryAsync(CompFile file, string targetDirectory, string sectionTitle)
    {
        await RunAsync(
            $"{file.DisplayName}（{sectionTitle}）",
            (progress, token) => _services.CompInstall
                .InstallFileToDirectoryAsync(file, targetDirectory, progress, token))
            .ConfigureAwait(true);
    }

    /// <summary>安装整合包（下载 → 解析 → 资源落盘 → 自动安装原版与加载器）。</summary>
    public async Task RunCompModpackAsync(CompFile file, string instanceName)
    {
        await RunAsync(
            $"整合包 {file.DisplayName}",
            (progress, token) => _services.CompInstall.InstallModpackAsync(
                file,
                _services.Config.EffectiveGameDirectory,
                instanceName,
                DownloadPolicy.FromConfig(_services.Config),
                _services.Config.DownloadThreads,
                progress,
                token))
            .ConfigureAwait(true);
    }

    private async Task RunAsync(
        string displayName,
        Func<IProgress<InstallProgress>, CancellationToken, Task<string>> action)
    {
        if (IsInstalling)
        {
            return;
        }

        _cts?.Cancel();
        _cts?.Dispose();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var generation = ++_runGeneration;
        IsInstalling = true;
        InstallingName = displayName;
        StageText = "准备中";
        TransferText = "";
        ProgressPercent = 0;
        ErrorText = "";
        try
        {
            var progress = new Progress<InstallProgress>(
                value => _services.Dispatcher.Post(() =>
                {
                    // 取消/新一轮任务后，丢弃仍在队列中的旧进度，避免遮罩文字“复活”
                    if (generation == _runGeneration && !cts.IsCancellationRequested)
                    {
                        ApplyProgress(value);
                    }
                }));
            var actionTask = action(progress, cts.Token);
            _actionTask = actionTask;
            var installedId = await actionTask.ConfigureAwait(true);
            StageText = "安装完成";
            ProgressPercent = 100;
            ErrorText = "";
            _toast?.Invoke($"安装完成：{installedId}");
            // 安装新实例后自动选定该实例（对应 PCL-CE ToolDownloadAutoSelectVersion）
            if (_services.Config.AutoSelectInstance)
            {
                _services.Config.SelectedVersion = installedId;
                await _services.ConfigStore.SaveAsync(cts.Token).ConfigureAwait(true);
            }

            Completed?.Invoke(this, installedId);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            StageText = "已取消";
            TransferText = "";
            ErrorText = "";
        }
        catch (Exception ex)
        {
            StageText = "安装失败";
            ErrorText = ex.Message;
            _toast?.Invoke($"安装失败：{ex.Message}");
            _services.Logger.Error("安装失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                _cts = null;
                _actionTask = null;
                IsInstalling = false;
                // 未成功的任务不把进度停在半途，回到可再次发起的状态
                ProgressPercent = StageText == "安装完成" ? 100 : 0;
            }

            cts.Dispose();
        }
    }

    private void ApplyProgress(InstallProgress progress)
    {
        StageText = progress.Stage;
        ProgressPercent = Math.Clamp(progress.Percentage * 100, 0, 100);
        var files = progress.FilesTotal > 0
            ? $"{progress.FilesDone}/{progress.FilesTotal} 个文件"
            : "";
        var bytes = progress.BytesTotal > 0
            ? $" · {FormatBytes(progress.BytesDone)} / {FormatBytes(progress.BytesTotal)}"
            : "";
        TransferText = files + bytes;
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
    /// 窗口关闭：无论处于哪个阶段都取消安装，并同步等待下载临时文件清理与半成品目录回滚完成，
    /// 避免进程退出时把残缺文件留在磁盘上。等待有上限（默认 1 秒），不会卡住关闭流程。
    /// </summary>
    public void CancelForShutdown(int timeoutMilliseconds = 1000)
    {
        var cts = _cts;
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        var task = _actionTask;
        if (task is null)
        {
            return;
        }

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
