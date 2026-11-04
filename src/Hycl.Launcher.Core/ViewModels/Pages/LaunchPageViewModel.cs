using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Auth;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 启动页 ViewModel：玩家档案切换、当前版本展示、启动与快捷方式。
/// 表现层数据来自 Hycl (2) 的启动页，业务调用继续使用现有 Core 的实例扫描与启动服务。
/// </summary>
public partial class LaunchPageViewModel : ObservableObject, IPageLifecycle
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private GameSession? _session;
    private MinecraftInstance? _selectedInstance;
    private bool _suppressInstanceSelection;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _progressCts;

    public LaunchPageViewModel(LauncherServices services, Action<string>? toast = null)
    {
        _services = services;
        _toast = toast;
        Profiles = new ObservableCollection<PlayerProfileViewModel>();
        BuildProfiles();
    }

    public ObservableCollection<PlayerProfileViewModel> Profiles { get; }

    [ObservableProperty]
    private PlayerProfileViewModel? _activeProfile;

    /// <summary>左栏当前显示“玩家”面板还是“档案”面板。</summary>
    [ObservableProperty]
    private bool _isPlayerPanel = true;

    [ObservableProperty]
    private string _leftPanelTitle = "玩家";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchText))]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [NotifyPropertyChangedFor(nameof(PrimaryEnabled))]
    [NotifyPropertyChangedFor(nameof(LaunchIconKey))]
    [NotifyPropertyChangedFor(nameof(LaunchSubText))]
    private bool _isLaunching;

    /// <summary>游戏进程是否仍在运行（运行中按钮切换为「关闭游戏」）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchText))]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [NotifyPropertyChangedFor(nameof(PrimaryEnabled))]
    [NotifyPropertyChangedFor(nameof(LaunchIconKey))]
    [NotifyPropertyChangedFor(nameof(LaunchSubText))]
    private bool _isGameRunning;

    /// <summary>是否正在优雅关闭游戏（关闭期间禁用按钮，避免重复触发）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchText))]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [NotifyPropertyChangedFor(nameof(PrimaryEnabled))]
    [NotifyPropertyChangedFor(nameof(LaunchIconKey))]
    private bool _isClosing;

    /// <summary>启动阶段提示（由 LaunchService 在准备各阶段回调）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchSubText))]
    private string _launchStageText = "";

    /// <summary>启动进度（0~1）：阶段式平滑推进，不使用不定进度条。</summary>
    [ObservableProperty]
    private double _launchProgress;

    /// <summary>关闭游戏确认弹窗是否显示（键盘与焦点管理由 UI 层负责）。</summary>
    [ObservableProperty]
    private bool _isCloseGameConfirmVisible;

    public bool CanLaunch => !IsLaunching && !IsGameRunning && !IsClosing;

    /// <summary>按钮是否可点击：启动中/关闭中禁用，运行中可点击（用于关闭游戏）。</summary>
    public bool PrimaryEnabled => !IsLaunching && !IsClosing;

    /// <summary>已安装实例列表（仅内部维护，启动页按当前选中实例展示）。</summary>
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];

    /// <summary>启动页版本下拉框的选项（实例目录名）。</summary>
    public ObservableCollection<string> InstanceNames { get; } = [];

    /// <summary>启动页版本下拉框的当前项；切换即写入配置并刷新展示。</summary>
    [ObservableProperty]
    private string? _selectedInstanceName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionSummary))]
    [NotifyPropertyChangedFor(nameof(LaunchSubText))]
    private string _currentVersion = "未安装";

    [ObservableProperty]
    private string _currentVersionTag = "无";

    /// <summary>当前版本的 Mod 加载器图标键（vanilla / forge / neoforge / fabric / quilt / optifine / liteloader）。</summary>
    [ObservableProperty]
    private string _loaderIconKey = "vanilla";

    /// <summary>加载器图标强调色。</summary>
    [ObservableProperty]
    private string _loaderColor = "#3E9B4F";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionSummary))]
    [NotifyPropertyChangedFor(nameof(LaunchSubText))]
    private string _loaderName = "Vanilla";

    /// <summary>启动按钮副标题：版本号 + 加载器。</summary>
    public string VersionSummary => $"{CurrentVersion} · {LoaderName}";

    public string LaunchText => IsClosing
        ? "正在关闭…"
        : IsLaunching
            ? "正在启动…"
            : IsGameRunning
                ? "关闭游戏"
                : "启动游戏";

    /// <summary>按钮图标键：运行中显示电源图标，其余显示播放图标。</summary>
    public string LaunchIconKey => !IsLaunching && !IsClosing && IsGameRunning ? "power" : "play";

    /// <summary>按钮副标题：启动中显示阶段提示，运行中提示可关闭，其余显示版本信息。</summary>
    public string LaunchSubText => IsLaunching
        ? LaunchStageText
        : IsGameRunning
            ? "游戏运行中 · 点击关闭"
            : VersionSummary;

    /// <summary>本次运行内累计启动次数（对应 PCL-CE 的“启动次数”信息项）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LaunchCountText))]
    private int _launchCount;

    public string LaunchCountText => LaunchCount == 0 ? "从未启动" : $"{LaunchCount} 次";

    /// <summary>快捷方式条目（对应 Hycl (2) 启动页的 quickLinks）。</summary>
    public IReadOnlyList<QuickLinkViewModel> QuickLinks { get; } =
    [
        new("版本列表", "管理已安装的游戏版本", "list"),
        new("下载游戏", "获取官方客户端与 Mod", "download"),
        new("快速联机", "与好友一起进入同一世界", "network"),
        new("导入整合包", "一键安装 CurseForge 整合包", "package"),
    ];

    public event EventHandler? CloseRequested;

    /// <summary>游戏启动后的启动器行为（0 关闭 / 4 最小化 / 其余保持）。</summary>
    public event EventHandler<int>? LauncherVisibilityChanged;

    /// <summary>请求切换到其他标签页（“下载游戏”快捷方式使用）。</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>
    /// 重新扫描 versions 目录并刷新当前版本；安装完成或进入启动页时调用。
    /// preferredId 为新安装实例的目录名，未指定时优先使用配置中的已选版本。
    /// </summary>
    public async Task RefreshInstancesAsync(string? preferredId = null)
    {
        CancelAndDisposeRefresh();
        var cts = new CancellationTokenSource();
        _refreshCts = cts;
        try
        {
            var list = await _services.Instances
                .ScanAsync(_services.Config.EffectiveGameDirectory, cts.Token)
                .ConfigureAwait(true);
            cts.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_refreshCts, cts))
            {
                return;
            }

            Instances.Clear();
            InstanceNames.Clear();
            foreach (var instance in list)
            {
                Instances.Add(instance);
                InstanceNames.Add(instance.Id);
            }

            var target = FindTarget(list, preferredId);
            _selectedInstance = target;
            _suppressInstanceSelection = true;
            SelectedInstanceName = target?.Id;
            _suppressInstanceSelection = false;
            ApplyInstance(target);
            if (target is null)
            {
                return;
            }

            if (!target.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                _services.Config.SelectedVersion = target.Id;
                await _services.ConfigStore.SaveAsync(cts.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // 离开启动页导致的正常取消：不写错误，返回后可直接再次刷新
        }
        catch (Exception ex)
        {
            _services.Logger.Error("刷新启动页实例失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_refreshCts, cts))
            {
                _refreshCts = null;
            }

            cts.Dispose();
        }
    }

    /// <summary>离开启动页：取消在途的实例扫描/配置保存。</summary>
    public void CancelPending()
    {
        CancelAndDisposeRefresh();
    }

    /// <summary>进入启动页：实例刷新由 MainViewModel 触发，这里无需额外动作。</summary>
    public void Activate()
    {
    }

    /// <summary>离开启动页：取消在途的实例扫描/配置保存。</summary>
    public void Deactivate()
    {
        CancelPending();
    }

    private void CancelAndDisposeRefresh()
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
    }

    private MinecraftInstance? FindTarget(IReadOnlyList<MinecraftInstance> list, string? preferredId)
    {
        if (list.Count == 0)
        {
            return null;
        }

        var wanted = preferredId ?? _services.Config.SelectedVersion;
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            var match = list.FirstOrDefault(x => x.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return list[0];
    }

    /// <summary>外部（版本管理页）切换当前实例后，启动页同步选中项。</summary>
    public async Task SelectInstanceAsync(string instanceId)
    {
        var match = Instances.FirstOrDefault(x => x.Id.Equals(instanceId, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            await RefreshInstancesAsync(instanceId).ConfigureAwait(true);
            return;
        }

        _selectedInstance = match;
        _suppressInstanceSelection = true;
        SelectedInstanceName = match.Id;
        _suppressInstanceSelection = false;
        ApplyInstance(match);
        if (!match.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            _services.Config.SelectedVersion = match.Id;
            await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        }
    }

    partial void OnSelectedInstanceNameChanged(string? value)
    {
        if (_suppressInstanceSelection || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var instance = Instances.FirstOrDefault(x => x.Id.Equals(value, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            return;
        }

        _selectedInstance = instance;
        ApplyInstance(instance);
        if (!instance.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase))
        {
            _services.Config.SelectedVersion = instance.Id;
            _ = _services.ConfigStore.SaveAsync();
        }
    }

    private void ApplyInstance(MinecraftInstance? target)
    {
        if (target is null)
        {
            CurrentVersion = "未安装";
            CurrentVersionTag = "无";
            LoaderIconKey = "vanilla";
            LoaderColor = "#3E9B4F";
            LoaderName = "Vanilla";
            return;
        }

        CurrentVersion = target.GameVersion;
        CurrentVersionTag = TypeLabel(target.Type);
        LoaderIconKey = LoaderIcon(target.Loader);
        LoaderColor = LoaderColorOf(target.Loader);
        LoaderName = LoaderNameOf(target.Loader);
    }

    private void BuildProfiles()
    {
        var accounts = _services.Accounts.Accounts;
        var selected = _services.Accounts.SelectedAccount;
        var selectedIndex = Math.Max(0, accounts.IndexOf(selected));
        Profiles.Clear();
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            Profiles.Add(new PlayerProfileViewModel(
                "p" + index,
                account.Name,
                DescribeAccountType(account),
                SkinFor(index),
                account.IsMicrosoft,
                isActive: index == selectedIndex));
        }

        ActiveProfile = Profiles.FirstOrDefault(x => x.IsActive) ?? Profiles.FirstOrDefault();
    }

    private static string DescribeAccountType(PlayerAccount account) => account.Type switch
    {
        "msa" => "微软正版",
        "authlib" => string.IsNullOrWhiteSpace(account.ServerName) ? "皮肤站" : account.ServerName,
        _ => "离线账户"
    };

    private static string SkinFor(int index) => (index % 3) switch
    {
        1 => "alex",
        2 => "zuri",
        _ => "steve"
    };

    private static string TypeLabel(string type) => type switch
    {
        "snapshot" => "快照版",
        "old_beta" => "远古版",
        "old_alpha" => "远古版",
        _ => "正式版"
    };

    private static string LoaderIcon(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        "Quilt" => "quilt",
        "OptiFine" => "optifine",
        "LiteLoader" => "liteloader",
        _ => "vanilla"
    };

    private static string LoaderColorOf(string loader) => loader switch
    {
        "Fabric" => "#C9976B",
        "Forge" => "#C86A2C",
        "NeoForge" => "#3A6FA8",
        "Quilt" => "#9B59B6",
        "OptiFine" => "#E0A526",
        "LiteLoader" => "#6C8A3C",
        _ => "#3E9B4F"
    };

    private static string LoaderNameOf(string loader) => loader == "原版" ? "Vanilla" : loader;

    partial void OnActiveProfileChanged(PlayerProfileViewModel? value)
    {
        foreach (var profile in Profiles)
        {
            profile.IsActive = ReferenceEquals(profile, value);
        }

        if (value is null)
        {
            return;
        }

        var index = Profiles.IndexOf(value);
        if (index >= 0)
        {
            _services.Accounts.Select(index);
            _ = _services.Accounts.SaveAsync();
        }

        _services.Config.UserName = value.Name;
        _ = _services.ConfigStore.SaveAsync();
    }

    [RelayCommand]
    private async Task LaunchAsync()
    {
        // 运行中点击同一个按钮 => 打开「关闭游戏」确认弹窗
        if (IsGameRunning)
        {
            IsCloseGameConfirmVisible = true;
            return;
        }

        if (!CanLaunch)
        {
            return;
        }

        var instance = _selectedInstance;
        if (instance is null)
        {
            _toast?.Invoke("尚未安装版本，请先在「下载」页安装");
            return;
        }

        IsLaunching = true;
        IsCloseGameConfirmVisible = false;
        LaunchStageText = "正在准备启动…";
        SetLaunchProgress(0.04);
        try
        {
            var config = _services.Config;
            var account = _services.Accounts.SelectedAccount;

            // 正版账号：令牌临近过期先静默续期（refresh_token），失败则中止本次启动
            if (account.Type == "msa" && account.IsTokenNearExpiry)
            {
                LaunchStageText = "正在刷新正版令牌…";
                try
                {
                    var refreshed = await _services.MicrosoftAuth
                        .RefreshAsync(account.RefreshToken)
                        .ConfigureAwait(true);
                    AccountStore.ApplyMicrosoftAuth(account, refreshed);
                    await _services.Accounts.SaveAsync().ConfigureAwait(true);
                    BuildProfiles();
                }
                catch (Exception ex)
                {
                    _toast?.Invoke($"正版令牌刷新失败，请重新登录：{ex.Message}");
                    LaunchStageText = "启动已取消";
                    IsLaunching = false;
                    return;
                }
            }

            // 皮肤站账号：校验令牌（失效则 refresh），并准备 authlib-injector 注入材料
            string? authlibJar = null;
            string? authlibPrefetched = null;
            if (account.Type == "authlib")
            {
                LaunchStageText = "正在校验皮肤站令牌…";
                try
                {
                    if (!await _services.AuthlibAuth
                            .ValidateAsync(account.Server, account.AccessToken, account.ClientToken)
                            .ConfigureAwait(true))
                    {
                        var refreshed = await _services.AuthlibAuth.RefreshAsync(account).ConfigureAwait(true);
                        AccountStore.ApplyAuthlibAuth(account, refreshed);
                        await _services.Accounts.SaveAsync().ConfigureAwait(true);
                    }

                    authlibJar = await _services.AuthlibAuth
                        .EnsureAuthlibInjectorJarAsync(_services.Paths.CacheDirectory, _services.DownloadClient)
                        .ConfigureAwait(true);
                    authlibPrefetched = await _services.AuthlibAuth
                        .GetPrefetchedAsync(account.Server)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _toast?.Invoke($"皮肤站账号准备失败：{ex.Message}");
                    LaunchStageText = "启动已取消";
                    IsLaunching = false;
                    return;
                }
            }

            var instanceSettings = config.FindInstanceSettings(instance.Id);
            var isolated = instanceSettings?.VersionIsolation ?? true;
            var options = new LaunchOptions
            {
                InstanceId = instance.Id,
                GameDirectory = config.EffectiveGameDirectory,
                InstanceDirectory = isolated ? instance.Directory : "",
                JavaPath = string.IsNullOrWhiteSpace(instanceSettings?.JavaPath)
                    ? config.JavaPath
                    : instanceSettings.JavaPath,
                MemoryMegabytes = instanceSettings is { MemoryMegabytes: > 0 }
                    ? instanceSettings.MemoryMegabytes
                    : config.GetEffectiveMemoryMegabytes(),
                InitialMemoryMegabytes = instanceSettings is { InitialMemoryMegabytes: >= 0 }
                    ? instanceSettings.InitialMemoryMegabytes
                    : config.InitialMemoryMegabytes,
                LockMemory = config.LockMemory,
                WindowType = instanceSettings is { WindowType: >= 0 }
                    ? instanceSettings.WindowType
                    : config.WindowType,
                WindowWidth = instanceSettings is { WindowWidth: > 0 }
                    ? instanceSettings.WindowWidth
                    : config.WindowWidth,
                WindowHeight = instanceSettings is { WindowHeight: > 0 }
                    ? instanceSettings.WindowHeight
                    : config.WindowHeight,
                ExtraJvmArguments = string.IsNullOrWhiteSpace(instanceSettings?.ExtraJvmArguments)
                    ? config.ExtraJvmArguments
                    : instanceSettings.ExtraJvmArguments,
                ExtraGameArguments = config.ExtraGameArguments,
                PlayerName = account.Name,
                PlayerUuid = account.Uuid,
                AccessToken = account.AccessToken,
                UserType = account.Type switch
                {
                    "msa" => "msa",
                    "authlib" => "Legacy",
                    _ => "legacy"
                },
                AuthClientId = account.Type == "msa" ? HyclSecrets.MicrosoftOAuthClientId : "",
                AuthXuid = account.Xuid,
                AuthlibServer = account.Type == "authlib" ? account.Server : "",
                AuthlibJarPath = authlibJar ?? "",
                AuthlibPrefetched = authlibPrefetched ?? "",
                WindowTitle = config.WindowTitle,
                TypeInfo = config.TypeInfo,
                LauncherVisibility = config.LauncherVisibility,
                ProcessPriority = config.ProcessPriority,
                PreferredIpStack = config.PreferredIpStack,
                PreLaunchCommand = config.PreLaunchCommand,
                PreLaunchCommandWait = config.PreLaunchCommandWait
            };

            var session = await _services.Launch
                .LaunchAsync(
                    options,
                    line => _services.Logger.Info(line),
                    stage => _services.Dispatcher.Post(() =>
                    {
                        if (IsLaunching)
                        {
                            LaunchStageText = stage;
                            SetLaunchProgress(ProgressForStage(stage));
                        }
                    }))
                .ConfigureAwait(true);
            _session = session;

            // 进程创建不等于启动成功：继续等待游戏窗口真正可见
            LaunchStageText = "正在启动 JVM…";
            SetLaunchProgress(0.6);
            _ = WatchSessionAsync(session);

            var result = await LaunchService.WaitForGameWindowAsync(
                session,
                stage => _services.Dispatcher.Post(() =>
                {
                    if (IsLaunching)
                    {
                        LaunchStageText = stage;
                        SetLaunchProgress(ProgressForMonitorStage(stage));
                    }
                })).ConfigureAwait(true);

            if (!result.Success)
            {
                IsLaunching = false;
                LaunchStageText = "";
                _services.Logger.Warn(result.Message);
                if (result.ProcessExited || session.HasExited)
                {
                    IsGameRunning = false;
                    if (ReferenceEquals(_session, session))
                    {
                        _session = null;
                    }

                    _toast?.Invoke(result.Message);
                }
                else
                {
                    // 进程仍在运行但未检测到窗口：不无限等待，保留「关闭游戏」入口
                    IsGameRunning = true;
                    _toast?.Invoke($"{result.Message}；进程仍在运行，可点击「关闭游戏」");
                }

                return;
            }

            SetLaunchProgress(1d);
            LaunchStageText = "游戏窗口已出现";
            await Task.Delay(600).ConfigureAwait(true);
            LaunchCount++;
            IsLaunching = false;
            LaunchStageText = "";
            IsGameRunning = true;
            _services.Logger.Info($"启动完成：{result.Message}");
            _toast?.Invoke($"已启动 {instance.Name}（PID {session.Id}）");

            // 启动器可见性（对应 PCL-CE LaunchArgumentVisible）：0 立即关闭 / 4 最小化 / 其余保持
            if (config.LauncherVisibility == 0 || config.CloseLauncherAfterLaunch)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                LauncherVisibilityChanged?.Invoke(this, config.LauncherVisibility);
            }
        }
        catch (LaunchException ex)
        {
            IsLaunching = false;
            IsGameRunning = false;
            LaunchStageText = "";
            SetLaunchProgress(0d);
            _toast?.Invoke(ex.Message);
            _services.Logger.Error("启动游戏失败", ex);
        }
        catch (Exception ex)
        {
            IsLaunching = false;
            IsGameRunning = false;
            LaunchStageText = "";
            SetLaunchProgress(0d);
            _toast?.Invoke($"启动失败：{ex.Message}");
            _services.Logger.Error("启动游戏失败", ex);
        }
    }

    /// <summary>取消关闭游戏：弹窗关闭，不做任何动作。</summary>
    [RelayCommand]
    private void CancelCloseGame() => IsCloseGameConfirmVisible = false;

    /// <summary>确认关闭：先请求正常退出，超时后强制结束；关闭后按钮恢复为可启动。</summary>
    [RelayCommand]
    private async Task ConfirmCloseGameAsync()
    {
        IsCloseGameConfirmVisible = false;
        var session = _session;
        if (!IsGameRunning || session is null)
        {
            return;
        }

        IsClosing = true;
        try
        {
            await session.StopGracefullyAsync(line => _services.Logger.Info(line)).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _services.Logger.Error("关闭游戏失败", ex);
            _toast?.Invoke($"关闭游戏失败：{ex.Message}");
        }
        finally
        {
            IsClosing = false;
            if (session.HasExited)
            {
                IsGameRunning = false;
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
            }
        }
    }

    private async Task WatchSessionAsync(GameSession session)
    {
        try
        {
            await session.WaitForExitAsync().ConfigureAwait(false);
            var exitCode = session.ExitCode;
            _services.Dispatcher.Post(() =>
            {
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }

                IsLaunching = false;
                IsGameRunning = false;
                IsClosing = false;
                LaunchStageText = "";
                SetLaunchProgress(0d);
                _toast?.Invoke($"游戏已退出（退出码 {exitCode}）");
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"等待游戏退出时出错：{ex.Message}");
            _services.Dispatcher.Post(() =>
            {
                IsLaunching = false;
                IsGameRunning = false;
                IsClosing = false;
                LaunchStageText = "";
                SetLaunchProgress(0d);
            });
        }
        finally
        {
            session.Dispose();
        }
    }

    /// <summary>按启动阶段平滑推进进度条（每段独立缓动，不做跳变）。</summary>
    private void SetLaunchProgress(double target)
    {
        target = Math.Clamp(target, 0d, 1d);
        var previous = _progressCts;
        _progressCts = null;
        if (previous is not null)
        {
            try
            {
                previous.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已被其它路径释放
            }

            previous.Dispose();
        }

        var cts = new CancellationTokenSource();
        _progressCts = cts;
        _ = AnimateProgressAsync(target, cts.Token);
    }

    private async Task AnimateProgressAsync(double target, CancellationToken token)
    {
        var start = LaunchProgress;
        var delta = target - start;
        if (Math.Abs(delta) < 0.001d)
        {
            LaunchProgress = target;
            return;
        }

        var durationMs = Math.Clamp(Math.Abs(delta) * 1600d, 160d, 520d);
        var startedAt = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var t = Math.Clamp(
                    Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds / durationMs, 0d, 1d);
                var eased = 1d - Math.Pow(1d - t, 3d);
                LaunchProgress = start + delta * eased;
                if (t >= 1d)
                {
                    LaunchProgress = target;
                    return;
                }

                await Task.Delay(16, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // 后续阶段接管进度动画
        }
    }

    private static double ProgressForStage(string stage) => stage switch
    {
        "正在检查版本文件…" => 0.10,
        "正在检测 Java 运行环境…" => 0.22,
        "正在准备启动参数…" => 0.36,
        "正在启动游戏…" => 0.48,
        _ => 0.52
    };

    private static double ProgressForMonitorStage(string stage) => stage switch
    {
        "正在启动 JVM…" => 0.6,
        "正在加载 Forge 加载器窗口…" => 0.74,
        "正在加载游戏窗口…" => 0.82,
        "游戏窗口已出现，正在完成初始化…" => 0.95,
        _ => 0.7
    };

    [RelayCommand]
    private void OpenProfiles()
    {
        IsPlayerPanel = false;
        LeftPanelTitle = "档案";
    }

    [RelayCommand]
    private void CloseProfiles()
    {
        IsPlayerPanel = true;
        LeftPanelTitle = "玩家";
    }

    [RelayCommand]
    private void SelectProfile(PlayerProfileViewModel? profile)
    {
        if (profile is null)
        {
            return;
        }

        ActiveProfile = profile;
        IsPlayerPanel = true;
        LeftPanelTitle = "玩家";
    }

    [RelayCommand]
    private void NewProfile()
    {
        var sequence = _services.Accounts.Accounts.Count + 1;
        var name = $"Player{sequence}";
        while (_services.Accounts.Accounts.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            sequence++;
            name = $"Player{sequence}";
        }

        _services.Accounts.AddOffline(name);
        _ = _services.Accounts.SaveAsync();
        BuildProfiles();
        _toast?.Invoke($"已创建新的离线档案：{name}");
    }

    [RelayCommand]
    private void ChangeSkin() => _toast?.Invoke("皮肤更换暂未支持，当前展示内置皮肤");

    // ------------------------- 登录对话框（添加账号：正版 / 皮肤站 / 离线） -------------------------

    [ObservableProperty]
    private bool _isLoginDialogVisible;

    /// <summary>0 正版 / 1 皮肤站（预留）/ 2 离线。</summary>
    [ObservableProperty]
    private int _loginTabIndex;

    public bool IsMsLoginTab => LoginTabIndex == 0;

    public bool IsAuthlibLoginTab => LoginTabIndex == 1;

    public bool IsOfflineLoginTab => LoginTabIndex == 2;

    [ObservableProperty]
    private bool _isMsLoginBusy;

    [ObservableProperty]
    private string _msLoginStage = "";

    [ObservableProperty]
    private string _msUserCode = "";

    [ObservableProperty]
    private string _msVerificationUri = "";

    public bool HasMsUserCode => !string.IsNullOrWhiteSpace(MsUserCode);

    [ObservableProperty]
    private string _offlineNameText = "";

    public bool CanAddOfflineAccount => OfflineNameText.Trim().Length > 0;

    private CancellationTokenSource? _msLoginCts;

    partial void OnLoginTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsMsLoginTab));
        OnPropertyChanged(nameof(IsAuthlibLoginTab));
        OnPropertyChanged(nameof(IsOfflineLoginTab));
    }

    partial void OnMsUserCodeChanged(string value) => OnPropertyChanged(nameof(HasMsUserCode));

    partial void OnOfflineNameTextChanged(string value) => OnPropertyChanged(nameof(CanAddOfflineAccount));

    [RelayCommand]
    private void OpenLoginDialog()
    {
        MsUserCode = "";
        MsVerificationUri = "";
        MsLoginStage = "";
        IsMsLoginBusy = false;
        OfflineNameText = "";
        AuthlibProfileOptions = null;
        SelectedAuthlibProfile = null;
        AuthlibLoginStage = "";
        IsAuthlibLoginBusy = false;
        LoginTabIndex = 0;
        IsLoginDialogVisible = true;
    }

    [RelayCommand]
    private void CloseLoginDialog()
    {
        _msLoginCts?.Cancel();
        IsLoginDialogVisible = false;
    }

    [RelayCommand]
    private void OpenMsVerificationPage()
    {
        var url = string.IsNullOrWhiteSpace(MsVerificationUri) ? "https://www.microsoft.com/link" : MsVerificationUri;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"无法打开浏览器：{ex.Message}（请手动访问 {url}）");
        }
    }

    /// <summary>正版设备码登录：申请设备码 → 展示给玩家 → 按 interval 轮询直到完成/过期。</summary>
    [RelayCommand]
    private async Task StartMsLoginAsync()
    {
        if (IsMsLoginBusy)
        {
            return;
        }

        if (!HyclSecrets.HasMicrosoftOAuthClientId)
        {
            _toast?.Invoke("正版登录未配置 Microsoft OAuth ClientId");
            return;
        }

        _msLoginCts?.Cancel();
        _msLoginCts?.Dispose();
        var cts = new CancellationTokenSource();
        _msLoginCts = cts;

        IsMsLoginBusy = true;
        MsUserCode = "";
        MsVerificationUri = "";
        try
        {
            MsLoginStage = "正在向微软申请设备码…";
            var deviceCode = await _services.MicrosoftAuth
                .StartDeviceCodeAsync(cts.Token)
                .ConfigureAwait(true);
            MsUserCode = deviceCode.UserCode;
            MsVerificationUri = string.IsNullOrWhiteSpace(deviceCode.VerificationUriComplete)
                ? deviceCode.VerificationUri
                : deviceCode.VerificationUriComplete;
            MsLoginStage =
                $"请在浏览器打开 {deviceCode.VerificationUri} 并输入上方代码（约 {Math.Max(1, deviceCode.ExpiresInSeconds / 60)} 分钟内有效）";

            var interval = TimeSpan.FromSeconds(Math.Max(2, deviceCode.IntervalSeconds));
            var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, deviceCode.ExpiresInSeconds));
            while (DateTimeOffset.UtcNow < deadline && !cts.Token.IsCancellationRequested)
            {
                await Task.Delay(interval, cts.Token).ConfigureAwait(true);
                var poll = await _services.MicrosoftAuth
                    .PollOnceAsync(deviceCode, cts.Token)
                    .ConfigureAwait(true);

                if (poll.Result is not null)
                {
                    _services.Accounts.AddMicrosoft(poll.Result);
                    await _services.Accounts.SaveAsync(cts.Token).ConfigureAwait(true);
                    _services.Config.UserName = poll.Result.UserName;
                    await _services.ConfigStore.SaveAsync(cts.Token).ConfigureAwait(true);
                    BuildProfiles();
                    _toast?.Invoke($"正版登录成功：{poll.Result.UserName}");
                    IsLoginDialogVisible = false;
                    return;
                }

                if (poll.IsExpired)
                {
                    MsLoginStage = "设备码已过期，请重新开始登录";
                    MsUserCode = "";
                    return;
                }

                if (poll.IsDenied)
                {
                    MsLoginStage = "你在浏览器中拒绝了授权，请重新开始登录";
                    MsUserCode = "";
                    return;
                }

                if (poll.IsSlowDown)
                {
                    interval = interval.Add(TimeSpan.FromSeconds(3));
                }
            }

            if (!cts.Token.IsCancellationRequested)
            {
                MsLoginStage = "登录超时，请重新开始";
                MsUserCode = "";
            }
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
            // 用户关闭对话框：静默退出
        }
        catch (MicrosoftAuthException ex)
        {
            MsLoginStage = ex.Message;
            MsUserCode = "";
            _toast?.Invoke($"正版登录失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            MsLoginStage = $"网络异常：{ex.Message}";
            MsUserCode = "";
            _toast?.Invoke($"正版登录失败：{ex.Message}");
            _services.Logger.Error("正版登录失败", ex);
        }
        finally
        {
            if (ReferenceEquals(_msLoginCts, cts))
            {
                _msLoginCts = null;
                IsMsLoginBusy = false;
                cts.Dispose();
            }
        }
    }

    [RelayCommand]
    private void AddOfflineAccount()
    {
        var name = OfflineNameText.Trim();
        if (name.Length == 0)
        {
            _toast?.Invoke("请先输入玩家名");
            return;
        }

        if (name.Length > 16)
        {
            _toast?.Invoke("玩家名最长 16 个字符");
            return;
        }

        _services.Accounts.AddOffline(name);
        _ = _services.Accounts.SaveAsync();
        _services.Config.UserName = name;
        _ = _services.ConfigStore.SaveAsync();
        BuildProfiles();
        OfflineNameText = "";
        IsLoginDialogVisible = false;
        _toast?.Invoke($"已添加离线档案：{name}");
    }

    // ------------------------- 皮肤站登录（authlib-injector / Yggdrasil） -------------------------

    [ObservableProperty]
    private string _authlibServerText = "https://littleskin.cn/api/yggdrasil";

    [ObservableProperty]
    private string _authlibUsernameText = "";

    [ObservableProperty]
    private string _authlibPasswordText = "";

    [ObservableProperty]
    private bool _isAuthlibLoginBusy;

    [ObservableProperty]
    private string _authlibLoginStage = "";

    /// <summary>多角色账号：authenticate 成功后列出待选角色。</summary>
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<AuthlibProfile>? _authlibProfileOptions;

    [ObservableProperty]
    private AuthlibProfile? _selectedAuthlibProfile;

    public bool HasAuthlibProfileOptions => AuthlibProfileOptions is { Count: > 0 };

    partial void OnAuthlibProfileOptionsChanged(System.Collections.ObjectModel.ObservableCollection<AuthlibProfile>? value)
        => OnPropertyChanged(nameof(HasAuthlibProfileOptions));

    /// <summary>皮肤站登录：探测站点 → authenticate → 多角色时展示选择 → refresh 绑定。</summary>
    [RelayCommand]
    private async Task StartAuthlibLoginAsync()
    {
        if (IsAuthlibLoginBusy)
        {
            return;
        }

        var username = AuthlibUsernameText.Trim();
        var password = AuthlibPasswordText;
        var serverInput = AuthlibServerText.Trim();
        if (username.Length == 0 || password.Length == 0 || serverInput.Length == 0)
        {
            _toast?.Invoke("请填写皮肤站地址、账号与密码");
            return;
        }

        IsAuthlibLoginBusy = true;
        AuthlibLoginStage = "";
        AuthlibServerInfo server;
        try
        {
            AuthlibLoginStage = "正在探测皮肤站…";
            server = await _services.AuthlibAuth
                .ResolveServerAsync(serverInput)
                .ConfigureAwait(true);
        }
        catch (AuthlibException ex)
        {
            AuthlibLoginStage = ex.Message;
            IsAuthlibLoginBusy = false;
            _toast?.Invoke($"皮肤站登录失败：{ex.Message}");
            return;
        }

        try
        {
            AuthlibLoginStage = $"正在登录 {server.ServerName}…";
            var result = await _services.AuthlibAuth
                .AuthenticateAsync(server, username, password)
                .ConfigureAwait(true);
            FinishAuthlibLogin(result);
        }
        catch (AuthlibProfileSelectionRequiredException ex)
        {
            // 多角色：展示选择列表；令牌暂存内存，选中后 refresh 绑定，不重发密码
            _pendingAuthlibServer = server.ApiRoot;
            _pendingAuthlibServerName = server.ServerName;
            _pendingAuthlibAccessToken = ex.AccessToken;
            _pendingAuthlibClientToken = ex.ClientToken;
            _pendingAuthlibUsername = username;
            AuthlibProfileOptions = new(ex.Profiles);
            SelectedAuthlibProfile = ex.Profiles[0];
            AuthlibLoginStage = "请选择要登录的角色";
        }
        catch (AuthlibException ex)
        {
            AuthlibLoginStage = ex.Message;
            _toast?.Invoke($"皮肤站登录失败：{ex.Message}");
        }
        catch (Exception ex)
        {
            AuthlibLoginStage = $"网络异常：{ex.Message}";
            _toast?.Invoke($"皮肤站登录失败：{ex.Message}");
            _services.Logger.Error("皮肤站登录失败", ex);
        }
        finally
        {
            AuthlibPasswordText = "";
            if (!HasAuthlibProfileOptions)
            {
                IsAuthlibLoginBusy = false;
            }
        }
    }

    private string? _pendingAuthlibServer;
    private string? _pendingAuthlibAccessToken;
    private string? _pendingAuthlibClientToken;
    private string _pendingAuthlibUsername = "";

    /// <summary>多角色选择完成后：用首次 authenticate 的令牌 refresh 绑定所选角色。</summary>
    [RelayCommand]
    private void ConfirmAuthlibProfile()
    {
        if (SelectedAuthlibProfile is null)
        {
            _toast?.Invoke("请先选择一个角色");
            return;
        }

        if (_pendingAuthlibServer is null || _pendingAuthlibAccessToken is null || _pendingAuthlibClientToken is null)
        {
            return;
        }

        IsAuthlibLoginBusy = true;
        AuthlibLoginStage = "正在绑定所选角色…";
        var profile = SelectedAuthlibProfile;
        var server = _pendingAuthlibServer;
        var accessToken = _pendingAuthlibAccessToken;
        var clientToken = _pendingAuthlibClientToken;
        var username = _pendingAuthlibUsername;
        _ = Task.Run(async () =>
        {
            try
            {
                // 站点名已在探测阶段确定；这里只做 refresh 绑定
                var result = await _services.AuthlibAuth
                    .BindProfileAsync(server, accessToken, clientToken, profile, username, _pendingAuthlibServerName ?? "")
                    .ConfigureAwait(false);
                _services.Dispatcher.Post(() => FinishAuthlibLogin(result));
            }
            catch (Exception ex)
            {
                _services.Dispatcher.Post(() =>
                {
                    AuthlibLoginStage = ex.Message;
                    _toast?.Invoke($"皮肤站登录失败：{ex.Message}");
                    AuthlibProfileOptions = null;
                    SelectedAuthlibProfile = null;
                    IsAuthlibLoginBusy = false;
                });
            }
        });
    }

    private string? _pendingAuthlibServerName;

    private void FinishAuthlibLogin(AuthlibAuthResult result)
    {
        _services.Accounts.AddAuthlib(result);
        _ = _services.Accounts.SaveAsync();
        _services.Config.UserName = result.UserName;
        _ = _services.ConfigStore.SaveAsync();
        BuildProfiles();
        AuthlibUsernameText = "";
        AuthlibProfileOptions = null;
        SelectedAuthlibProfile = null;
        _pendingAuthlibServer = null;
        _pendingAuthlibAccessToken = null;
        _pendingAuthlibClientToken = null;
        _pendingAuthlibUsername = "";
        IsLoginDialogVisible = false;
        _toast?.Invoke($"皮肤站登录成功：{result.UserName}（{result.ServerName}）");
    }

    [RelayCommand]
    private void EditProfile() => _toast?.Invoke("编辑档案暂未支持，可在配置文件中修改玩家名");

    [RelayCommand]
    private async Task ManageInstanceAsync()
    {
        await RefreshInstancesAsync().ConfigureAwait(true);
        NavigateRequested?.Invoke(this, "instances");
    }

    [RelayCommand]
    private void ShowQuickLink(QuickLinkViewModel? link)
    {
        if (link is null)
        {
            return;
        }

        switch (link.Title)
        {
            case "下载游戏":
                NavigateRequested?.Invoke(this, "download");
                return;
            case "版本列表":
                _ = ManageInstanceAsync();
                return;
        }

        _toast?.Invoke(link.Title switch
        {
            "快速联机" => "联机功能暂未支持",
            _ => "整合包安装暂未支持"
        });
    }
}

/// <summary>快捷方式条目。</summary>
public sealed record QuickLinkViewModel(string Title, string Subtitle, string IconKey);
