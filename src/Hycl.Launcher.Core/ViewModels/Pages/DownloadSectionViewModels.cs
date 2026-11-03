using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Download;
using Hycl.Launcher.Core.Game.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>下载页左侧分区（安装包）的通用数据模型。</summary>
public partial class LoaderSectionViewModel : ObservableObject
{
    private readonly Func<Task<IReadOnlyList<VersionGroupViewModel>>> _loader;
    private bool _loaded;

    public LoaderSectionViewModel(
        string kind,
        string title,
        string hint,
        Func<Task<IReadOnlyList<VersionGroupViewModel>>> loader)
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

    public async Task RefreshAsync(bool force = false)
    {
        if (_loaded && !force)
        {
            return;
        }

        IsLoading = true;
        IsFailed = false;
        ErrorText = "";
        try
        {
            var groups = await _loader().ConfigureAwait(true);
            Groups.Clear();
            foreach (var group in groups)
            {
                Groups.Add(group);
            }

            _loaded = true;
        }
        catch (Exception ex)
        {
            IsFailed = true;
            ErrorText = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
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

    private async Task RunAsync(
        string displayName,
        Func<IProgress<InstallProgress>, CancellationToken, Task<string>> action)
    {
        if (IsInstalling)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        IsInstalling = true;
        InstallingName = displayName;
        StageText = "准备中";
        TransferText = "";
        ProgressPercent = 0;
        try
        {
            var progress = new Progress<InstallProgress>(
                value => _services.Dispatcher.Post(() => ApplyProgress(value)));
            var installedId = await action(progress, _cts.Token).ConfigureAwait(true);
            StageText = "安装完成";
            ProgressPercent = 100;
            _toast?.Invoke($"安装完成：{installedId}");
            Completed?.Invoke(this, installedId);
        }
        catch (OperationCanceledException)
        {
            StageText = "已取消";
        }
        catch (Exception ex)
        {
            StageText = "安装失败";
            _toast?.Invoke($"安装失败：{ex.Message}");
            _services.Logger.Error("安装失败", ex);
        }
        finally
        {
            IsInstalling = false;
            _cts?.Dispose();
            _cts = null;
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

    [RelayCommand]
    private void CancelInstall() => Cancel();
}
