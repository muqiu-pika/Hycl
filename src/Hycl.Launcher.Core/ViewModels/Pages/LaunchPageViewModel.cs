using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Models;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 启动页 ViewModel：玩家档案切换、当前版本展示、启动与快捷方式。
/// 表现层数据来自 Hycl (2) 的启动页，业务调用继续使用现有 Core 的实例扫描与启动服务。
/// </summary>
public partial class LaunchPageViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private GameSession? _session;
    private MinecraftInstance? _selectedInstance;

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
    private bool _isLaunching;

    /// <summary>已安装实例列表（仅内部维护，启动页按当前选中实例展示）。</summary>
    public ObservableCollection<MinecraftInstance> Instances { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionSummary))]
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
    private string _loaderName = "Vanilla";

    /// <summary>启动按钮副标题：版本号 + 加载器。</summary>
    public string VersionSummary => $"{CurrentVersion} · {LoaderName}";

    public string LaunchText => IsLaunching ? "正在启动…" : "启动游戏";

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

    /// <summary>请求切换到其他标签页（“下载游戏”快捷方式使用）。</summary>
    public event EventHandler<string>? NavigateRequested;

    /// <summary>
    /// 重新扫描 versions 目录并刷新当前版本；安装完成或进入启动页时调用。
    /// preferredId 为新安装实例的目录名，未指定时优先使用配置中的已选版本。
    /// </summary>
    public async Task RefreshInstancesAsync(string? preferredId = null)
    {
        try
        {
            var list = await _services.Instances
                .ScanAsync(_services.Config.EffectiveGameDirectory)
                .ConfigureAwait(true);

            Instances.Clear();
            foreach (var instance in list)
            {
                Instances.Add(instance);
            }

            var target = FindTarget(list, preferredId);
            _selectedInstance = target;
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

            if (!target.Id.Equals(_services.Config.SelectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                _services.Config.SelectedVersion = target.Id;
                await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            _services.Logger.Error("刷新启动页实例失败", ex);
        }
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
                account.Type == "offline" ? "离线账户" : account.Type,
                SkinFor(index),
                isActive: index == selectedIndex));
        }

        ActiveProfile = Profiles.FirstOrDefault(x => x.IsActive) ?? Profiles.FirstOrDefault();
    }

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
        if (IsLaunching)
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
        try
        {
            var config = _services.Config;
            var account = _services.Accounts.SelectedAccount;
            var options = new LaunchOptions
            {
                InstanceId = instance.Id,
                GameDirectory = config.EffectiveGameDirectory,
                JavaPath = config.JavaPath,
                MemoryMegabytes = config.GetEffectiveMemoryMegabytes(),
                WindowType = config.WindowType,
                WindowWidth = config.WindowWidth,
                WindowHeight = config.WindowHeight,
                ExtraJvmArguments = config.ExtraJvmArguments,
                ExtraGameArguments = config.ExtraGameArguments,
                PlayerName = account.Name,
                PlayerUuid = account.Uuid
            };

            var session = await _services.Launch
                .LaunchAsync(options, line => _services.Logger.Info(line))
                .ConfigureAwait(true);
            _session = session;
            LaunchCount++;
            _toast?.Invoke($"已启动 {instance.Name}（PID {session.Id}）");
            if (config.CloseLauncherAfterLaunch)
            {
                CloseRequested?.Invoke(this, EventArgs.Empty);
            }

            _ = WatchSessionAsync(session);
        }
        catch (LaunchException ex)
        {
            IsLaunching = false;
            _toast?.Invoke(ex.Message);
            _services.Logger.Error("启动游戏失败", ex);
        }
        catch (Exception ex)
        {
            IsLaunching = false;
            _toast?.Invoke($"启动失败：{ex.Message}");
            _services.Logger.Error("启动游戏失败", ex);
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
                _toast?.Invoke($"游戏已退出（退出码 {exitCode}）");
            });
        }
        catch (Exception ex)
        {
            _services.Logger.Warn($"等待游戏退出时出错：{ex.Message}");
            _services.Dispatcher.Post(() => IsLaunching = false);
        }
        finally
        {
            session.Dispose();
        }
    }

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

    [RelayCommand]
    private void EditProfile() => _toast?.Invoke("编辑档案暂未支持，可在配置文件中修改玩家名");

    [RelayCommand]
    private async Task ManageInstanceAsync()
    {
        await RefreshInstancesAsync().ConfigureAwait(true);
        _toast?.Invoke(Instances.Count == 0
            ? "尚未安装任何版本"
            : $"已安装 {Instances.Count} 个版本：{string.Join("、", Instances.Select(x => x.Name))}");
    }

    [RelayCommand]
    private void ShowQuickLink(QuickLinkViewModel? link)
    {
        if (link is null)
        {
            return;
        }

        if (link.Title == "下载游戏")
        {
            NavigateRequested?.Invoke(this, "download");
            return;
        }

        _toast?.Invoke(link.Title switch
        {
            "版本列表" => Instances.Count == 0
                ? "尚未安装任何版本"
                : $"已安装 {Instances.Count} 个版本：{string.Join("、", Instances.Select(x => x.Name))}",
            "快速联机" => "联机功能暂未支持",
            _ => "整合包安装暂未支持"
        });
    }
}

/// <summary>快捷方式条目。</summary>
public sealed record QuickLinkViewModel(string Title, string Subtitle, string IconKey);
