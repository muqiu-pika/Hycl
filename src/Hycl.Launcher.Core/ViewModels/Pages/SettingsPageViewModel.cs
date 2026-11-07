using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Models;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.Core.ViewModels.Pages;

/// <summary>
/// 设置页 ViewModel：侧边导航 + 数据驱动的设置卡片。
/// 页面结构与分组沿用 Hycl (2)，设置项改接现有 Core 的 <see cref="LauncherConfig"/>。
/// </summary>
public partial class SettingsPageViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private readonly Action<string>? _toast;
    private readonly Dictionary<string, SettingsSectionViewModel> _sections = new();
    private readonly object _loadLock = new();
    private Task? _loadTask;
    private int _saveVersion;

    private const int MinimumLoadingMilliseconds = 220;

    private static readonly string[] MemoryOptions =
    [
        "自动分配", "1024 MB", "2048 MB", "4096 MB", "8192 MB", "16384 MB"
    ];

    private static readonly string[] WindowOptions =
    [
        "默认", "全屏", "与启动器相同", "自定义", "最大化"
    ];

    private static readonly string[] ThreadOptions = ["4", "8", "16", "32"];

    private static readonly string[] RetryOptions = ["0", "1", "2", "3", "5"];

    /// <summary>游戏内设置（options.txt）的取值档位。</summary>
    private static readonly string[] GuiScaleOptions = ["自动", "1 小", "2 标准", "3 大", "4 最大"];

    private static readonly string[] MaxFpsOptions = ["60", "90", "120", "144", "165", "240", "不限"];

    private static readonly string[] GraphicsOptions = ["流畅", "精致", "绚丽"];

    private static readonly string[] ParticleOptions = ["全部", "减少", "最少"];

    private static readonly string[] MainHandOptions = ["右手", "左手"];

    private static readonly string[] NarratorOptions = ["关闭", "全部", "仅聊天", "仅系统"];

    private static readonly string[] ChatVisibilityOptions = ["完整显示", "仅命令", "隐藏"];

    /// <summary>游戏语言：显示名与 options.txt 中的语言代码。</summary>
    private static readonly (string Label, string Code)[] GameLanguageOptions =
    [
        ("简体中文", "zh_cn"),
        ("繁體中文", "zh_tw"),
        ("English (US)", "en_us"),
        ("日本語", "ja_jp"),
        ("한국어", "ko_kr"),
        ("Русский", "ru_ru"),
        ("Deutsch", "de_de"),
        ("Français", "fr_fr"),
        ("Español", "es_es"),
    ];

    private static readonly string[] GameLanguageLabels =
        GameLanguageOptions.Select(pair => pair.Label).ToArray();

    private static string ParticleLabel(string value) => value switch
    {
        "decreased" => "减少",
        "minimal" => "最少",
        _ => "全部"
    };

    private static string GraphicsLabel(string value) => value switch
    {
        "fast" => "流畅",
        "fabulous" => "绚丽",
        _ => "精致"
    };

    private static string LanguageLabel(string code)
    {
        foreach (var pair in GameLanguageOptions)
        {
            if (pair.Code.Equals(code, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Label;
            }
        }

        return "简体中文";
    }

    private static string LanguageCode(string label)
    {
        foreach (var pair in GameLanguageOptions)
        {
            if (pair.Label == label)
            {
                return pair.Code;
            }
        }

        return "zh_cn";
    }

    /// <summary>下载源三档（对应 PCL-CE 的下载源设置文案）。</summary>
    public static readonly string[] DownloadSourceOptions =
    [
        "尽量使用镜像源",
        "优先使用官方源，在加载缓慢时换用镜像源",
        "尽量使用官方源"
    ];

    /// <summary>社区资源源三档（对应 PCL-CE ComboDownloadMod 文案）。</summary>
    public static readonly string[] CommunitySourceOptions =
    [
        "使用镜像源",
        "优先官方源，失败时使用镜像",
        "仅官方源"
    ];

    /// <summary>启动器可见性文案与配置值（对应 PCL-CE LaunchArgumentVisible 常用档）。</summary>
    public static readonly string[] LauncherVisibilityOptions =
    [
        "游戏启动后立即关闭",
        "启动游戏后最小化",
        "保持不变"
    ];

    public static readonly int[] LauncherVisibilityValues = [0, 4, 5];

    /// <summary>进程优先级文案与配置值（对应 PCL-CE LaunchArgumentPriority）。</summary>
    public static readonly string[] ProcessPriorityOptions = ["高", "中（推荐）", "低", "极高", "实时"];

    public static readonly int[] ProcessPriorityValues = [0, 1, 2, 3, 4];

    /// <summary>IP 协议偏好（对应 PCL-CE LaunchPreferredIpStack）。</summary>
    public static readonly string[] IpStackOptions = ["IPv4 优先（推荐）", "Java 默认", "IPv6 优先"];

    /// <summary>初始内存选项。</summary>
    public static readonly string[] InitialMemoryOptions = ["自动", "512 MB", "1024 MB", "2048 MB", "4096 MB"];

    /// <summary>快速下载行为（对应 PCL-CE ToolDownloadQuickBehavior）。</summary>
    public static readonly string[] QuickDownloadOptions = ["总是询问", "安装到当前实例", "询问安装到哪个实例", "询问保存路径"];

    /// <summary>更新通道（对应 PCL-CE SystemUpdateChannel）。</summary>
    public static readonly string[] UpdateChannelOptions = ["正式版", "测试版", "开发版"];

    /// <summary>当前主题（light/dark），供 UI 启动时应用。</summary>
    public string CurrentTheme => _services.Config.Theme;

    /// <summary>主题变更事件（UI 层订阅后落到 Avalonia ThemeVariant）。</summary>
    public event EventHandler<string>? ThemeChanged;

    private ComboRowViewModel? _memoryRow;
    private ComboRowViewModel? _initialMemoryRow;
    private ToggleRowViewModel? _lockMemoryRow;
    private ComboRowViewModel? _windowRow;
    private ComboRowViewModel? _launcherVisibilityRow;
    private ComboRowViewModel? _processPriorityRow;
    private ComboRowViewModel? _ipStackRow;
    private TextRowViewModel? _windowTitleRow;
    private TextRowViewModel? _typeInfoRow;
    private TextRowViewModel? _jvmArgsRow;
    private TextRowViewModel? _gameArgsRow;
    private TextRowViewModel? _preLaunchRow;
    private ToggleRowViewModel? _preLaunchWaitRow;
    private TextRowViewModel? _gameDirectoryRow;
    private ToggleRowViewModel? _autoJavaRow;
    private TextRowViewModel? _javaPathRow;
    private MutableInfoRowViewModel? _javaDetectedRow;
    private ComboRowViewModel? _updateChannelRow;
    private MutableInfoRowViewModel? _updateStatusRow;
    private MutableLogRowViewModel? _logTailRow;
    private ComboRowViewModel? _versionListSourceRow;
    private ComboRowViewModel? _fileSourceRow;
    private ComboRowViewModel? _communitySourceRow;
    private ToggleRowViewModel? _autoDependenciesRow;
    private ToggleRowViewModel? _ignoreQuiltRow;
    private ComboRowViewModel? _quickDownloadRow;
    private ToggleRowViewModel? _fixAuthlibRow;
    private ToggleRowViewModel? _autoSelectInstanceRow;
    private ComboRowViewModel? _threadsRow;
    private ComboRowViewModel? _retryRow;
    private ComboRowViewModel? _themeRow;

    private ToggleRowViewModel? _gameSettingsEnabledRow;
    private SliderRowViewModel? _renderDistanceRow;
    private SliderRowViewModel? _simulationDistanceRow;
    private SliderRowViewModel? _fovRow;
    private ComboRowViewModel? _guiScaleRow;
    private ComboRowViewModel? _maxFpsRow;
    private ToggleRowViewModel? _vsyncRow;
    private ComboRowViewModel? _graphicsRow;
    private ComboRowViewModel? _particlesRow;
    private ToggleRowViewModel? _cloudsRow;
    private ToggleRowViewModel? _smoothLightingRow;
    private ToggleRowViewModel? _entityShadowsRow;
    private SliderRowViewModel? _gammaRow;
    private SliderRowViewModel? _fovEffectRow;
    private SliderRowViewModel? _screenEffectRow;
    private SliderRowViewModel? _mouseSensitivityRow;
    private ToggleRowViewModel? _invertMouseRow;
    private ToggleRowViewModel? _autoJumpRow;
    private ComboRowViewModel? _mainHandRow;
    private SliderRowViewModel? _masterVolumeRow;
    private SliderRowViewModel? _musicVolumeRow;
    private SliderRowViewModel? _recordVolumeRow;
    private SliderRowViewModel? _weatherVolumeRow;
    private SliderRowViewModel? _blockVolumeRow;
    private SliderRowViewModel? _hostileVolumeRow;
    private SliderRowViewModel? _neutralVolumeRow;
    private SliderRowViewModel? _playerVolumeRow;
    private SliderRowViewModel? _ambientVolumeRow;
    private SliderRowViewModel? _voiceVolumeRow;
    private SliderRowViewModel? _chatOpacityRow;
    private SliderRowViewModel? _chatScaleRow;
    private SliderRowViewModel? _chatWidthRow;
    private SliderRowViewModel? _chatHeightFocusedRow;
    private SliderRowViewModel? _chatHeightUnfocusedRow;
    private ComboRowViewModel? _chatVisibilityRow;
    private ToggleRowViewModel? _chatColorsRow;
    private ToggleRowViewModel? _chatLinksRow;
    private ComboRowViewModel? _narratorRow;
    private ToggleRowViewModel? _reducedMotionRow;
    private ToggleRowViewModel? _forceUnicodeFontRow;
    private ToggleRowViewModel? _hideSplashTextsRow;
    private ComboRowViewModel? _languageRow;
    private ToggleRowViewModel? _closeAfterLaunchRow;
    private ToggleRowViewModel? _showOutputRow;
    private ToggleRowViewModel? _animationsRow;

    public SettingsPageViewModel(LauncherServices services, Action<string>? toast = null)
    {
        _services = services;
        _toast = toast;
        Groups =
        [
            new("游戏",
            [
                new NavItemViewModel("启动设置", "rocket"),
                new NavItemViewModel("Java 设置", "coffee"),
                new NavItemViewModel("游戏管理", "bookmark"),
                new NavItemViewModel("游戏内设置", "gamepad"),
            ]),
            new("工具",
            [
                new NavItemViewModel("联机与局域网", "network"),
            ]),
            new("启动器",
            [
                new NavItemViewModel("个性化", "palette"),
                new NavItemViewModel("语言", "globe"),
                new NavItemViewModel("其他设置", "monitor"),
            ]),
            new("关于",
            [
                new NavItemViewModel("关于", "info"),
                new NavItemViewModel("检查更新", "refresh"),
                new NavItemViewModel("反馈", "message"),
                new NavItemViewModel("日志", "scroll-text"),
            ]),
        ];

        SelectedSection = Groups[0].Items[0];
    }

    public IReadOnlyList<NavGroupViewModel> Groups { get; }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _isReady;

    [ObservableProperty]
    private NavItemViewModel? _selectedSection;

    [ObservableProperty]
    private SettingsSectionViewModel? _currentSection;

    public Task EnsureLoadedAsync()
    {
        lock (_loadLock)
        {
            return _loadTask ??= LoadAsyncCore();
        }
    }

    /// <summary>重新检测本机 Java 并更新结果行（对应 PCL-CE Java 管理的刷新按钮）。</summary>
    private async Task RefreshJavaListAsync()
    {
        if (_javaDetectedRow is null)
        {
            return;
        }

        _javaDetectedRow.Value = "正在检测…";
        var installations = await _services.JavaDetector.DetectAsync(_services.Config.JavaPath).ConfigureAwait(true);
        _javaDetectedRow.Value = installations.Count == 0
            ? "未检测到 Java；请安装 Java 8 及以上或在上方手动指定路径。"
            : string.Join(Environment.NewLine, installations.Take(6)
                .Select(java => $"Java {java.MajorVersion}（{java.Architecture}）· {java.ExecutablePath}"));
        _toast?.Invoke($"已检测到 {installations.Count} 个 Java");
    }

    private async Task LoadAsyncCore()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        IsLoading = true;
        IsReady = false;

        await Task.Yield();
        await Task.Run(BuildSections);
        WirePersistence();
        CurrentSection = SelectedSection is null ? null : _sections.GetValueOrDefault(SelectedSection.Title);

        var remaining = MinimumLoadingMilliseconds - (int)watch.ElapsedMilliseconds;
        if (remaining > 0)
        {
            await Task.Delay(remaining);
        }

        IsLoading = false;
        IsReady = true;
        PerfTrace.Mark("settings_loaded");
        // 设置页就绪后异步刷新 Java 检测结果（对应 PCL-CE Java 管理的自动扫描）
        _ = RefreshJavaListAsync();
    }

    partial void OnSelectedSectionChanged(NavItemViewModel? value)
    {
        foreach (var item in Groups.SelectMany(group => group.Items))
        {
            item.IsActive = ReferenceEquals(item, value);
        }

        CurrentSection = IsReady && value is not null ? _sections.GetValueOrDefault(value.Title) : null;
    }

    private void BuildSections()
    {
        var config = _services.Config;

        _sections["启动设置"] = new SettingsSectionViewModel("启动设置",
        [
            new SettingCardViewModel("游戏选项",
            [
                _memoryRow = new ComboRowViewModel(
                    "内存分配",
                    AddCustom(MemoryOptions, config.MemoryMegabytes == 0 ? "自动分配" : $"{config.MemoryMegabytes} MB"),
                    config.MemoryMegabytes == 0 ? "自动分配" : $"{config.MemoryMegabytes} MB"),
                _initialMemoryRow = new ComboRowViewModel(
                    "初始内存（-Xms）",
                    InitialMemoryOptions,
                    config.InitialMemoryMegabytes == 0 ? "自动" : $"{config.InitialMemoryMegabytes} MB"),
                _lockMemoryRow = new ToggleRowViewModel("锁定内存分配（-Xms = -Xmx）", config.LockMemory),
                _windowRow = new ComboRowViewModel(
                    "窗口大小",
                    WindowOptions,
                    WindowOptions[Math.Clamp(config.WindowType, 0, WindowOptions.Length - 1)]),
                _launcherVisibilityRow = new ComboRowViewModel(
                    "启动器可见性",
                    LauncherVisibilityOptions,
                    LauncherVisibilityOptions[Math.Clamp(Array.IndexOf(LauncherVisibilityValues, config.LauncherVisibility), 0, LauncherVisibilityOptions.Length - 1)]),
                _processPriorityRow = new ComboRowViewModel(
                    "进程优先级",
                    ProcessPriorityOptions,
                    ProcessPriorityOptions[Math.Clamp(Array.IndexOf(ProcessPriorityValues, config.ProcessPriority), 0, ProcessPriorityOptions.Length - 1)]),
                _ipStackRow = new ComboRowViewModel(
                    "IP 协议偏好",
                    IpStackOptions,
                    IpStackOptions[Math.Clamp(config.PreferredIpStack, 0, 2)]),
                _gameDirectoryRow = new TextRowViewModel(
                    "游戏目录",
                    Core.Platform.PlatformInfo.DefaultMinecraftDirectory,
                    config.GameDirectory),
                new TextLineRowViewModel("留空游戏目录时使用平台默认的 .minecraft 目录。")
            ]),
            new SettingCardViewModel("游戏窗口与信息",
            [
                _windowTitleRow = new TextRowViewModel(
                    "游戏窗口标题",
                    "空 = 不修改；支持 {name} {user} {version} 占位符",
                    config.WindowTitle),
                _typeInfoRow = new TextRowViewModel(
                    "自定义信息",
                    "显示在游戏主界面左下角与 F3",
                    config.TypeInfo),
                new TextLineRowViewModel("窗口标题在游戏窗口创建后自动注入（仅 Windows）。")
            ]),
            new SettingCardViewModel("高级启动选项",
            [
                _jvmArgsRow = new TextRowViewModel(
                    "JVM 参数（额外）",
                    "例如 -XX:+UseG1GC",
                    config.ExtraJvmArguments),
                _gameArgsRow = new TextRowViewModel(
                    "游戏参数（额外）",
                    "追加到游戏参数尾部",
                    config.ExtraGameArguments),
                _preLaunchRow = new TextRowViewModel(
                    "启动前执行命令",
                    "支持 {path} {user} {name} {version} 占位符",
                    config.PreLaunchCommand),
                _preLaunchWaitRow = new ToggleRowViewModel("等待命令执行完成后再继续启动", config.PreLaunchCommandWait),
                new TextLineRowViewModel("启动前命令在游戏进程创建前运行；未勾选等待时命令与游戏并行执行。")
            ]),
        ]);

        _sections["Java 设置"] = new SettingsSectionViewModel("Java 设置",
        [
            new SettingCardViewModel("Java 选择",
            [
                _autoJavaRow = new ToggleRowViewModel("自动选择 Java", string.IsNullOrWhiteSpace(config.JavaPath)),
                _javaPathRow = new TextRowViewModel(
                    "手动指定 Java",
                    "例如 C:\\Program Files\\Java\\jdk-21\\bin\\javaw.exe",
                    config.JavaPath),
                new TextLineRowViewModel("开启自动选择时忽略手动路径，由启动服务按版本要求挑选 Java；手动指定后该 Java 优先。")
            ]),
            new SettingCardViewModel("已检测的 Java",
            [
                _javaDetectedRow = new MutableInfoRowViewModel("检测结果", "正在检测…"),
                new ButtonsRowViewModel(
                [
                    new SettingButtonViewModel("重新检测 Java", "refresh", true, () => _ = RefreshJavaListAsync()),
                    new SettingButtonViewModel("打开 Java 目录", "package", false, () => OpenFolder(
                        Path.GetDirectoryName(config.JavaPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))),
                ]),
                new TextLineRowViewModel("自动选择依据游戏版本要求挑选最合适的 Java；检测结果每次启动时也会刷新。")
            ]),
        ]);

        _sections["游戏管理"] = new SettingsSectionViewModel("游戏管理",
        [
            new SettingCardViewModel("下载源",
            [
                _versionListSourceRow = new ComboRowViewModel(
                    "版本列表源",
                    DownloadSourceOptions,
                    DownloadSourceOptions[Math.Clamp(config.VersionListSource, 0, 2)]),
                _fileSourceRow = new ComboRowViewModel(
                    "下载文件源",
                    DownloadSourceOptions,
                    DownloadSourceOptions[Math.Clamp(config.FileSource, 0, 2)]),
                _communitySourceRow = new ComboRowViewModel(
                    "社区资源源",
                    CommunitySourceOptions,
                    CommunitySourceOptions[Math.Clamp(config.CommunitySourceSolution, 0, 2)]),
                _autoDependenciesRow = new ToggleRowViewModel(
                    "自动安装前置",
                    config.CompAutoInstallDependencies),
                _ignoreQuiltRow = new ToggleRowViewModel(
                    "忽略 Quilt",
                    config.CompIgnoreQuilt),
                _quickDownloadRow = new ComboRowViewModel(
                    "快速下载行为",
                    QuickDownloadOptions,
                    QuickDownloadOptions[Math.Clamp(config.QuickDownloadBehavior, 0, 3)]),
                _threadsRow = new ComboRowViewModel(
                    "下载线程数",
                    AddCustom(ThreadOptions, config.DownloadThreads.ToString()),
                    config.DownloadThreads.ToString()),
                _retryRow = new ComboRowViewModel(
                    "下载重试次数",
                    AddCustom(RetryOptions, config.RetryCount.ToString()),
                    config.RetryCount.ToString()),
                _fixAuthlibRow = new ToggleRowViewModel(
                    "升级部分版本的 Authlib",
                    config.FixAuthlib),
                _autoSelectInstanceRow = new ToggleRowViewModel(
                    "安装新实例后自动选定该实例",
                    config.AutoSelectInstance),
                new TextLineRowViewModel("镜像源（BMCLAPI / mcimirror）适合国内网络；社区资源在 CurseForge API Key 未配置时依赖镜像源兜底。")
            ]),
        ]);

        var game = config.GameSettings;
        _sections["游戏内设置"] = new SettingsSectionViewModel("游戏内设置",
        [
            new SettingCardViewModel("总开关",
            [
                _gameSettingsEnabledRow = new ToggleRowViewModel("启用游戏内设置", game.Enabled),
                new TextLineRowViewModel("启用后只在实例首次启动且选择「跟随全局」时写入一次 options.txt；禁用则完全使用游戏默认设置。")
            ]),
            new SettingCardViewModel("视频设置",
            [
                _renderDistanceRow = new SliderRowViewModel("视距", 2, 32, 1, game.RenderDistance, " 区块"),
                _simulationDistanceRow = new SliderRowViewModel("模拟距离", 5, 32, 1, game.SimulationDistance, " 区块"),
                _fovRow = new SliderRowViewModel("视角（FOV）", 30, 110, 1, game.Fov, "°"),
                _maxFpsRow = new ComboRowViewModel(
                    "最大帧率", MaxFpsOptions,
                    game.MaxFps >= 260 ? "不限" : Math.Clamp(game.MaxFps, 30, 260).ToString()),
                _vsyncRow = new ToggleRowViewModel("垂直同步", game.VSync),
                _graphicsRow = new ComboRowViewModel("图形质量", GraphicsOptions, GraphicsLabel(game.Graphics)),
                _particlesRow = new ComboRowViewModel("粒子效果", ParticleOptions, ParticleLabel(game.Particles)),
                _cloudsRow = new ToggleRowViewModel("云", game.Clouds),
                _smoothLightingRow = new ToggleRowViewModel("平滑光照", game.SmoothLighting),
                _entityShadowsRow = new ToggleRowViewModel("实体阴影", game.EntityShadows),
                _guiScaleRow = new ComboRowViewModel(
                    "界面尺寸", GuiScaleOptions, GuiScaleOptions[Math.Clamp(game.GuiScale, 0, 4)]),
                _gammaRow = new SliderRowViewModel("亮度", 0, 1, 0.01, game.Gamma, asPercent: true),
                _fovEffectRow = new SliderRowViewModel("视角摇晃", 0, 1, 0.01, game.FovEffectScale, asPercent: true),
                _screenEffectRow = new SliderRowViewModel("屏幕扭曲", 0, 1, 0.01, game.ScreenEffectScale, asPercent: true),
                new TextLineRowViewModel("模拟距离需要 1.18 及以上版本；旧版本会自动忽略该项。")
            ],
            collapsible: true,
            expanded: false),
            new SettingCardViewModel("控制设置",
            [
                _mouseSensitivityRow = new SliderRowViewModel("鼠标灵敏度", 0, 1, 0.01, game.MouseSensitivity, asPercent: true),
                _invertMouseRow = new ToggleRowViewModel("反转鼠标", game.InvertMouse),
                _autoJumpRow = new ToggleRowViewModel("自动跳跃", game.AutoJump),
                _mainHandRow = new ComboRowViewModel("主手", MainHandOptions, game.MainHand == "left" ? "左手" : "右手"),
                new TextLineRowViewModel("这些值只在首次启动写入一次，之后以你在游戏内修改的设置为准。")
            ],
            collapsible: true,
            expanded: false),
            new SettingCardViewModel("音乐和声音",
            [
                _masterVolumeRow = new SliderRowViewModel("主音量", 0, 1, 0.01, game.MasterVolume, asPercent: true),
                _musicVolumeRow = new SliderRowViewModel("音乐", 0, 1, 0.01, game.MusicVolume, asPercent: true),
                _recordVolumeRow = new SliderRowViewModel("音符盒 / 唱片机", 0, 1, 0.01, game.RecordVolume, asPercent: true),
                _weatherVolumeRow = new SliderRowViewModel("天气", 0, 1, 0.01, game.WeatherVolume, asPercent: true),
                _blockVolumeRow = new SliderRowViewModel("方块", 0, 1, 0.01, game.BlockVolume, asPercent: true),
                _hostileVolumeRow = new SliderRowViewModel("敌对生物", 0, 1, 0.01, game.HostileVolume, asPercent: true),
                _neutralVolumeRow = new SliderRowViewModel("中立生物", 0, 1, 0.01, game.NeutralVolume, asPercent: true),
                _playerVolumeRow = new SliderRowViewModel("玩家", 0, 1, 0.01, game.PlayerVolume, asPercent: true),
                _ambientVolumeRow = new SliderRowViewModel("环境", 0, 1, 0.01, game.AmbientVolume, asPercent: true),
                _voiceVolumeRow = new SliderRowViewModel("语音", 0, 1, 0.01, game.VoiceVolume, asPercent: true),
            ],
            collapsible: true,
            expanded: false),
            new SettingCardViewModel("聊天设置",
            [
                _chatOpacityRow = new SliderRowViewModel("聊天不透明度", 0, 1, 0.01, game.ChatOpacity, asPercent: true),
                _chatScaleRow = new SliderRowViewModel("聊天缩放", 0.5, 1, 0.01, game.ChatScale, asPercent: true),
                _chatWidthRow = new SliderRowViewModel("聊天宽度", 0.5, 1, 0.01, game.ChatWidth, asPercent: true),
                _chatHeightFocusedRow = new SliderRowViewModel("聊天高度（展开）", 0, 1, 0.01, game.ChatHeightFocused, asPercent: true),
                _chatHeightUnfocusedRow = new SliderRowViewModel("聊天高度（收起）", 0, 1, 0.01, game.ChatHeightUnfocused, asPercent: true),
                _chatVisibilityRow = new ComboRowViewModel(
                    "聊天显示", ChatVisibilityOptions, ChatVisibilityOptions[Math.Clamp(game.ChatVisibility, 0, 2)]),
                _chatColorsRow = new ToggleRowViewModel("聊天颜色", game.ChatColors),
                _chatLinksRow = new ToggleRowViewModel("聊天链接", game.ChatLinks),
            ],
            collapsible: true,
            expanded: false),
            new SettingCardViewModel("辅助功能",
            [
                _narratorRow = new ComboRowViewModel(
                    "字幕", NarratorOptions, NarratorOptions[Math.Clamp(game.Narrator, 0, 3)]),
                _reducedMotionRow = new ToggleRowViewModel("减少动态效果", game.ReducedMotion),
                _forceUnicodeFontRow = new ToggleRowViewModel("强制使用 Unicode 字体", game.ForceUnicodeFont),
                _hideSplashTextsRow = new ToggleRowViewModel("隐藏闪烁标语", game.HideSplashTexts),
            ],
            collapsible: true,
            expanded: false),
            new SettingCardViewModel("语言",
            [
                _languageRow = new ComboRowViewModel("游戏语言", GameLanguageLabels, LanguageLabel(game.Language)),
                new TextLineRowViewModel("世界难度、皮肤、资源包与按键绑定保存在世界或游戏内，不属于 options.txt，因此这里不提供修改。")
            ],
            collapsible: true,
            expanded: false),
        ]);

        _sections["联机与局域网"] = new SettingsSectionViewModel("联机与局域网",
        [
            new SettingCardViewModel("局域网",
            [
                new TextLineRowViewModel("联机与局域网穿透功能尚未迁移到当前 Core，暂不可用。")
            ]),
        ]);

        _sections["个性化"] = new SettingsSectionViewModel("个性化",
        [
            new SettingCardViewModel("外观",
            [
                _themeRow = new ComboRowViewModel(
                    "界面主题",
                    ["浅色", "深色"],
                    config.Theme == "dark" ? "深色" : "浅色"),
                _animationsRow = new ToggleRowViewModel("启动动画", config.EnableAnimations),
                new TextLineRowViewModel("主题设置会写入配置；重新启动启动器后生效。")
            ]),
        ]);

        _sections["语言"] = new SettingsSectionViewModel("语言",
        [
            new SettingCardViewModel("界面语言",
            [
                new InfoRowViewModel("当前语言", "简体中文"),
                new TextLineRowViewModel("多语言切换尚未迁移到当前 Core。")
            ]),
        ]);

        _sections["其他设置"] = new SettingsSectionViewModel("其他设置",
        [
            new SettingCardViewModel("杂项",
            [
                _closeAfterLaunchRow = new ToggleRowViewModel("启动后关闭启动器", config.CloseLauncherAfterLaunch),
                _showOutputRow = new ToggleRowViewModel("显示游戏输出", config.ShowGameOutput),
                new TextLineRowViewModel("启动器进程退出后，游戏仍会继续运行。")
            ]),
        ]);

        _sections["关于"] = new SettingsSectionViewModel("关于",
        [
            new SettingCardViewModel("Hyplay Craft Launcher",
            [
                new InfoRowViewModel("当前版本", LaunchService.LauncherVersion),
                new InfoRowViewModel("界面来源", "PCL.Neo-main（表现层迁移）"),
                new InfoRowViewModel("业务逻辑", "对齐 PCL-CE"),
                new InfoRowViewModel("开源协议", "Apache-2.0"),
                new TextLineRowViewModel("界面版权归 PCL.Neo-main 原作者所有；业务实现参考 PCL-CE。")
            ]),
            new SettingCardViewModel("获取帮助与源代码",
            [
                new ButtonsRowViewModel(
                [
                    new SettingButtonViewModel(
                        "PCL-CE 源代码",
                        "package",
                        true,
                        () => OpenUrl(AboutLinks.SourceCode)),
                    new SettingButtonViewModel(
                        "PCL-CE 主页",
                        "network",
                        false,
                        () => OpenUrl(AboutLinks.HomePage)),
                    new SettingButtonViewModel(
                        "赞助原作者",
                        "bookmark",
                        false,
                        () => OpenUrl(AboutLinks.Sponsor)),
                ]),
                new TextLineRowViewModel("HYCL 的业务实现参考 PCL-CE；遇到问题请先检查日志目录中的 launcher.log。")
            ]),
            new SettingCardViewModel("数据目录",
            [
                new InfoRowViewModel("数据目录", _services.Paths.DataDirectory),
                new ButtonsRowViewModel(
                [
                    new SettingButtonViewModel(
                        "打开数据目录",
                        "package",
                        true,
                        () => OpenFolder(_services.Paths.DataDirectory)),
                    new SettingButtonViewModel(
                        "打开日志目录",
                        "scroll-text",
                        false,
                        () => OpenFolder(Path.GetDirectoryName(_services.Logger.LogFilePath) ?? _services.Paths.DataDirectory)),
                ])
            ]),
        ]);

        _sections["检查更新"] = new SettingsSectionViewModel("检查更新",
        [
            new SettingCardViewModel("软件更新",
            [
                new InfoRowViewModel("当前版本", LaunchService.LauncherVersion),
                _updateChannelRow = new ComboRowViewModel(
                    "更新通道",
                    UpdateChannelOptions,
                    UpdateChannelOptions[Math.Clamp(config.UpdateChannel, 0, UpdateChannelOptions.Length - 1)]),
                new ButtonsRowViewModel(
                [
                    new SettingButtonViewModel(
                        "检查更新",
                        "refresh",
                        true,
                        () => _ = CheckForUpdateAsync()),
                ]),
                _updateStatusRow = new MutableInfoRowViewModel("检查结果", "尚未检查。"),
                new TextLineRowViewModel("连接 GitHub Releases 检查最新版本；下载安装请前往仓库 Releases 页面。")
            ]),
        ]);

        _sections["反馈"] = new SettingsSectionViewModel("反馈",
        [
            new SettingCardViewModel("提交反馈",
            [
                new TextLineRowViewModel("遇到问题或有建议时，请附带日志目录中的 launcher.log 提交反馈。"),
                new ButtonsRowViewModel(
                [
                    new SettingButtonViewModel(
                        "打开日志目录",
                        "scroll-text",
                        true,
                        () => OpenFolder(Path.GetDirectoryName(_services.Logger.LogFilePath) ?? _services.Paths.DataDirectory)),
                    new SettingButtonViewModel(
                        "前往反馈",
                        "message",
                        false,
                        () => OpenUrlOrHint(AboutLinks.Feedback, "未配置反馈地址：请在 AboutLinks.Feedback 填入仓库 issues 地址")),
                ]),
            ]),
        ]);

        _sections["日志"] = new SettingsSectionViewModel("日志",
        [
            new SettingCardViewModel("运行日志",
            [
                new ButtonsRowViewModel(
                [
                    new SettingButtonViewModel(
                        "打开日志目录",
                        "package",
                        true,
                        () => OpenFolder(Path.GetDirectoryName(_services.Logger.LogFilePath) ?? _services.Paths.DataDirectory)),
                    new SettingButtonViewModel(
                        "清理历史日志",
                        "close",
                        false,
                        () => CleanOldLogs()),
                ]),
                _logTailRow = new MutableLogRowViewModel(ReadLogTail()),
            ]),
        ]);
    }

    /// <summary>关于页/反馈页的外部链接（Hycl 自身仓库；Sponsor 指向上游 PCL-CE 以致谢）。</summary>
    public static class AboutLinks
    {
        public const string SourceCode = "https://github.com/muqiu-pika/Hycl";
        public const string HomePage = "https://github.com/muqiu-pika/Hycl";
        public const string Sponsor = "https://ifdian.net/a/LTCat";
        public const string Feedback = "https://github.com/muqiu-pika/Hycl/issues";
    }

    /// <summary>更新检查框架（对应 PCL-CE UpdateManager；读取 GitHub Releases 最新版本号）。</summary>
    public static class UpdateChecker
    {
        /// <summary>仓库 releases API 地址；留空表示未配置。</summary>
        public const string ReleasesUrl = "https://api.github.com/repos/muqiu-pika/Hycl/releases/latest";

        /// <summary>检查更新：有新版本时返回版本号，已是最新返回 null，未配置/失败抛异常。</summary>
        public static async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ReleasesUrl))
            {
                throw new InvalidOperationException("未配置更新源：请在 UpdateChecker.ReleasesUrl 填入仓库 releases 地址");
            }

            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HYCL/1.0");
            var json = await client.GetStringAsync(ReleasesUrl, cancellationToken).ConfigureAwait(false);
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            // tag 可能带 v 前缀（v1.2.3），统一去掉后与 LauncherVersion 比较
            var tagName = (node?["tag_name"]?.ToString() ?? "").TrimStart('v', 'V');
            return tagName.Equals(LaunchService.LauncherVersion, StringComparison.OrdinalIgnoreCase) ? null : tagName;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        if (_updateStatusRow is null)
        {
            return;
        }

        _updateStatusRow.Value = "正在检查…";
        try
        {
            var newVersion = await UpdateChecker.CheckAsync().ConfigureAwait(true);
            _updateStatusRow.Value = newVersion is null
                ? $"已是最新版本（{LaunchService.LauncherVersion}）"
                : $"发现新版本 {newVersion}，请前往项目主页下载";
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            _updateStatusRow.Value = ex.Message;
        }
    }

    private void CleanOldLogs()
    {
        try
        {
            var logDirectory = Path.GetDirectoryName(_services.Logger.LogFilePath);
            if (string.IsNullOrEmpty(logDirectory) || !Directory.Exists(logDirectory))
            {
                _toast?.Invoke("日志目录不存在");
                return;
            }

            var removed = 0;
            foreach (var file in Directory.EnumerateFiles(logDirectory, "*.log*"))
            {
                if (file.Equals(_services.Logger.LogFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue; // 当前日志保留
                }

                File.Delete(file);
                removed++;
            }

            _toast?.Invoke($"已清理 {removed} 个历史日志");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _toast?.Invoke($"清理日志失败：{ex.Message}");
        }
    }

    private void OpenUrlOrHint(string url, string hint)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _toast?.Invoke(hint);
            return;
        }

        if (_services.System.OpenUrl(url))
        {
            _toast?.Invoke("已在浏览器打开");
        }
    }

    private void OpenUrl(string url)
    {
        if (_services.System.OpenUrl(url))
        {
            _toast?.Invoke("已在浏览器打开");
        }
        else
        {
            _toast?.Invoke($"无法打开链接：{url}");
        }
    }

    private string ReadLogTail()
    {
        try
        {
            var path = _services.Logger.LogFilePath;
            if (!File.Exists(path))
            {
                return "尚无日志文件。";
            }

            var lines = File.ReadAllLines(path);
            return string.Join(Environment.NewLine, lines.TakeLast(20));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"读取日志失败：{ex.Message}";
        }
    }

    private void OpenFolder(string path)
    {
        if (_services.System.OpenFolder(path))
        {
            _toast?.Invoke($"已打开目录：{path}");
        }
        else
        {
            _toast?.Invoke($"无法打开目录：{path}");
        }
    }

    private static IReadOnlyList<string> AddCustom(IReadOnlyList<string> source, string value)
    {
        if (string.IsNullOrWhiteSpace(value) || source.Contains(value))
        {
            return source;
        }

        var list = source.ToList();
        list.Insert(Math.Min(2, list.Count), value);
        return list;
    }

    /// <summary>构建完成后统一挂接保存逻辑，避免构建期间触发写入。</summary>
    private void WirePersistence()
    {
        foreach (var row in _sections.Values.SelectMany(section => section.Cards).SelectMany(card => card.Rows))
        {
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(ComboRowViewModel.SelectedOption)
                    or nameof(TextRowViewModel.Text)
                    or nameof(ToggleRowViewModel.IsOn))
                {
                    QueueSave();
                }
            };
        }
    }

    private void QueueSave()
    {
        var version = ++_saveVersion;
        _ = SaveAfterDelayAsync(version);
    }

    private async Task SaveAfterDelayAsync(int version)
    {
        await Task.Delay(400).ConfigureAwait(true);
        if (version != _saveVersion)
        {
            return;
        }

        await SaveAsync().ConfigureAwait(true);
    }

    private async Task SaveAsync()
    {
        var config = _services.Config;

        if (_memoryRow?.SelectedOption is { } memory)
        {
            config.MemoryMegabytes = memory == "自动分配" ? 0 : ParseNumber(memory);
        }

        if (_initialMemoryRow?.SelectedOption is { } initialMemory)
        {
            config.InitialMemoryMegabytes = initialMemory == "自动" ? 0 : ParseNumber(initialMemory);
        }

        if (_lockMemoryRow is not null)
        {
            config.LockMemory = _lockMemoryRow.IsOn;
        }

        if (_windowRow?.SelectedOption is { } window)
        {
            var index = Array.IndexOf(WindowOptions, window);
            if (index >= 0)
            {
                config.WindowType = index;
            }
        }

        if (_launcherVisibilityRow?.SelectedOption is { } visibility)
        {
            var index = Array.IndexOf(LauncherVisibilityOptions, visibility);
            if (index >= 0)
            {
                config.LauncherVisibility = LauncherVisibilityValues[index];
            }
        }

        if (_processPriorityRow?.SelectedOption is { } priority)
        {
            var index = Array.IndexOf(ProcessPriorityOptions, priority);
            if (index >= 0)
            {
                config.ProcessPriority = ProcessPriorityValues[index];
            }
        }

        if (_ipStackRow?.SelectedOption is { } ipStack)
        {
            var index = Array.IndexOf(IpStackOptions, ipStack);
            if (index >= 0)
            {
                config.PreferredIpStack = index;
            }
        }

        if (_windowTitleRow is not null)
        {
            config.WindowTitle = _windowTitleRow.Text.Trim();
        }

        if (_typeInfoRow is not null)
        {
            config.TypeInfo = _typeInfoRow.Text.Trim();
        }

        if (_jvmArgsRow is not null)
        {
            config.ExtraJvmArguments = _jvmArgsRow.Text.Trim();
        }

        if (_gameArgsRow is not null)
        {
            config.ExtraGameArguments = _gameArgsRow.Text.Trim();
        }

        if (_preLaunchRow is not null)
        {
            config.PreLaunchCommand = _preLaunchRow.Text.Trim();
        }

        if (_preLaunchWaitRow is not null)
        {
            config.PreLaunchCommandWait = _preLaunchWaitRow.IsOn;
        }

        if (_gameDirectoryRow is not null)
        {
            config.GameDirectory = _gameDirectoryRow.Text.Trim();
        }

        if (_autoJavaRow is not null && _autoJavaRow.IsOn)
        {
            config.JavaPath = "";
        }
        else if (_javaPathRow is not null)
        {
            config.JavaPath = _javaPathRow.Text.Trim();
        }

        if (_versionListSourceRow?.SelectedOption is { } versionListSource)
        {
            var index = Array.IndexOf(DownloadSourceOptions, versionListSource);
            if (index >= 0)
            {
                config.VersionListSource = index;
            }
        }

        if (_fileSourceRow?.SelectedOption is { } fileSource)
        {
            var index = Array.IndexOf(DownloadSourceOptions, fileSource);
            if (index >= 0)
            {
                config.FileSource = index;
            }
        }

        if (_communitySourceRow?.SelectedOption is { } communitySource)
        {
            var index = Array.IndexOf(CommunitySourceOptions, communitySource);
            if (index >= 0)
            {
                config.CommunitySourceSolution = index;
            }
        }

        if (_autoDependenciesRow is not null)
        {
            config.CompAutoInstallDependencies = _autoDependenciesRow.IsOn;
        }

        if (_ignoreQuiltRow is not null)
        {
            config.CompIgnoreQuilt = _ignoreQuiltRow.IsOn;
        }

        if (_quickDownloadRow?.SelectedOption is { } quickDownload)
        {
            var index = Array.IndexOf(QuickDownloadOptions, quickDownload);
            if (index >= 0)
            {
                config.QuickDownloadBehavior = index;
            }
        }

        if (_fixAuthlibRow is not null)
        {
            config.FixAuthlib = _fixAuthlibRow.IsOn;
            _services.Installer.FixAuthlibEnabled = config.FixAuthlib;
        }

        if (_autoSelectInstanceRow is not null)
        {
            config.AutoSelectInstance = _autoSelectInstanceRow.IsOn;
        }

        if (_updateChannelRow?.SelectedOption is { } channel)
        {
            var index = Array.IndexOf(UpdateChannelOptions, channel);
            if (index >= 0)
            {
                config.UpdateChannel = index;
            }
        }

        _logTailRow?.Reload(ReadLogTail);

        if (_threadsRow?.SelectedOption is { } threads)
        {
            config.DownloadThreads = ParseNumber(threads);
        }

        if (_retryRow?.SelectedOption is { } retry)
        {
            config.RetryCount = ParseNumber(retry);
        }

        if (_themeRow?.SelectedOption is { } theme)
        {
            var newTheme = theme == "深色" ? "dark" : "light";
            if (config.Theme != newTheme)
            {
                config.Theme = newTheme;
                ThemeChanged?.Invoke(this, newTheme);
            }
        }

        if (_closeAfterLaunchRow is not null)
        {
            config.CloseLauncherAfterLaunch = _closeAfterLaunchRow.IsOn;
        }

        if (_showOutputRow is not null)
        {
            config.ShowGameOutput = _showOutputRow.IsOn;
        }

        if (_animationsRow is not null)
        {
            config.EnableAnimations = _animationsRow.IsOn;
        }

        // 游戏内设置：首启动写入 options.txt 的取值
        var gameSettings = config.GameSettings;
        if (_gameSettingsEnabledRow is not null)
        {
            gameSettings.Enabled = _gameSettingsEnabledRow.IsOn;
        }

        if (_renderDistanceRow is not null)
        {
            gameSettings.RenderDistance = (int)Math.Round(_renderDistanceRow.Value);
        }

        if (_simulationDistanceRow is not null)
        {
            gameSettings.SimulationDistance = (int)Math.Round(_simulationDistanceRow.Value);
        }

        if (_fovRow is not null)
        {
            gameSettings.Fov = (int)Math.Round(_fovRow.Value);
        }

        if (_guiScaleRow?.SelectedOption is { } guiScale)
        {
            gameSettings.GuiScale = ParseNumber(guiScale);
        }

        if (_maxFpsRow?.SelectedOption is { } maxFps)
        {
            gameSettings.MaxFps = maxFps == "不限" ? 260 : ParseNumber(maxFps);
        }

        if (_vsyncRow is not null)
        {
            gameSettings.VSync = _vsyncRow.IsOn;
        }

        if (_graphicsRow?.SelectedOption is { } graphics)
        {
            gameSettings.Graphics = graphics switch
            {
                "流畅" => "fast",
                "绚丽" => "fabulous",
                _ => "fancy"
            };
        }

        if (_particlesRow?.SelectedOption is { } particles)
        {
            gameSettings.Particles = particles == "全部" ? "all" : particles == "减少" ? "decreased" : "minimal";
        }

        if (_cloudsRow is not null)
        {
            gameSettings.Clouds = _cloudsRow.IsOn;
        }

        if (_smoothLightingRow is not null)
        {
            gameSettings.SmoothLighting = _smoothLightingRow.IsOn;
        }

        if (_entityShadowsRow is not null)
        {
            gameSettings.EntityShadows = _entityShadowsRow.IsOn;
        }

        if (_gammaRow is not null)
        {
            gameSettings.Gamma = _gammaRow.Value;
        }

        if (_fovEffectRow is not null)
        {
            gameSettings.FovEffectScale = _fovEffectRow.Value;
        }

        if (_screenEffectRow is not null)
        {
            gameSettings.ScreenEffectScale = _screenEffectRow.Value;
        }

        if (_mouseSensitivityRow is not null)
        {
            gameSettings.MouseSensitivity = _mouseSensitivityRow.Value;
        }

        if (_invertMouseRow is not null)
        {
            gameSettings.InvertMouse = _invertMouseRow.IsOn;
        }

        if (_autoJumpRow is not null)
        {
            gameSettings.AutoJump = _autoJumpRow.IsOn;
        }

        if (_mainHandRow?.SelectedOption is { } mainHand)
        {
            gameSettings.MainHand = mainHand == "左手" ? "left" : "right";
        }

        if (_masterVolumeRow is not null)
        {
            gameSettings.MasterVolume = _masterVolumeRow.Value;
        }

        if (_musicVolumeRow is not null)
        {
            gameSettings.MusicVolume = _musicVolumeRow.Value;
        }

        if (_recordVolumeRow is not null)
        {
            gameSettings.RecordVolume = _recordVolumeRow.Value;
        }

        if (_weatherVolumeRow is not null)
        {
            gameSettings.WeatherVolume = _weatherVolumeRow.Value;
        }

        if (_blockVolumeRow is not null)
        {
            gameSettings.BlockVolume = _blockVolumeRow.Value;
        }

        if (_hostileVolumeRow is not null)
        {
            gameSettings.HostileVolume = _hostileVolumeRow.Value;
        }

        if (_neutralVolumeRow is not null)
        {
            gameSettings.NeutralVolume = _neutralVolumeRow.Value;
        }

        if (_playerVolumeRow is not null)
        {
            gameSettings.PlayerVolume = _playerVolumeRow.Value;
        }

        if (_ambientVolumeRow is not null)
        {
            gameSettings.AmbientVolume = _ambientVolumeRow.Value;
        }

        if (_voiceVolumeRow is not null)
        {
            gameSettings.VoiceVolume = _voiceVolumeRow.Value;
        }

        if (_chatOpacityRow is not null)
        {
            gameSettings.ChatOpacity = _chatOpacityRow.Value;
        }

        if (_chatScaleRow is not null)
        {
            gameSettings.ChatScale = _chatScaleRow.Value;
        }

        if (_chatWidthRow is not null)
        {
            gameSettings.ChatWidth = _chatWidthRow.Value;
        }

        if (_chatHeightFocusedRow is not null)
        {
            gameSettings.ChatHeightFocused = _chatHeightFocusedRow.Value;
        }

        if (_chatHeightUnfocusedRow is not null)
        {
            gameSettings.ChatHeightUnfocused = _chatHeightUnfocusedRow.Value;
        }

        if (_chatVisibilityRow?.SelectedOption is { } chatVisibility)
        {
            gameSettings.ChatVisibility = chatVisibility switch
            {
                "仅命令" => 1,
                "隐藏" => 2,
                _ => 0
            };
        }

        if (_chatColorsRow is not null)
        {
            gameSettings.ChatColors = _chatColorsRow.IsOn;
        }

        if (_chatLinksRow is not null)
        {
            gameSettings.ChatLinks = _chatLinksRow.IsOn;
        }

        if (_narratorRow?.SelectedOption is { } narrator)
        {
            gameSettings.Narrator = narrator switch
            {
                "全部" => 1,
                "仅聊天" => 2,
                "仅系统" => 3,
                _ => 0
            };
        }

        if (_reducedMotionRow is not null)
        {
            gameSettings.ReducedMotion = _reducedMotionRow.IsOn;
        }

        if (_forceUnicodeFontRow is not null)
        {
            gameSettings.ForceUnicodeFont = _forceUnicodeFontRow.IsOn;
        }

        if (_hideSplashTextsRow is not null)
        {
            gameSettings.HideSplashTexts = _hideSplashTextsRow.IsOn;
        }

        if (_languageRow?.SelectedOption is { } language)
        {
            gameSettings.Language = LanguageCode(language);
        }

        gameSettings.Normalize();

        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
    }

    private static int ParseNumber(string text)
    {
        var digits = new string(text.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    [RelayCommand]
    private void SelectSection(NavItemViewModel? item) => SelectedSection = item;
}
