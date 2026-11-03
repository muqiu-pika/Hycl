using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.UI;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 先订阅再构造主窗口：构造完成即应用持久化主题（light/dark；对应 PCL-CE 个性化-浅色/深色）
            MainWindow.ViewModelReady += viewModel =>
            {
                ThemeManager.Apply(viewModel.Settings.CurrentTheme);
                viewModel.Settings.ThemeChanged += (_, theme) => Dispatcher.UIThread.Post(() => ThemeManager.Apply(theme));
            };
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>把 Core 的调度抽象接到 Avalonia UI 线程。</summary>
internal sealed class AvaloniaDispatcher : IAppDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
