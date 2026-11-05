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
    /// <summary>当前仍在运行（或正在启动）的游戏会话；支持同一实例多次启动（多开）。</summary>
    private readonly List<GameSession> _sessions = new();
    private MinecraftInstance? _selectedInstance;
    private bool _suppressInstanceSelection;
    private CancellationTokenSource? _refreshCts;
    private CancellationTokenSource? _progressCts;
    private CancellationTokenSource? _launchTickerCts;
    private Stopwatch? _launchWatch;
    private readonly List<string> _launchLog = new();
    private readonly object _launchLogGate = new();

    /// <summary>启动页实时日志最多保留的行数（防止长时间加载把内存与界面拖慢）。</summary>
    private const int MaxLaunchLogLines = 600;

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

    /// <summary>
    /// 是否显示独立「启动中」页面：点击启动后切换过去，成功或返回后切回启动页；
    /// 启动失败时保持显示（失败详情需要留在页面上）。
    /// </summary>
    [ObservableProperty]
    private bool _isLaunchProgressVisible;

    /// <summary>启动页实时日志是否展开（默认收起，失败页固定展示）。</summary>
    [ObservableProperty]
    private bool _isLaunchLogExpanded;

    /// <summary>启动页日志文本（按固定节流刷新，避免每一行都触发界面重排）。</summary>
    [ObservableProperty]
    private string _launchLogText = "";

    /// <summary>启动耗时文本（每 200ms 刷新一次）。</summary>
    [ObservableProperty]
    private string _launchElapsedText = "0.0 秒";

    /// <summary>启动失败的结构化结论；为 null 表示当前没有失败。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLaunchFailed))]
    [NotifyPropertyChangedFor(nameof(LaunchFailureExitText))]
    private LaunchFailureInfo? _launchFailure;

    public bool IsLaunchFailed => LaunchFailure is not null;

    /// <summary>失败页展示的退出码文本（未创建进程时如实说明）。</summary>
    public string LaunchFailureExitText => LaunchFailure?.ExitCode is { } code
        ? $"进程退出码：{code}"
        : "进程尚未启动，未返回退出码";

    // ===== 启动过程页（对齐 PCL-CE 的信息结构）=====

    /// <summary>PCL-CE 的七步流程：检查版本 → 校验资源 → 检查 Java 与内存 → 构建参数 → 启动进程 → 等待窗口 → 完成。</summary>
    private static readonly string[] LaunchStepTitles =
    [
        "检查版本文件",
        "校验与补全资源",
        "检查 Java 与内存",
        "构建启动参数",
        "启动游戏进程",
        "等待游戏窗口",
        "启动完成"
    ];

    public ObservableCollection<LaunchStepItemViewModel> LaunchSteps { get; } = [];

    /// <summary>当前步骤标题与「第 n 步 / 共 7 步」计数。</summary>
    [ObservableProperty]
    private string _launchStepTitle = LaunchStepTitles[0];

    [ObservableProperty]
    private string _launchStepCounterText = $"第 1 步 / 共 {LaunchStepTitles.Length} 步";

    /// <summary>登录方式：离线账号显示「离线登录 / 用户名」，未选择账号时明确提示。</summary>
    [ObservableProperty]
    private string _launchAccountSummary = "未选择账号";

    /// <summary>关键参数摘要（版本名 / 内存 / Java / 游戏目录），用于排错。</summary>
    [ObservableProperty]
    private string _launchParamsVersion = "—";

    [ObservableProperty]
    private string _launchParamsMemory = "—";

    [ObservableProperty]
    private string _launchParamsJava = "—";

    [ObservableProperty]
    private string _launchParamsGameDirectory = "—";

    /// <summary>启动前检查未通过的项（失败页逐条展示原因与建议）。</summary>
    public ObservableCollection<LaunchCheckItem> LaunchFailedChecks { get; } = [];

    public bool HasFailedChecks => LaunchFailedChecks.Count > 0;

    /// <summary>失败页大标题：启动前检查未通过 / 启动失败 / 游戏崩溃。</summary>
    [ObservableProperty]
    private string _launchFailureHeader = "启动失败";

    /// <summary>游戏内崩溃时指向的最新一份 crash-report 文件名；无则为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCrashReport))]
    private string? _crashReportName;

    public bool HasCrashReport => !string.IsNullOrWhiteSpace(CrashReportName);

    private int _launchStepIndex;

    public bool CanLaunch => !IsLaunching && !IsClosing;

    /// <summary>按钮是否可点击：启动中/关闭中禁用；游戏运行中仍可再次启动（多开）。</summary>
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

    /// <summary>当前版本的方块图标键（与版本管理页实例列表同源，grass-block / forge / package…）。</summary>
    [ObservableProperty]
    private string _currentIconKey = "grass-block";

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
            : "启动游戏";

    /// <summary>按钮图标键：始终为播放图标（运行中也可再次启动，关闭游戏由右下角悬浮按钮承担）。</summary>
    public string LaunchIconKey => "play";

    /// <summary>按钮副标题：启动中显示阶段提示，其余显示版本信息（灰色小字）。</summary>
    public string LaunchSubText => IsLaunching ? LaunchStageText : VersionSummary;

    /// <summary>该实例的累计启动次数（持久化在配置的 instanceLaunchCounts 字段）。</summary>
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
            CurrentIconKey = "grass-block";
            LoaderIconKey = "vanilla";
            LoaderColor = "#3E9B4F";
            LoaderName = "Vanilla";
            LaunchCount = 0;
            return;
        }

        CurrentVersion = target.GameVersion;
        CurrentVersionTag = TypeLabel(target.Type);
        // 与版本管理页同一套图标来源（扫描实例时的加载器/类型判定），不再用线条图标兜底
        CurrentIconKey = string.IsNullOrWhiteSpace(target.IconKey) || target.IconKey == "generic"
            ? LoaderIcon(target.Loader)
            : target.IconKey;
        LoaderIconKey = LoaderIcon(target.Loader);
        LoaderColor = LoaderColorOf(target.Loader);
        LoaderName = LoaderNameOf(target.Loader);
        LaunchCount = _services.Config.GetLaunchCount(target.Id);
    }

    /// <summary>玩家卡显示名：无账号时给友好占位。</summary>
    public string PlayerCardName => ActiveProfile?.Name ?? "未登录";

    /// <summary>玩家卡显示的账号类型：无账号时为空。</summary>
    public string PlayerCardType => ActiveProfile?.AccountType ?? "";

    private void BuildProfiles()
    {
        var accounts = _services.Accounts.Accounts;
        var selected = _services.Accounts.SelectedAccount;
        var selectedIndex = selected is null ? 0 : Math.Max(0, accounts.IndexOf(selected));
        Profiles.Clear();
        for (var index = 0; index < accounts.Count; index++)
        {
            var account = accounts[index];
            Profiles.Add(new PlayerProfileViewModel(
                "p" + index,
                account.Name,
                DescribeAccountType(account),
                ResolveSkinKey(account),
                account.IsMicrosoft,
                isActive: index == selectedIndex,
                accountUuid: account.Uuid,
                skinPath: account.SkinPath,
                isOffline: account.Type == "offline"));
        }

        ActiveProfile = Profiles.FirstOrDefault(x => x.IsActive) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(PlayerCardName));
        OnPropertyChanged(nameof(PlayerCardType));
    }

    /// <summary>档案头像的皮肤键：本地缓存皮肤存在时无需回退；离线账号用自选皮肤。</summary>
    private static string ResolveSkinKey(PlayerAccount account)
        => account.Type switch
        {
            "offline" => string.IsNullOrWhiteSpace(account.SkinKey) ? SkinForAccount(account) : account.SkinKey,
            _ => SkinForAccount(account)
        };

    private static string SkinForAccount(PlayerAccount account)
    {
        // 稳定哈希取模分派内置皮肤；无 Uuid 时按名字派生
        var index = Math.Abs(string.IsNullOrWhiteSpace(account.Uuid)
            ? (account.Name ?? "").GetHashCode()
            : account.Uuid.GetHashCode());
        var remainder = index - index / 3 * 3;
        return remainder switch
        {
            1 => "alex",
            2 => "zuri",
            _ => "steve"
        };
    }

    /// <summary>档案删除（档案面板的小删除按钮）。</summary>
    [RelayCommand]
    private async Task DeleteProfileAsync(PlayerProfileViewModel? profile)
    {
        if (profile is null || string.IsNullOrEmpty(profile.AccountUuid))
        {
            return;
        }

        if (_services.Accounts.RemoveById(profile.AccountUuid))
        {
            await _services.Accounts.SaveAsync().ConfigureAwait(true);
            BuildProfiles();
            _toast?.Invoke($"已删除档案：{profile.Name}");
        }
    }

    /// <summary>档案重命名弹窗是否显示。</summary>
    [ObservableProperty]
    private bool _isRenameProfileVisible;

    /// <summary>重命名输入框内容。</summary>
    [ObservableProperty]
    private string _renameProfileText = "";

    /// <summary>重命名校验错误（非空时原名称保持不变）。</summary>
    [ObservableProperty]
    private string _renameProfileError = "";

    private string _renameProfileUuid = "";

    /// <summary>打开离线档案重命名弹窗。</summary>
    [RelayCommand]
    private void StartRenameProfile(PlayerProfileViewModel? profile)
    {
        if (profile is null || !profile.IsOffline || string.IsNullOrEmpty(profile.AccountUuid))
        {
            return;
        }

        _renameProfileUuid = profile.AccountUuid;
        RenameProfileText = profile.Name;
        RenameProfileError = "";
        IsRenameProfileVisible = true;
    }

    [RelayCommand]
    private void CancelRenameProfile()
    {
        IsRenameProfileVisible = false;
        RenameProfileError = "";
    }

    /// <summary>
    /// 确认重命名离线档案：名称不能为空、字符集与离线账号校验一致（字母/数字/下划线，3~16），
    /// 且不能与其他离线档案重名（大小写不敏感）。成功后写回账号与选中状态并持久化。
    /// </summary>
    [RelayCommand]
    private async Task ConfirmRenameProfileAsync()
    {
        var name = RenameProfileText.Trim();
        var account = _services.Accounts.Accounts
            .FirstOrDefault(x => x.Type == "offline"
                                 && x.Uuid.Equals(_renameProfileUuid, StringComparison.OrdinalIgnoreCase));
        if (account is null)
        {
            RenameProfileError = "该档案已不存在，请刷新后重试";
            return;
        }

        if (name.Length == 0)
        {
            RenameProfileError = "名称不能为空";
            return;
        }

        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
        {
            RenameProfileError = "离线名只能包含字母、数字与下划线，长度 3~16";
            return;
        }

        var conflict = _services.Accounts.Accounts.Any(x =>
            x.Type == "offline"
            && !x.Uuid.Equals(_renameProfileUuid, StringComparison.OrdinalIgnoreCase)
            && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (conflict)
        {
            RenameProfileError = "该名称已被使用";
            return;
        }

        var wasSelected = _services.Accounts.SelectedAccount is { } selected
                          && selected.Uuid.Equals(_renameProfileUuid, StringComparison.OrdinalIgnoreCase);
        account.Name = name;
        account.Uuid = OfflineUuid.Create(name);
        _renameProfileUuid = account.Uuid;
        if (wasSelected)
        {
            _services.Config.UserName = name;
        }

        await _services.Accounts.SaveAsync().ConfigureAwait(true);
        if (wasSelected)
        {
            await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        }

        BuildProfiles();
        IsRenameProfileVisible = false;
        RenameProfileError = "";
        _toast?.Invoke($"档案已重命名为：{name}");
    }

    /// <summary>皮肤下载到缓存并回填 SkinPath；失败静默（皮肤非关键数据）。返回是否成功。</summary>
    private async Task<bool> DownloadSkinAsync(PlayerAccount account, string skinUrl)
    {
        try
        {
            var directory = Path.Combine(_services.Paths.CacheDirectory, "skins");
            Directory.CreateDirectory(directory);
            var skinPath = Path.Combine(directory, account.Uuid + ".png");
            await _services.DownloadClient
                .DownloadFileAsync([skinUrl], skinPath, null, null, null, CancellationToken.None)
                .ConfigureAwait(false);
            account.SkinPath = skinPath;
            return true;
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"下载皮肤失败（忽略）：{ex.Message}");
            return false;
        }
    }

    /// <summary>离线皮肤更换：把内置皮肤键写回当前账号。</summary>
    [RelayCommand]
    private async Task ApplySkinAsync(string skinKey)
    {
        var account = _services.Accounts.SelectedAccount;
        if (account is null)
        {
            _toast?.Invoke("请先添加账号");
            return;
        }

        if (account.Type != "offline")
        {
            _toast?.Invoke(account.Type == "msa"
                ? "正版账号的皮肤跟随微软档案自动获取"
                : "皮肤站账号的皮肤跟随皮肤站设置");
            return;
        }

        if (!BuiltInSkinKeys.Contains(skinKey))
        {
            return;
        }

        account.SkinKey = skinKey;
        account.SkinPath = "";
        await _services.Accounts.SaveAsync().ConfigureAwait(true);
        BuildProfiles();
        IsSkinPickerVisible = false;
        _toast?.Invoke($"已更换皮肤：{skinKey}");
    }

    public static readonly IReadOnlyList<string> BuiltInSkinKeys = ["steve", "alex", "zuri"];

    private static string DescribeAccountType(PlayerAccount account) => account.Type switch
    {
        "msa" => "微软正版",
        "authlib" => string.IsNullOrWhiteSpace(account.ServerName) ? "皮肤站" : account.ServerName,
        _ => "离线账户"
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

    private static string LoaderNameOf(string? loader)
    {
        var value = string.IsNullOrWhiteSpace(loader) ? "" : loader.Trim();
        return value is "" or "原版" ? "Vanilla" : value;
    }

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

            // 先切到独立启动页：账号缺失、版本/资源/Java/内存检查不通过都在这一页给出原因与建议，
            // 不再用弹窗把玩家挡在启动页外（对齐 PCL-CE 的启动前检查体验）。
            StartLaunchSession();
            PrepareLaunchSummary(instance, account);

            var checkOptions = BuildLaunchOptions(instance, account, "", "");
            LaunchParamsMemory = $"{checkOptions.MemoryMegabytes} MB";

            var preflight = await _services.Launch
                .RunPreflightAsync(
                    checkOptions,
                    account,
                    step => _services.Dispatcher.Post(() => AdvanceLaunchStep(step)))
                .ConfigureAwait(true);
            ApplyPreflightResult(preflight);

            if (!preflight.Passed || account is null)
            {
                // 检查不通过：停在检查步骤，逐项展示原因与建议，允许返回修正
                ShowPreflightFailure(preflight, account);
                return;
            }

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
                    AbortLaunchSession();
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
                    AbortLaunchSession();
                    return;
                }
            }

            AdvanceLaunchStep(4);
            var options = BuildLaunchOptions(instance, account, authlibJar, authlibPrefetched);

            var session = await _services.Launch
                .LaunchAsync(
                    options,
                    line =>
                    {
                        AppendLaunchLog(line);
                        _services.Logger.Info(line);
                    },
                    stage =>
                    {
                        AppendLaunchLog($"[阶段] {stage}");
                        _services.Dispatcher.Post(() =>
                        {
                            if (IsLaunching)
                            {
                                LaunchStageText = stage;
                                var step = StepForLaunchStage(stage);
                                if (step > 0)
                                {
                                    AdvanceLaunchStep(step);
                                }

                                SetLaunchProgress(ProgressForStage(stage));
                            }
                        });
                    })
                .ConfigureAwait(true);
            _sessions.Add(session);

            // 进程创建不等于启动成功：继续等待游戏窗口真正可见。
            // 退出看护不在这里启动——否则窗口检测失败/退出的两条分支会与看护互相覆盖状态
            //（按钮显示「运行中」而游戏早已退出），等窗口检测结果出来后再按分支启动。
            LaunchStageText = "正在启动 JVM…";
            SetLaunchProgress(0.6);

            var result = await LaunchService.WaitForGameWindowAsync(
                session,
                stage =>
                {
                    AppendLaunchLog($"[阶段] {stage}");
                    _services.Dispatcher.Post(() =>
                    {
                        if (IsLaunching)
                        {
                            LaunchStageText = stage;
                            AdvanceLaunchStep(6);
                            SetLaunchProgress(ProgressForMonitorStage(stage));
                        }
                    });
                }).ConfigureAwait(true);

            if (!result.Success)
            {
                IsLaunching = false;
                LaunchStageText = "";
                _services.Logger.Warn(result.Message);
                if (result.ProcessExited || session.HasExited)
                {
                    _sessions.Remove(session);
                    IsGameRunning = _sessions.Count > 0;

                    // 进程在窗口出现前退出：按退出码 + 日志特征给出失败页，绝不显示空白页
                    AppendLaunchLog($"[失败] {result.Message}");
                    FailLaunchStep();
                    ShowLaunchFailure(LaunchFailureAnalyzer.Analyze(
                        result.ExitCode,
                        SnapshotLaunchLog(),
                        result.Message), "启动失败");
                }
                else
                {
                    // 进程仍在运行但未检测到窗口：保留「关闭游戏」入口，
                    // 退出看护此刻才启动（复位按钮状态），不再与窗口检测竞争
                    IsGameRunning = true;
                    LaunchStageText = "未检测到游戏窗口，游戏可能仍在加载";
                    CompleteLaunchSession();
                    _ = WatchSessionAsync(session, config.EffectiveGameDirectory);
                    _toast?.Invoke($"{result.Message}；进程仍在运行，可点击「关闭游戏」");
                }

                return;
            }

            SetLaunchProgress(1d);
            LaunchStageText = "游戏窗口已出现";
            CompleteAllLaunchSteps();
            await Task.Delay(600).ConfigureAwait(true);
            // 仅“检测到游戏窗口”算一次成功启动；写入配置并立即持久化（重启不清零）
            LaunchCount = _services.Config.IncrementLaunchCount(instance.Id);
            await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
            IsLaunching = false;
            LaunchStageText = "";
            IsGameRunning = true;
            // 只有真正等到游戏窗口出现才离开启动页回到启动页（运行中状态由右下角停止按钮承载）
            CompleteLaunchSession();
            // 窗口已确认出现后才启动退出看护
            _ = WatchSessionAsync(session, config.EffectiveGameDirectory);
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
            IsGameRunning = _sessions.Count > 0;
            SetLaunchProgress(0d);
            _services.Logger.Error("启动游戏失败", ex);
            // 启动前就失败时进程没有输出，把异常原因写进日志，失败页不会只有空日志
            AppendLaunchLog($"[失败] {ex.Message}");
            FailLaunchStep();
            ShowLaunchFailure(LaunchFailureAnalyzer.Analyze(null, SnapshotLaunchLog(), ex.Message), "启动失败");
        }
        catch (Exception ex)
        {
            IsLaunching = false;
            IsGameRunning = _sessions.Count > 0;
            SetLaunchProgress(0d);
            _services.Logger.Error("启动游戏失败", ex);
            AppendLaunchLog($"[失败] 启动失败：{ex.Message}");
            FailLaunchStep();
            ShowLaunchFailure(LaunchFailureAnalyzer.Analyze(
                null,
                SnapshotLaunchLog(),
                $"启动失败：{ex.Message}"), "启动失败");
        }
    }

    // ------------------------- 独立启动页：日志 / 计时 / 失败展示 -------------------------

    /// <summary>进入一次新的启动会话：清空日志、重置失败状态与步骤列表，并切到启动中页面。</summary>
    private void StartLaunchSession()
    {
        lock (_launchLogGate)
        {
            _launchLog.Clear();
        }

        LaunchLogText = "";
        LaunchFailure = null;
        LaunchFailureHeader = "启动失败";
        LaunchFailedChecks.Clear();
        OnPropertyChanged(nameof(HasFailedChecks));
        CrashReportName = null;
        LatestCrashReportPath = null;
        IsCrashBannerVisible = false;
        ResetLaunchSteps();
        IsLaunchLogExpanded = false;
        IsLaunchProgressVisible = true;
        StopLaunchTicker();
        _launchWatch = Stopwatch.StartNew();
        LaunchElapsedText = "0.0 秒";
        var cts = new CancellationTokenSource();
        _launchTickerCts = cts;
        _ = RunLaunchTickerAsync(cts.Token);
    }

    /// <summary>启动成功：停表并回到启动页（失败状态由 ShowLaunchFailure 单独保留）。</summary>
    private void CompleteLaunchSession()
    {
        StopLaunchTicker();
        IsLaunchProgressVisible = false;
        IsLaunchLogExpanded = false;
    }

    /// <summary>启动被取消（令牌/账号准备失败）：停表并回到启动页，不生成失败页。</summary>
    private void AbortLaunchSession()
    {
        StopLaunchTicker();
        IsLaunchProgressVisible = false;
        IsLaunchLogExpanded = false;
    }

    /// <summary>展示结构化的启动失败页，并把完整日志快照固定下来供复制。</summary>
    private void ShowLaunchFailure(LaunchFailureInfo failure, string header = "启动失败")
    {
        StopLaunchTicker();
        LaunchFailureHeader = header;
        LaunchFailure = failure;
        LaunchLogText = string.Join(Environment.NewLine, SnapshotLaunchLog());
        IsLaunchProgressVisible = true;
    }

    // ------------------------- 启动过程页：步骤 / 参数摘要 / 检查结果 -------------------------

    private void ResetLaunchSteps()
    {
        LaunchSteps.Clear();
        for (var i = 0; i < LaunchStepTitles.Length; i++)
        {
            LaunchSteps.Add(new LaunchStepItemViewModel(i + 1, LaunchStepTitles[i]));
        }

        _launchStepIndex = 0;
        LaunchStepTitle = LaunchStepTitles[0];
        LaunchStepCounterText = $"第 1 步 / 共 {LaunchStepTitles.Length} 步";
    }

    /// <summary>只前进不后退：重复汇报更早的步骤时保持当前进度，避免步骤条来回跳。</summary>
    private void AdvanceLaunchStep(int step)
    {
        if (step < 1 || step > LaunchSteps.Count || step < _launchStepIndex + 1)
        {
            return;
        }

        for (var i = 0; i < LaunchSteps.Count; i++)
        {
            var item = LaunchSteps[i];
            if (i + 1 < step)
            {
                if (item.Status != LaunchStepStatus.Failed)
                {
                    item.Status = LaunchStepStatus.Completed;
                }
            }
            else if (i + 1 == step)
            {
                item.Status = LaunchStepStatus.Active;
            }
        }

        _launchStepIndex = step - 1;
        LaunchStepTitle = LaunchSteps[step - 1].Title;
        LaunchStepCounterText = $"第 {step} 步 / 共 {LaunchSteps.Count} 步";
    }

    private void FailLaunchStep()
    {
        if (_launchStepIndex >= 0 && _launchStepIndex < LaunchSteps.Count)
        {
            LaunchSteps[_launchStepIndex].Status = LaunchStepStatus.Failed;
        }
    }

    private void CompleteAllLaunchSteps()
    {
        foreach (var item in LaunchSteps)
        {
            item.Status = LaunchStepStatus.Completed;
        }

        _launchStepIndex = LaunchSteps.Count - 1;
        LaunchStepTitle = LaunchSteps[^1].Title;
        LaunchStepCounterText = $"第 {LaunchSteps.Count} 步 / 共 {LaunchSteps.Count} 步";
    }

    /// <summary>由启动阶段文案映射到步骤序号；0 表示该文案不改变步骤（内部重复检查）。</summary>
    private static int StepForLaunchStage(string stage) => stage switch
    {
        "正在准备启动参数…" => 4,
        "正在启动游戏…" => 5,
        _ => 0
    };

    private static int StepForCheckKey(string key) => key switch
    {
        "assets" or "libraries" => 2,
        "java" or "memory" => 3,
        _ => 1
    };

    /// <summary>进入启动页时先填好账号与静态参数；Java 在检查完成后填入真实值。</summary>
    private void PrepareLaunchSummary(MinecraftInstance instance, PlayerAccount? account)
    {
        LaunchAccountSummary = account is null
            ? "未选择账号"
            : $"{LoginTypeLabel(account)} / {account.Name}";
        var versionName = string.IsNullOrWhiteSpace(instance.Name) ? instance.Id : instance.Name;
        LaunchParamsVersion = string.Equals(versionName, instance.GameVersion, StringComparison.OrdinalIgnoreCase)
            ? versionName
            : $"{versionName}（{instance.GameVersion}）";
        LaunchParamsJava = "启动前检查后填入";
        LaunchParamsGameDirectory = _services.Config.EffectiveGameDirectory;
    }

    private static string LoginTypeLabel(PlayerAccount account) => account.Type switch
    {
        "msa" => "正版登录",
        "authlib" => string.IsNullOrWhiteSpace(account.ServerName) ? "皮肤站登录" : $"皮肤站登录 · {account.ServerName}",
        _ => "离线登录"
    };

    private void ApplyPreflightResult(LaunchPreflightResult result)
    {
        LaunchParamsJava = result.JavaSummary ?? "未检测到可用 Java";
    }

    /// <summary>检查未通过：停在失败的那一步，逐项列出原因与建议，允许回到启动页修正。</summary>
    private void ShowPreflightFailure(LaunchPreflightResult result, PlayerAccount? account)
    {
        LaunchFailedChecks.Clear();
        foreach (var item in result.FailedItems)
        {
            LaunchFailedChecks.Add(item);
        }

        // 账号为空的兜底：理论上检查项里已经包含「登录账号」
        if (LaunchFailedChecks.Count == 0 && account is null)
        {
            LaunchFailedChecks.Add(new LaunchCheckItem(
                "account",
                "登录账号",
                "未选择账号。",
                "在启动页「添加账号」选择一个正版 / 皮肤站 / 离线账号后再启动。",
                LaunchCheckStatus.Failed));
        }

        OnPropertyChanged(nameof(HasFailedChecks));
        var first = LaunchFailedChecks[0];
        AdvanceLaunchStep(StepForCheckKey(first.Key));
        FailLaunchStep();
        IsLaunching = false;
        LaunchStageText = "";
        _toast?.Invoke($"启动前检查未通过：{first.Title}");
        ShowLaunchFailure(
            new LaunchFailureInfo(first.Title, first.Detail, first.Suggestion, null),
            "启动前检查未通过");
    }

    /// <summary>启动选项统一在这里组装，检查阶段与真实启动共用同一份取值逻辑。</summary>
    private LaunchOptions BuildLaunchOptions(
        MinecraftInstance instance,
        PlayerAccount? account,
        string? authlibJar,
        string? authlibPrefetched)
    {
        var config = _services.Config;
        var instanceSettings = config.FindInstanceSettings(instance.Id);
        var isolated = instanceSettings?.VersionIsolation ?? true;
        return new LaunchOptions
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
            ExtraGameArguments = string.IsNullOrWhiteSpace(instanceSettings?.ExtraGameArguments)
                ? config.ExtraGameArguments
                : instanceSettings.ExtraGameArguments,
            PlayerName = account?.Name ?? "",
            PlayerUuid = account?.Uuid ?? "",
            AccessToken = account?.AccessToken ?? "",
            UserType = account?.Type switch
            {
                "msa" => "msa",
                "authlib" => "Legacy",
                _ => "legacy"
            },
            AuthClientId = account?.Type == "msa" ? HyclSecrets.MicrosoftOAuthClientId : "",
            AuthXuid = account?.Xuid ?? "",
            AuthlibServer = account?.Type == "authlib" ? account.Server : "",
            AuthlibJarPath = authlibJar ?? "",
            AuthlibPrefetched = authlibPrefetched ?? "",
            WindowTitle = string.IsNullOrWhiteSpace(instanceSettings?.WindowTitle)
                ? config.WindowTitle
                : instanceSettings.WindowTitle,
            TypeInfo = string.IsNullOrWhiteSpace(instanceSettings?.TypeInfo)
                ? config.TypeInfo
                : instanceSettings.TypeInfo,
            LauncherVisibility = config.LauncherVisibility,
            ProcessPriority = config.ProcessPriority,
            PreferredIpStack = config.PreferredIpStack,
            PreLaunchCommand = config.PreLaunchCommand,
            PreLaunchCommandWait = config.PreLaunchCommandWait,
            ServerToEnter = instanceSettings?.ServerToEnter.Trim() ?? ""
        };
    }

    private void AppendLaunchLog(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_launchLogGate)
        {
            _launchLog.Add(line);
            if (_launchLog.Count > MaxLaunchLogLines)
            {
                _launchLog.RemoveRange(0, _launchLog.Count - MaxLaunchLogLines);
            }
        }
    }

    private string[] SnapshotLaunchLog()
    {
        lock (_launchLogGate)
        {
            return _launchLog.ToArray();
        }
    }

    /// <summary>每 200ms 刷新计时；仅在日志展开或已失败时重建日志文本，避免无谓的界面重排。</summary>
    private async Task RunLaunchTickerAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(200, token).ConfigureAwait(true);
                if (_launchWatch is { } watch)
                {
                    LaunchElapsedText = $"{watch.Elapsed.TotalSeconds:0.0} 秒";
                }

                if (IsLaunchLogExpanded || IsLaunchFailed)
                {
                    LaunchLogText = string.Join(Environment.NewLine, SnapshotLaunchLog());
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
        {
            // 会话结束或被新的启动会话接管
        }
    }

    private void StopLaunchTicker()
    {
        try
        {
            _launchTickerCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已释放
        }

        _launchTickerCts?.Dispose();
        _launchTickerCts = null;
        if (_launchWatch is { } watch)
        {
            watch.Stop();
            LaunchElapsedText = $"{watch.Elapsed.TotalSeconds:0.0} 秒";
            _launchWatch = null;
        }
    }

    [RelayCommand]
    private void ToggleLaunchLog() => IsLaunchLogExpanded = !IsLaunchLogExpanded;

    /// <summary>失败页 / 启动页的「返回启动页」：保留日志，只切回启动页。</summary>
    [RelayCommand]
    private void ReturnToLaunchPage()
    {
        IsLaunchProgressVisible = false;
        IsLaunchLogExpanded = false;
    }

    /// <summary>失败页的「重新启动」：清掉失败状态后重新走完整启动流程。</summary>
    [RelayCommand]
    private void RetryLaunch()
    {
        if (IsLaunching)
        {
            return;
        }

        LaunchFailure = null;
        LaunchCommand.Execute(null);
    }

    /// <summary>右下角停止按钮：打开既有的关闭游戏二次确认（存档 / MOD 风险提示）。</summary>
    [RelayCommand]
    private void RequestCloseGame()
    {
        if (IsGameRunning)
        {
            IsCloseGameConfirmVisible = true;
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
        if (!IsGameRunning || _sessions.Count == 0)
        {
            return;
        }

        var closingSessions = _sessions.ToArray();
        IsClosing = true;
        try
        {
            foreach (var session in closingSessions)
            {
                try
                {
                    await session.StopGracefullyAsync(line => _services.Logger.Info(line)).ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    _services.Logger.Error("关闭游戏失败", ex);
                    _toast?.Invoke($"关闭游戏失败：{ex.Message}");
                }

                if (session.HasExited)
                {
                    _sessions.Remove(session);
                }
            }
        }
        finally
        {
            IsClosing = false;
            IsGameRunning = _sessions.Count > 0;
        }
    }

    private async Task WatchSessionAsync(GameSession session, string gameDirectory)
    {
        try
        {
            await session.WaitForExitAsync().ConfigureAwait(false);
            var exitCode = session.ExitCode;

            // 非零退出码 = 游戏崩溃：定位最新崩溃报告并引导玩家（对齐 PCL-CE 的崩溃后体验）
            string? crashReportPath = null;
            if (exitCode != 0)
            {
                try
                {
                    var crashDirectory = Path.Combine(gameDirectory, "crash-reports");
                    if (Directory.Exists(crashDirectory))
                    {
                        crashReportPath = Directory
                            .EnumerateFiles(crashDirectory, "*.txt")
                            .Concat(Directory.EnumerateFiles(crashDirectory, "*.html"))
                            .OrderByDescending(File.GetLastWriteTime)
                            .FirstOrDefault();
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _services.Logger.Warn($"查找崩溃报告失败：{ex.Message}");
                }
            }

            _services.Dispatcher.Post(() =>
            {
                _sessions.Remove(session);
                IsLaunching = false;
                IsGameRunning = _sessions.Count > 0;
                if (!IsGameRunning)
                {
                    IsClosing = false;
                }

                LaunchStageText = "";
                SetLaunchProgress(0d);
                if (exitCode != 0)
                {
                    // 崩溃 = 游戏进程非零退出（此时窗口已出现过）：进入失败页展示结论 + 可滚动日志，
                    // 并指向最新一份 crash-report（PCL-CE 的崩溃后体验）。
                    var failure = LaunchFailureAnalyzer
                        .Analyze(exitCode, SnapshotLaunchLog(), null)
                        with { CrashedInGame = true, CrashReportPath = crashReportPath };
                    AppendLaunchLog($"[崩溃] 游戏进程异常退出，退出码 {exitCode}");
                    CrashReportName = crashReportPath is null ? null : Path.GetFileName(crashReportPath);
                    if (crashReportPath is not null)
                    {
                        LatestCrashReportPath = crashReportPath;
                    }

                    ShowLaunchFailure(failure, "游戏崩溃 · 已在游戏中崩溃");
                    _toast?.Invoke(crashReportPath is null
                        ? $"游戏异常退出（退出码 {exitCode}），可在「工具 → 日志」查看输出"
                        : $"游戏异常退出（退出码 {exitCode}），已为你定位最新崩溃报告");
                }
                else
                {
                    _toast?.Invoke($"游戏已正常退出");
                    IsCrashBannerVisible = false;
                }
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"等待游戏退出时出错：{ex.Message}");
            _services.Dispatcher.Post(() =>
            {
                _sessions.Remove(session);
                IsLaunching = false;
                IsGameRunning = _sessions.Count > 0;
                if (!IsGameRunning)
                {
                    IsClosing = false;
                }

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

    /// <summary>打开离线皮肤选择弹窗（正版/皮肤站账号的皮肤自动获取，不在此更换）。</summary>
    [RelayCommand]
    private void ChangeSkin()
    {
        var account = _services.Accounts.SelectedAccount;
        if (account is null)
        {
            _toast?.Invoke("请先添加账号");
            return;
        }

        if (account.Type != "offline")
        {
            _toast?.Invoke(account.Type == "msa"
                ? "正版账号的皮肤跟随微软档案自动获取"
                : "皮肤站账号的皮肤跟随皮肤站设置");
            return;
        }

        IsSkinPickerVisible = true;
    }

    [ObservableProperty]
    private bool _isSkinPickerVisible;

    [RelayCommand]
    private void CloseSkinPicker() => IsSkinPickerVisible = false;

    // ------------------------- 登录对话框（添加账号：正版 / 皮肤站 / 离线） -------------------------

    [ObservableProperty]
    private bool _isLoginDialogVisible;

    /// <summary>游戏崩溃横幅：退出码非 0 且找到崩溃报告时显示。</summary>
    [ObservableProperty]
    private bool _isCrashBannerVisible;

    /// <summary>最新崩溃报告文件路径。</summary>
    [ObservableProperty]
    private string? _latestCrashReportPath;

    /// <summary>用系统默认程序打开崩溃报告（对齐 PCL-CE 的崩溃后引导）。</summary>
    [RelayCommand]
    private void OpenCrashReport()
    {
        if (string.IsNullOrWhiteSpace(LatestCrashReportPath) || !File.Exists(LatestCrashReportPath))
        {
            _toast?.Invoke("崩溃报告文件不存在");
            return;
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo(LatestCrashReportPath) { UseShellExecute = true });
            IsCrashBannerVisible = false;
        }
        catch (Exception ex)
        {
            _toast?.Invoke($"无法打开崩溃报告：{ex.Message}");
        }
    }

    [RelayCommand]
    private void DismissCrashBanner() => IsCrashBannerVisible = false;

    /// <summary>失败页「打开日志目录」：定位启动器本次运行写入的日志文件夹。</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        if (_services.System.OpenFolder(_services.Paths.LogDirectory))
        {
            return;
        }

        _toast?.Invoke("无法打开日志目录");
    }

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

    /// <summary>供视图层（剪贴板等 UI 关切）使用统一 toast 通道。</summary>
    public void ShowToast(string message) => _toast?.Invoke(message);

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

                    // 登录后自动拉取玩家真实皮肤并刷新首页纸娃娃
                    if (!string.IsNullOrWhiteSpace(poll.Result.SkinUrl))
                    {
                        var account = _services.Accounts.Accounts
                            .FirstOrDefault(x => x.Uuid.Equals(poll.Result.Uuid, StringComparison.OrdinalIgnoreCase));
                        if (account is not null)
                        {
                            _ = DownloadSkinAsync(account, poll.Result.SkinUrl)
                                .ContinueWith(_ => _services.Dispatcher.Post(BuildProfiles), TaskScheduler.Default);
                        }
                    }

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

        // 登录后自动拉取皮肤站皮肤并刷新首页纸娃娃
        var account = _services.Accounts.Accounts.FirstOrDefault(x =>
            x.Type == "authlib" && x.Uuid.Equals(result.Uuid, StringComparison.OrdinalIgnoreCase));
        if (account is not null)
        {
            _ = Task.Run(async () =>
            {
                var skinUrl = await _services.AuthlibAuth
                    .TryGetSkinUrlAsync(result.ApiRoot, result.Uuid)
                    .ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(skinUrl))
                {
                    await DownloadSkinAsync(account, skinUrl).ConfigureAwait(false);
                    _services.Dispatcher.Post(BuildProfiles);
                }
            });
        }
    }

    /// <summary>
    /// 编辑当前档案：离线档案等价于修改玩家名，复用重命名弹窗（只有玩家名一个输入项）。
    /// 正版/皮肤站档案的名称由账号体系维护，不在此编辑。
    /// </summary>
    [RelayCommand]
    private void EditProfile()
    {
        var profile = ActiveProfile;
        if (profile is null)
        {
            _toast?.Invoke("请先添加账号");
            return;
        }

        if (!profile.IsOffline || string.IsNullOrEmpty(profile.AccountUuid))
        {
            _toast?.Invoke("仅离线档案可修改玩家名");
            return;
        }

        StartRenameProfile(profile);
    }

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

/// <summary>启动过程页单步状态。</summary>
public enum LaunchStepStatus
{
    Pending,
    Active,
    Completed,
    Failed
}

/// <summary>启动过程页的一步：序号、标题、状态标记。</summary>
public sealed partial class LaunchStepItemViewModel : ObservableObject
{
    public LaunchStepItemViewModel(int number, string title)
    {
        Number = number;
        Title = title;
    }

    public int Number { get; }

    public string Title { get; }

    public string NumberText => Number.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Marker))]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    [NotifyPropertyChangedFor(nameof(IsCompleted))]
    [NotifyPropertyChangedFor(nameof(IsFailed))]
    private LaunchStepStatus _status = LaunchStepStatus.Pending;

    public string Marker => Status switch
    {
        LaunchStepStatus.Active => "▶",
        LaunchStepStatus.Completed => "✓",
        LaunchStepStatus.Failed => "✕",
        _ => "○"
    };

    public bool IsPending => Status == LaunchStepStatus.Pending;

    public bool IsActive => Status == LaunchStepStatus.Active;

    public bool IsCompleted => Status == LaunchStepStatus.Completed;

    public bool IsFailed => Status == LaunchStepStatus.Failed;
}
