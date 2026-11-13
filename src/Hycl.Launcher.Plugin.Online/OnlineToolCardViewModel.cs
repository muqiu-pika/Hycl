using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.PluginContract;

namespace Hycl.Launcher.Plugin.Online;

/// <summary>工具卡剪贴板抽象（由卡片视图在挂载后注入，规避插件直接依赖窗口）。</summary>
public interface ICardClipboard
{
    Task CopyAsync(string text);
}

/// <summary>
/// 陶瓦联机工具卡 ViewModel：安装进度 / 建房 / 加入 / 房间码与地址展示 / 日志（IP 消敏）。
/// 引擎事件在后台线程到达，统一经 <see cref="IPluginContext.Dispatch"/> 回 UI 线程。
/// </summary>
public partial class OnlineToolCardViewModel : ObservableObject
{
    private const int MaxLogLines = 200;

    private readonly IPluginContext _context;
    private readonly TerracottaService _service;

    /// <summary>手动下载（补齐）进行中：避免引擎的 Idle 状态把手动进度提前关掉。</summary>
    private bool _manualInstalling;

    public OnlineToolCardViewModel(IPluginContext context, TerracottaService service)
    {
        _context = context;
        _service = service;
        _service.Log += OnServiceLog;
        _service.StateChanged += OnServiceStateChanged;
        // 功能页可能在会话进行中（房间已创建）才被创建：先用服务当前快照初始化，
        // 否则要等下一个状态事件才会隐藏「创建房间」表单并出现「关闭房间」按钮
        ApplyState(_service.Current);
        _ = RefreshBinaryStateAsync();
    }

    /// <summary>由卡片视图注入（挂载到可视树后）。</summary>
    public ICardClipboard? Clipboard { get; set; }

    // ---------- 安装态 ----------

    [ObservableProperty]
    private bool _isBinaryReady;

    [ObservableProperty]
    private string _installStatusText = "正在检查陶瓦工具…";

    [ObservableProperty]
    private bool _isInstalling;

    [ObservableProperty]
    private string _installButtonText = "补齐文件后重试";

    [ObservableProperty]
    private double _downloadPercent;

    [ObservableProperty]
    private string _platformUnsupportedText = "";

    // ---------- 插件设置区（功能页内「插件设置」） ----------

    [ObservableProperty]
    private string _toolStatusText = "正在校验…";

    [ObservableProperty]
    private string _settingsErrorText = "";

    [ObservableProperty]
    private bool _isCheckingTool;

    [ObservableProperty]
    private bool _isRepairing;

    // ---------- 会话态 ----------

    [ObservableProperty]
    private string _phaseText = "未连接";

    [ObservableProperty]
    private string _stateRawText = "";

    [ObservableProperty]
    private string _roomCode = "";

    [ObservableProperty]
    private string _joinCodeInput = "";

    [ObservableProperty]
    private string _gameAddress = "";

    [ObservableProperty]
    private string _errorText = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>会话存在（启动中/建房/加入/已就绪）：期间隐藏建加表单并显示「关闭房间」按钮。</summary>
    [ObservableProperty]
    private bool _isSessionActive;

    [ObservableProperty]
    private string _stopButtonText = "停止联机";

    public ObservableCollection<string> Logs { get; } = [];

    public string UpstreamUrl => "https://github.com/burningtnt/Terracotta";

    public string PluginHomeUrl => "https://github.com/muqiu-pika/Hycl-online";

