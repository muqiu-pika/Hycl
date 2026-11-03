using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Models;

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

    /// <summary>下载源三档（对应 PCL-CE 的下载源设置文案）。</summary>
    public static readonly string[] DownloadSourceOptions =
    [
        "尽量使用镜像源",
        "优先使用官方源，在加载缓慢时换用镜像源",
        "尽量使用官方源"
    ];

    private ComboRowViewModel? _memoryRow;
    private ComboRowViewModel? _windowRow;
    private TextRowViewModel? _gameDirectoryRow;
    private ToggleRowViewModel? _autoJavaRow;
    private TextRowViewModel? _javaPathRow;
    private ComboRowViewModel? _versionListSourceRow;
    private ComboRowViewModel? _fileSourceRow;
    private ComboRowViewModel? _threadsRow;
    private ComboRowViewModel? _retryRow;
    private ComboRowViewModel? _themeRow;
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
                _windowRow = new ComboRowViewModel(
                    "窗口大小",
                    WindowOptions,
                    WindowOptions[Math.Clamp(config.WindowType, 0, WindowOptions.Length - 1)]),
                _gameDirectoryRow = new TextRowViewModel(
                    "游戏目录",
                    Core.Platform.PlatformInfo.DefaultMinecraftDirectory,
                    config.GameDirectory),
                new TextLineRowViewModel("留空游戏目录时使用平台默认的 .minecraft 目录。")
            ]),
        ]);

        _sections["Java 设置"] = new SettingsSectionViewModel("Java 设置",
        [
            new SettingCardViewModel("Java 运行环境",
            [
                _autoJavaRow = new ToggleRowViewModel("自动选择 Java", string.IsNullOrWhiteSpace(config.JavaPath)),
                _javaPathRow = new TextRowViewModel(
                    "手动指定 Java",
                    "例如 C:\\Program Files\\Java\\jdk-21\\bin\\javaw.exe",
                    config.JavaPath),
                new TextLineRowViewModel("开启自动选择时忽略手动路径，由启动服务按版本要求挑选 Java。")
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
                _threadsRow = new ComboRowViewModel(
                    "下载线程数",
                    AddCustom(ThreadOptions, config.DownloadThreads.ToString()),
                    config.DownloadThreads.ToString()),
                _retryRow = new ComboRowViewModel(
                    "下载重试次数",
                    AddCustom(RetryOptions, config.RetryCount.ToString()),
                    config.RetryCount.ToString()),
                new TextLineRowViewModel("镜像源（BMCLAPI）适合国内网络；官方优先档会在官方源响应缓慢时自动改用镜像源。")
            ]),
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
            new SettingCardViewModel("更新",
            [
                new InfoRowViewModel("当前版本", LaunchService.LauncherVersion),
                new TextLineRowViewModel("当前为本地构建版本，更新通道尚未接入。")
            ]),
        ]);

        _sections["反馈"] = new SettingsSectionViewModel("反馈",
        [
            new SettingCardViewModel("提交反馈",
            [
                new TextLineRowViewModel("遇到问题或有建议时，请附带日志目录中的 launcher.log 提交反馈。")
            ]),
        ]);

        _sections["日志"] = new SettingsSectionViewModel("日志",
        [
            new SettingCardViewModel("运行日志",
            [
                new LogRowViewModel(ReadLogTail())
            ]),
        ]);
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

        if (_windowRow?.SelectedOption is { } window)
        {
            var index = Array.IndexOf(WindowOptions, window);
            if (index >= 0)
            {
                config.WindowType = index;
            }
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
            config.Theme = theme == "深色" ? "dark" : "light";
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
