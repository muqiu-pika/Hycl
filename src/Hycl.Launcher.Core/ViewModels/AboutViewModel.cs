using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hycl.Launcher.Core.Game;
using Hycl.Launcher.Core.Platform;

namespace Hycl.Launcher.Core.ViewModels;

/// <summary>关于页：版本、运行环境、路径与开源声明。</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private readonly LauncherServices _services;

    public AboutViewModel(LauncherServices services)
    {
        _services = services;
    }

    public string AppName => "Hyplay Craft Launcher";

    public string EnglishName => "HYCL";

    public string Tagline => "跨平台 Minecraft 启动器 · 以 PCL-CE 为蓝本用 Avalonia 重写";

    public string VersionText => "版本 " + LaunchService.LauncherVersion;

    public string OsText => $"{PlatformInfo.OperatingSystemName} · {PlatformInfo.ArchitectureName}";

    public string FrameworkText => ".NET 10 · Avalonia 12.1.4 · C# / XAML";

    public string DataDirectory => _services.Paths.DataDirectory;

    public string GameDirectory => _services.Config.EffectiveGameDirectory;

    public string LogFile => _services.Logger.LogFilePath;

    public string LicenseNote =>
        "本项目为独立重写实现，参考并致敬 PCL2-CE（Plain Craft Launcher 2 社区版）与 PCL.Neo 的界面与功能设计。" +
        Environment.NewLine +
        "原项目版权归其作者与贡献者所有；本项目代码按 Avalonia 重写，不包含 WPF 源码直接拷贝。";

    public string Disclaimer =>
        "Minecraft 是 Mojang Studios 的商标。本项目与 Mojang 或 Microsoft 无关联。";

    public void NotifyPathsChanged()
    {
        OnPropertyChanged(nameof(GameDirectory));
        OnPropertyChanged(nameof(DataDirectory));
        OnPropertyChanged(nameof(LogFile));
    }

    [RelayCommand]
    private void OpenUrl(string? url)
    {
        if (!string.IsNullOrWhiteSpace(url))
        {
            _services.System.OpenUrl(url);
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        _services.System.OpenFolder(_services.Paths.DataDirectory);
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        _services.System.OpenFolder(Path.GetDirectoryName(_services.Logger.LogFilePath) ?? _services.Paths.DataDirectory);
    }

    [RelayCommand]
    private void OpenGameFolder()
    {
        _services.System.OpenFolder(_services.Config.EffectiveGameDirectory);
    }
}