    /// <summary>插件版本（与 plugin.json / csproj 同源）。</summary>
    public string PluginVersion => typeof(OnlineToolCardViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string ToolVersion => _service.ToolVersionNumber;

    public string PlatformKey => TerracottaService.PlatformAssetKey() ?? "当前平台不受支持";

    public string ToolDirectory => _service.ToolDirectory;

    private bool IsReady => !string.IsNullOrEmpty(GameAddress) || _service.Current.Phase == TerracottaPhase.Ready;

    // ---------- 命令 ----------

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task InstallAsync(CancellationToken cancellationToken)
    {
        if (IsInstalling)
        {
            return;
        }

        _manualInstalling = true;
        IsInstalling = true;
        InstallButtonText = "正在下载…";
        DownloadPercent = 0;
        ErrorText = "";
        try
        {
            var progress = new Progress<double>(value => _context.Dispatch(() => DownloadPercent = Math.Clamp(value * 100, 0, 100)));
            await _service.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(true);
            await RefreshBinaryStateAsync().ConfigureAwait(true);
            _context.Dispatch(() => InstallStatusText = $"陶瓦 v{_service.ToolVersionNumber} 已就绪");
        }
        catch (OperationCanceledException)
        {
            // 用户取消：断点临时文件已保留，重新点击会从断点继续
            _context.Dispatch(() => InstallStatusText = "已取消下载，进度已保留，再次点击「补齐文件后重试」将继续下载");
        }
        catch (Exception ex)
        {
            _context.Dispatch(() =>
            {
                ErrorText = ex.Message;
                InstallStatusText = TerracottaService.RepairHint;
            });
        }
        finally
        {
            _manualInstalling = false;
            _context.Dispatch(() =>
            {
                IsInstalling = false;
                InstallButtonText = "补齐文件后重试";
            });
        }
    }

    [RelayCommand]
    private async Task CreateRoomAsync(CancellationToken cancellationToken)
    {
        ErrorText = "";
        try
        {
            await _service.StartAsync(TerracottaMode.Host, null, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 「关闭房间 / 停止联机」取消了轮询：状态复位由 StopAsync 负责。
            // 取消绝不能逃出命令 —— AsyncRelayCommand.AwaitAndThrowIfFailed 会把它抛到 UI 线程，
            // 冒泡出主消息循环后被当成「启动失败」弹框并退出整个启动器。
        }
        catch (Exception ex)
        {
            _context.Dispatch(() => ErrorText = ex.Message);
        }
    }

    [RelayCommand]
    private async Task JoinRoomAsync(CancellationToken cancellationToken)
    {
        ErrorText = "";
        try
        {
            await _service.StartAsync(TerracottaMode.Join, JoinCodeInput, cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // 同 CreateRoomAsync：取消来自「关闭房间 / 停止联机」，状态已由 StopAsync 复位
        }
        catch (Exception ex)
        {
            _context.Dispatch(() => ErrorText = ex.Message);
        }
    }

    [RelayCommand]
    private async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _service.StopAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 关闭房间的失败要显示在卡片上，不能逃出命令（会弹「启动失败」并关掉启动器）
            _context.Dispatch(() => ErrorText = "关闭房间时出错：" + ex.Message);
        }
    }

    [RelayCommand]
    private async Task CopyRoomAsync(CancellationToken cancellationToken)
    {
        if (Clipboard is not null && RoomCode.Length > 0)
        {
            await Clipboard.CopyAsync(RoomCode).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private async Task CopyAddressAsync(CancellationToken cancellationToken)
    {
        if (Clipboard is not null && GameAddress.Length > 0)
        {
            await Clipboard.CopyAsync(GameAddress).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void OpenUpstream() => _context.OpenUrl(UpstreamUrl);

    [RelayCommand]
    private void OpenPluginHome() => _context.OpenUrl(PluginHomeUrl);

    /// <summary>设置区：重新校验本地二进制（对应 KAMUCL 陶瓦面板的「重新校验」）。</summary>
    [RelayCommand]
    private async Task RefreshToolAsync(CancellationToken cancellationToken)
    {
        if (IsCheckingTool)
        {
            return;
        }

        IsCheckingTool = true;
        SettingsErrorText = "";
        ToolStatusText = "正在校验…";
        try
        {
            var ready = await _service.IsBinaryReadyAsync(cancellationToken).ConfigureAwait(true);
            _context.Dispatch(() =>
            {
                IsBinaryReady = ready;
                ToolStatusText = ready
                    ? "已安装并通过 SHA-256 校验"
                    : "未安装或校验失败，可用「修复安装」重新下载";
                InstallStatusText = ready ? $"陶瓦 v{_service.ToolVersionNumber} 已就绪" : "尚未安装陶瓦工具";
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _context.Dispatch(() =>
            {
                ToolStatusText = "校验失败";
                SettingsErrorText = ex.Message;
            });
        }
        finally
        {
            _context.Dispatch(() => IsCheckingTool = false);
        }
    }

    /// <summary>设置区：重新下载/修复工具（多镜像回退 + 双 SHA-256 校验；可取消，取消保留断点）。</summary>
    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RepairToolAsync(CancellationToken cancellationToken)
    {
        if (IsRepairing)
        {
            return;
        }

        IsRepairing = true;
        SettingsErrorText = "";
        DownloadPercent = 0;
        try
        {
            var progress = new Progress<double>(value =>
                _context.Dispatch(() => DownloadPercent = Math.Clamp(value * 100, 0, 100)));
            await _service.EnsureInstalledAsync(progress, cancellationToken).ConfigureAwait(true);
            await RefreshToolAsync(CancellationToken.None).ConfigureAwait(true);
            _context.Dispatch(() => InstallStatusText = $"陶瓦 v{_service.ToolVersionNumber} 已就绪");
        }
        catch (OperationCanceledException)
        {
            _context.Dispatch(() => SettingsErrorText = "已取消下载，进度已保留，再次点击「修复安装」将继续下载");
        }
        catch (Exception ex)
        {
            _context.Dispatch(() => SettingsErrorText = ex.Message);
        }
        finally
        {
            _context.Dispatch(() => IsRepairing = false);
        }
    }

    // ---------- 引擎事件 ----------

    private void OnServiceLog(string level, string message)
    {
        _context.Dispatch(() =>
        {
            Logs.Add($"[{level}] {SanitizeLog(message)}");
            while (Logs.Count > MaxLogLines)
            {
                Logs.RemoveAt(0);
            }
        });
    }

    private void OnServiceStateChanged(TerracottaState state)
    {
        _context.Dispatch(() => ApplyState(state));
    }

    private void ApplyState(TerracottaState state)
    {
        PhaseText = DescribePhase(state);
        StateRawText = state.StateRaw ?? "";
        RoomCode = state.Room ?? "";
        GameAddress = state.Url ?? "";
        ErrorText = state.Error ?? "";
        IsBusy = state.Phase is TerracottaPhase.Starting or TerracottaPhase.Hosting or TerracottaPhase.Joining;
        // 就绪后房间仍在运行：会话区保持展开，按钮切换为「关闭房间」，否则用户无从退出
        IsSessionActive = state.Phase
            is TerracottaPhase.Starting
            or TerracottaPhase.Hosting
            or TerracottaPhase.Joining
            or TerracottaPhase.Ready;
        StopButtonText = state.Phase == TerracottaPhase.Ready ? "关闭房间" : "停止联机";
        OnPropertyChanged(nameof(IsReady));

        // 工具态由引擎快照驱动：自动下载显示进度，校验失败显示补齐入口，通过后整块隐藏
        IsBinaryReady = state.BinaryReady;
        if (state.Phase == TerracottaPhase.Downloading)
        {
            IsInstalling = true;
            InstallStatusText = "正在自动下载陶瓦工具…";
        }
        else if (!_manualInstalling)
        {
            IsInstalling = false;
            InstallStatusText = state.BinaryReady
                ? $"陶瓦 v{_service.ToolVersionNumber} 已就绪"
                : string.IsNullOrWhiteSpace(state.Error) ? "正在检查陶瓦工具…" : state.Error;
        }
    }

    private async Task RefreshBinaryStateAsync()
    {
        var supported = TerracottaService.PlatformAssetKey() is not null;
        PlatformUnsupportedText = supported ? "" : "当前平台不受陶瓦联机支持（仅支持 Windows/Linux/macOS 的 x64 与 arm64）。";
        if (!supported)
        {
            InstallStatusText = "平台不受支持";
            ToolStatusText = "当前平台不受支持，无法下载工具";
            return;
        }

        // 引擎正在自动下载时不要抢先下结论，交给 StateChanged 驱动进度
        if (_service.Current.Phase == TerracottaPhase.Downloading)
        {
            _context.Dispatch(() =>
            {
                IsInstalling = true;
                InstallStatusText = "正在自动下载陶瓦工具…";
            });
            return;
        }

        try
        {
            var ready = await _service.IsBinaryReadyAsync().ConfigureAwait(true);
            _context.Dispatch(() =>
            {
                IsBinaryReady = ready;
                InstallStatusText = ready ? $"陶瓦 v{_service.ToolVersionNumber} 已就绪" : TerracottaService.RepairHint;
                ToolStatusText = ready
                    ? "已安装并通过 SHA-256 校验"
                    : "未安装或校验失败，点击「补齐文件后重试」重新下载";
            });
        }
        catch (Exception ex)
        {
            // 检查失败必须落到 UI，否则卡片会永远停在「正在检查陶瓦工具…」占位文案。
            // MissingMethodException / TypeLoadException = 插件与启动器契约 ABI 错位（旧插件跑在新宿主上），
            // 文案要指向「更新插件」而不是让用户以为是文件损坏。
            _context.Log.Warn("检查陶瓦工具状态失败：" + ex);
            var incompatible = TerracottaService.IsAbiMismatch(ex);
            _context.Dispatch(() =>
            {
                InstallStatusText = incompatible
                    ? "插件与启动器版本不兼容，请在「设置 → 插件工坊」更新插件后重启"
                    : $"陶瓦工具检查失败：{ex.Message}";
                ToolStatusText = incompatible
                    ? "当前插件版本早于启动器（契约成员已变更），更新插件即可恢复"
                    : "检查未完成，可点击「补齐文件后重试」";
            });
        }
    }

    private static string DescribePhase(TerracottaState state) => state.Phase switch
    {
        TerracottaPhase.Downloading => "正在下载陶瓦工具",
        TerracottaPhase.Starting => "正在启动陶瓦",
        // 上游状态机：host-scanning 需要本机 MC 已对局域网开放，host-starting 才是在打洞。
        // 两者文案必须分开，否则用户看到「正在创建房间」一直转圈，不知道要先去开局域网。
        TerracottaPhase.Hosting => state.StateRaw switch
        {
            "host-scanning" => "正在扫描本机 Minecraft 端口 —— 请先在游戏里「对局域网开放」世界",
            "host-starting" => "房间码已生成，正在通过公共节点建立连接（网络较差时需 1-2 分钟）",
            "host-ok" => "房间已就绪，把房间码发给好友即可",
            _ => "正在创建房间",
        },
        TerracottaPhase.Joining => state.StateRaw switch
        {
            "guest-connecting" or "guest-starting" => "正在连接房主（网络较差时需 1-2 分钟）",
            "guest-ok" => "连接就绪",
            _ => "正在加入房间",
        },
        TerracottaPhase.Ready => "连接就绪",
        _ => string.IsNullOrEmpty(state.Error) ? "未连接" : "连接未完成",
    };

    /// <summary>日志消敏：远程地址打码，仅保留 127.0.0.1（那是用户要填的地址）。</summary>
    private static string SanitizeLog(string text) => IpPattern()
        .Replace(text, match =>
            match.Value.StartsWith("127.0.0.1", StringComparison.Ordinal) ? match.Value : "***");

    [GeneratedRegex(@"(\d{1,3}\.){3}\d{1,3}(:\d+)?")]
    private static partial Regex IpPattern();
}
