using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Config;
using Hycl.Launcher.Core.Java;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>设置页：启动参数、Java、下载源与启动器行为。</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly LauncherServices _services;
    private bool _javaLoaded;
    private bool _initializing = true;

    public SettingsViewModel(LauncherServices services)
    {
        _services = services;
        Categories = new ObservableCollection<SidebarItem>
        {
            new("launch", "启动设置", "内存、窗口与参数"),
            new("java", "Java", "运行时检测与路径"),
            new("download", "下载", "下载源与并发"),
            new("launcher", "启动器", "行为与日志")
        };
        SelectedCategoryItem = Categories[0];

        JavaChoices = new ObservableCollection<JavaChoice> { JavaChoice.Auto };

        var config = services.Config;
        GameDirectory = config.GameDirectory;
        AutoMemory = config.MemoryMegabytes == 0;
        MemoryMegabytes = config.MemoryMegabytes > 0 ? config.MemoryMegabytes : ComputeAutoMemory();
        WindowTypeIndex = config.WindowType;
        WindowWidth = config.WindowWidth;
        WindowHeight = config.WindowHeight;
        ExtraJvmArguments = config.ExtraJvmArguments;
        ExtraGameArguments = config.ExtraGameArguments;
        DownloadSourceIndex = config.DownloadSource == DownloadSourceKind.Official ? 0 : 1;
        DownloadThreads = config.DownloadThreads;
        RetryCount = config.RetryCount;
        CloseLauncherAfterLaunch = config.CloseLauncherAfterLaunch;
        ShowGameOutput = config.ShowGameOutput;
        EnableAnimations = config.EnableAnimations;
        JavaPath = config.JavaPath;
        _initializing = false;
    }

    public ObservableCollection<SidebarItem> Categories { get; }

    public ObservableCollection<JavaChoice> JavaChoices { get; }

    [ObservableProperty]
    private SidebarItem? _selectedCategoryItem;

    [ObservableProperty]
    private string _gameDirectory = "";

    [ObservableProperty]
    private bool _autoMemory = true;

    [ObservableProperty]
    private decimal _memoryMegabytes = 2048;

    [ObservableProperty]
    private int _windowTypeIndex;

    [ObservableProperty]
    private decimal _windowWidth = 854;

    [ObservableProperty]
    private decimal _windowHeight = 480;

    [ObservableProperty]
    private string _extraJvmArguments = "";

    [ObservableProperty]
    private string _extraGameArguments = "";

    [ObservableProperty]
    private int _downloadSourceIndex = 1;

    [ObservableProperty]
    private decimal _downloadThreads = 16;

    [ObservableProperty]
    private decimal _retryCount = 3;

    [ObservableProperty]
    private bool _closeLauncherAfterLaunch;

    [ObservableProperty]
    private bool _showGameOutput = true;

    [ObservableProperty]
    private bool _enableAnimations = true;

    [ObservableProperty]
    private string _javaPath = "";

    [ObservableProperty]
    private JavaChoice? _selectedJavaChoice;

    [ObservableProperty]
    private bool _isDetectingJava;

    [ObservableProperty]
    private string _javaStatus = "尚未检测 Java";

    [ObservableProperty]
    private string _saveStatus = "";

    public bool IsLaunchCategory => SelectedCategoryItem?.Key == "launch";

    public bool IsJavaCategory => SelectedCategoryItem?.Key == "java";

    public bool IsDownloadCategory => SelectedCategoryItem?.Key == "download";

    public bool IsLauncherCategory => SelectedCategoryItem?.Key == "launcher";

    public bool CanEditMemory => !AutoMemory;

    public bool CanEditWindowSize => WindowTypeIndex == 3;

    public string DefaultGameDirectory => PlatformInfo.DefaultMinecraftDirectory;

    public string EffectiveGameDirectory => _services.Config.EffectiveGameDirectory;

    public string GameDirectoryHint => "留空使用默认目录：" + DefaultGameDirectory;

    public event EventHandler? Saved;

    public async Task ActivateAsync()
    {
        if (_javaLoaded)
        {
            return;
        }

        await DetectJavaAsync().ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task DetectJavaAsync()
    {
        IsDetectingJava = true;
        JavaStatus = "正在扫描本机 Java…";
        try
        {
            var installations = await _services.Launch.GetJavaInstallationsAsync(true).ConfigureAwait(true);
            JavaChoices.Clear();
            JavaChoices.Add(JavaChoice.Auto);
            foreach (var installation in installations)
            {
                JavaChoices.Add(new JavaChoice(
                    installation.ExecutablePath,
                    $"{installation.DisplayName} · {installation.VersionText}",
                    installation));
            }

            var manualPath = JavaPath.Trim();
            if (manualPath.Length > 0)
            {
                var manual = installations.FirstOrDefault(x =>
                    x.ExecutablePath.Equals(manualPath, StringComparison.OrdinalIgnoreCase));
                if (manual is null)
                {
                    var inspected = await _services.JavaDetector.InspectAsync(manualPath).ConfigureAwait(true);
                    if (inspected is not null)
                    {
                        JavaChoices.Add(new JavaChoice(inspected.ExecutablePath, $"{inspected.DisplayName} · 手动指定", inspected));
                        manualPath = inspected.ExecutablePath;
                    }
                }
            }

            var configured = _services.Config.JavaPath;
            SelectedJavaChoice = JavaChoices.FirstOrDefault(x =>
                                    !string.IsNullOrEmpty(x.Path) &&
                                    x.Path.Equals(configured, StringComparison.OrdinalIgnoreCase))
                                ?? JavaChoices[0];
            JavaStatus = installations.Count == 0
                ? "未检测到 Java；可安装 Java 或手动填写路径"
                : $"已检测到 {installations.Count} 个 Java";
            _javaLoaded = true;
        }
        catch (Exception ex)
        {
            JavaStatus = $"检测 Java 失败：{ex.Message}";
            _services.Logger.Error("检测 Java 失败", ex);
        }
        finally
        {
            IsDetectingJava = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var config = _services.Config;
        config.GameDirectory = GameDirectory.Trim();
        config.MemoryMegabytes = AutoMemory ? 0 : (int)MemoryMegabytes;
        config.WindowType = WindowTypeIndex;
        config.WindowWidth = (int)WindowWidth;
        config.WindowHeight = (int)WindowHeight;
        config.ExtraJvmArguments = ExtraJvmArguments.Trim();
        config.ExtraGameArguments = ExtraGameArguments.Trim();
        config.DownloadSource = DownloadSourceIndex == 0 ? DownloadSourceKind.Official : DownloadSourceKind.Bmclapi;
        config.DownloadThreads = (int)DownloadThreads;
        config.RetryCount = (int)RetryCount;
        config.CloseLauncherAfterLaunch = CloseLauncherAfterLaunch;
        config.ShowGameOutput = ShowGameOutput;
        config.EnableAnimations = EnableAnimations;

        var selectedJava = SelectedJavaChoice?.Path ?? "";
        config.JavaPath = string.IsNullOrWhiteSpace(JavaPath) ? selectedJava : JavaPath.Trim();
        _services.DownloadClient.RetryCount = config.RetryCount;

        await _services.ConfigStore.SaveAsync().ConfigureAwait(true);
        SaveStatus = $"设置已保存（{DateTime.Now:HH:mm:ss}）";
        OnPropertyChanged(nameof(EffectiveGameDirectory));
        OnPropertyChanged(nameof(DefaultGameDirectory));
        OnPropertyChanged(nameof(GameDirectoryHint));
        OnPropertyChanged(nameof(JavaSummary));
        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        _services.System.OpenFolder(_services.Config.EffectiveGameDirectory);
    }

    [RelayCommand]
    private void ResetJavaToAuto()
    {
        JavaPath = "";
        SelectedJavaChoice = JavaChoices.FirstOrDefault();
        JavaStatus = "已选择自动匹配 Java";
    }

    partial void OnSelectedCategoryItemChanged(SidebarItem? value)
    {
        OnPropertyChanged(nameof(IsLaunchCategory));
        OnPropertyChanged(nameof(IsJavaCategory));
        OnPropertyChanged(nameof(IsDownloadCategory));
        OnPropertyChanged(nameof(IsLauncherCategory));
    }

    partial void OnAutoMemoryChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditMemory));
        if (_initializing)
        {
            return;
        }

        if (value)
        {
            MemoryMegabytes = ComputeAutoMemory();
        }
    }

    partial void OnWindowTypeIndexChanged(int value) => OnPropertyChanged(nameof(CanEditWindowSize));

    private static int ComputeAutoMemory()
    {
        var config = new LauncherConfig();
        return config.GetEffectiveMemoryMegabytes();
    }

    /// <summary>供 XAML 显示当前 Java 的说明。</summary>
    public string JavaHint => "未指定路径时，启动器会根据游戏版本自动选择最合适的 Java。";

    public string JavaSummary
    {
        get
        {
            var path = _services.Config.JavaPath;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return "当前使用：" + path;
            }

            return "当前使用：自动选择";
        }
    }
}
