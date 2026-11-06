using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Game;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>主页：账号卡片、版本选择、启动与游戏日志。</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly StringBuilder _logBuilder = new();
    private GameSession? _session;
    private bool _suppressSelectionPersist;

    public HomeViewModel(LauncherServices services)
    {
        _services = services;
        Instances.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(IsInstanceListEmpty));
            OnPropertyChanged(nameof(InstanceCountText));
        };
    }

    public ObservableCollection<MinecraftInstance> Instances { get; } = new();

    [ObservableProperty]
    private MinecraftInstance? _selectedInstance;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private string _launchStatus = "准备就绪";

    [ObservableProperty]
    private string _latestLog = "";

    [ObservableProperty]
    private bool _isAccountEditing;

    [ObservableProperty]
    private string _accountNameInput = "";

    public PlayerAccount? CurrentAccount => _services.Accounts.SelectedAccount;

    public string PlayerName => CurrentAccount?.Name ?? "未登录";

    public string PlayerAvatar => CurrentAccount?.AvatarText ?? "?";

    public string AccountSubtitle => CurrentAccount is null
        ? "尚无账号，请在启动页添加"
        : "离线账户 · " + (CurrentAccount.Uuid.Length >= 8 ? CurrentAccount.Uuid[..8] : CurrentAccount.Uuid);

    public bool HasInstances => Instances.Count > 0;

    public bool IsInstanceListEmpty => !HasInstances;

    public string InstanceCountText => Instances.Count == 0 ? "暂无版本" : $"共 {Instances.Count} 个版本";

    public string SelectedVersionName => SelectedInstance?.Description ?? "未选择版本";

    public bool CanLaunch => !IsLaunching && SelectedInstance is not null;

    public string LaunchButtonText => IsLaunching ? "游戏运行中" : "启动游戏";

    public bool HasLog => LatestLog.Length > 0;

    public bool IsLogEmpty => !HasLog;

    public string MemoryText => $"{_services.Config.GetEffectiveMemoryMegabytes()} MB";

    public string GameDirectoryText => _services.Config.EffectiveGameDirectory;

    public string LaunchHint => IsLaunching
        ? "游戏进程正在运行，可继续查看日志。"
        : "在「下载」页安装版本后即可启动。";

    public event EventHandler? CloseRequested;

    [RelayCommand]
    public async Task RefreshInstancesAsync()
    {
        IsRefreshing = true;
        try
        {
            var directory = _services.Config.EffectiveGameDirectory;
            var instances = await _services.Instances.ScanAsync(directory).ConfigureAwait(true);
            var previousId = SelectedInstance?.Id ?? _services.Config.SelectedVersion;
            _suppressSelectionPersist = true;
            Instances.Clear();
            foreach (var instance in instances)
            {
                Instances.Add(instance);
            }

            SelectedInstance = Instances.FirstOrDefault(x => x.Id.Equals(previousId, StringComparison.OrdinalIgnoreCase))
                               ?? Instances.FirstOrDefault();
            _suppressSelectionPersist = false;

            if (SelectedInstance is not null)
            {
                _services.Config.SelectedVersion = SelectedInstance.Id;
                await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
            }

            IsRefreshing = false;
            OnPropertyChanged(nameof(HasInstances));
            OnPropertyChanged(nameof(IsInstanceListEmpty));
            OnPropertyChanged(nameof(InstanceCountText));
            OnPropertyChanged(nameof(SelectedVersionName));
            OnPropertyChanged(nameof(CanLaunch));
            OnPropertyChanged(nameof(MemoryText));
            OnPropertyChanged(nameof(GameDirectoryText));
            if (!HasInstances && !IsLaunching)
            {
                LaunchStatus = "尚未安装版本，请前往「下载」安装";
            }
            else if (!IsLaunching)
            {
                LaunchStatus = "准备就绪";
            }
        }
        catch (Exception ex)
        {
            IsRefreshing = false;
            LaunchStatus = $"读取版本列表失败：{ex.Message}";
            _services.Logger.Error("读取版本列表失败", ex);
        }
    }

    public void SelectInstance(string instanceId)
    {
        var match = Instances.FirstOrDefault(x => x.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            SelectedInstance = match;
        }
    }

    [RelayCommand]
    private void BeginAccountEdit()
    {
        AccountNameInput = PlayerName;
        IsAccountEditing = true;
    }

    [RelayCommand]
    private void CancelAccountEdit()
    {
        IsAccountEditing = false;
    }

    [RelayCommand]
    private async Task SaveAccountAsync()
    {
        var name = AccountNameInput.Trim();
        if (name.Length is < 1 or > 16)
        {
            LaunchStatus = "玩家名长度需为 1–16 个字符";
            return;
        }

        if (CurrentAccount is not { } account)
        {
            _services.Accounts.AddOffline(name);
        }
        else
        {
            account.Name = name;
            account.Uuid = OfflineUuid.Create(name);
        }

        await _services.Accounts.SaveAsync().ConfigureAwait(true);
        _services.Config.UserName = name;
        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        IsAccountEditing = false;
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(PlayerAvatar));
        OnPropertyChanged(nameof(AccountSubtitle));
        LaunchStatus = $"已切换离线账户：{name}";
    }

    [RelayCommand]
    public async Task LaunchAsync()
    {
        if (IsLaunching)
        {
            return;
        }

        var instance = SelectedInstance;
        if (instance is null)
        {
            LaunchStatus = "请先选择要启动的版本";
            return;
        }

        var account0 = _services.Accounts.SelectedAccount;
        if (account0 is null)
        {
            LaunchStatus = "请先在启动页添加账号（正版 / 皮肤站 / 离线）";
            return;
        }

        IsLaunching = true;
        LaunchStatus = "正在启动…";
        AppendLog($"[HYCL] 正在启动 {instance.Name}…");
        try
        {
            var config = _services.Config;

            // 皮肤站账号：校验令牌（失效则 refresh），并准备 authlib-injector 注入材料（与启动页一致）
            string? authlibJar = null;
            string? authlibPrefetched = null;
            if (account0.Type == "authlib")
            {
                try
                {
                    if (!await _services.AuthlibAuth
                            .ValidateAsync(account0.Server, account0.AccessToken, account0.ClientToken)
                            .ConfigureAwait(true))
                    {
                        var refreshed = await _services.AuthlibAuth.RefreshAsync(account0).ConfigureAwait(true);
                        AccountStore.ApplyAuthlibAuth(account0, refreshed);
                        await _services.Accounts.SaveAsync().ConfigureAwait(true);
                    }

                    authlibJar = await _services.AuthlibAuth
                        .EnsureAuthlibInjectorJarAsync(_services.Paths.CacheDirectory, _services.DownloadClient)
                        .ConfigureAwait(true);
                    try
                    {
                        // 预取只是加速项，拿不到不影响 javaagent 注入
                        authlibPrefetched = await _services.AuthlibAuth
                            .GetPrefetchedAsync(account0.Server)
                            .ConfigureAwait(true);
                    }
                    catch (Exception ex)
                    {
                        _services.Logger.Warn($"皮肤站预取数据获取失败（不影响注入）：{ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    LaunchStatus = $"皮肤站账号准备失败：{ex.Message}";
                    AppendLog("[错误] " + LaunchStatus);
                    _services.Logger.Error("皮肤站账号准备失败", ex);
                    IsLaunching = false;
                    return;
                }
            }

            var options = LaunchOptionsBuilder.Build(config, instance, account0, authlibJar, authlibPrefetched);

            var session = await _services.Launch.LaunchAsync(options, AppendLog).ConfigureAwait(true);
            _session = session;
            LaunchStatus = $"游戏已启动（PID {session.Id}）";
            AppendLog($"[HYCL] 游戏进程已启动，PID={session.Id}");
            if (config.LauncherVisibility == 0 || config.CloseLauncherAfterLaunch)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }

            _ = WatchSessionAsync(session);
        }
        catch (LaunchException ex)
        {
            IsLaunching = false;
            LaunchStatus = ex.Message;
            AppendLog("[错误] " + ex.Message);
            _services.Logger.Error("启动游戏失败", ex);
        }
        catch (Exception ex)
        {
            IsLaunching = false;
            LaunchStatus = $"启动失败：{ex.Message}";
            AppendLog("[错误] " + ex.Message);
            _services.Logger.Error("启动游戏失败", ex);
        }
    }

    [RelayCommand]
    private void StopGame()
    {
        if (_session is null)
        {
            return;
        }

        LaunchStatus = "正在结束游戏进程…";
        _session.Stop();
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var target = SelectedInstance?.Directory ?? _services.Config.EffectiveGameDirectory;
        _services.System.OpenFolder(target);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        _services.System.OpenFolder(Path.GetDirectoryName(_services.Logger.LogFilePath) ?? _services.Paths.DataDirectory);
    }

    public void AppendLog(string line)
    {
        _services.Dispatcher.Post(() => AppendLogCore(line));
    }

    private void AppendLogCore(string line)
    {
        _logBuilder.AppendLine(line);
        if (_logBuilder.Length > 50000)
        {
            _logBuilder.Remove(0, _logBuilder.Length - 40000);
        }

        LatestLog = _logBuilder.ToString();
        OnPropertyChanged(nameof(HasLog));
        OnPropertyChanged(nameof(IsLogEmpty));
    }

    private async Task WatchSessionAsync(GameSession session)
    {
        try
        {
            await session.WaitForExitAsync().ConfigureAwait(false);
            var exitCode = session.ExitCode;
            _services.Dispatcher.Post(() =>
            {
                if (_session == session)
                {
                    _session = null;
                }

                IsLaunching = false;
                LaunchStatus = $"游戏已退出（退出码 {exitCode}）";
                AppendLogCore($"[HYCL] 游戏已退出，退出码 {exitCode}");
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"等待游戏退出时出错：{ex.Message}");
            _services.Dispatcher.Post(() =>
            {
                if (_session == session)
                {
                    _session = null;
                }

                IsLaunching = false;
            });
        }
        finally
        {
            session.Dispose();
        }
    }

    partial void OnSelectedInstanceChanged(MinecraftInstance? value)
    {
        OnPropertyChanged(nameof(SelectedVersionName));
        OnPropertyChanged(nameof(CanLaunch));

        if (_suppressSelectionPersist)
        {
            return;
        }

        if (value is not null)
        {
            _services.Config.SelectedVersion = value.Id;
            _ = _services.ConfigStore.SaveAsync();
        }

        if (!IsLaunching)
        {
            LaunchStatus = value is null ? "尚未选择版本" : "准备就绪";
        }
    }

    partial void OnIsLaunchingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        OnPropertyChanged(nameof(LaunchButtonText));
        OnPropertyChanged(nameof(LaunchHint));
    }
}
