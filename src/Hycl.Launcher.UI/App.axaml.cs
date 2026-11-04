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
            // 启动加载页先出现（对应 PCL-CE 的图标弹跳），随后再进入主窗口的耗时构造；
            // splash 的弹跳动画由合成器驱动，UI 线程被服务初始化阻塞时仍会继续播放
            var splash = new SplashWindow();
            splash.Show();

            // 先订阅再构造主窗口：构造完成即应用持久化主题（light/dark；对应 PCL-CE 个性化-浅色/深色）
            MainWindow.ViewModelReady += viewModel =>
            {
                ThemeManager.Apply(viewModel.Settings.CurrentTheme);
                viewModel.Settings.ThemeChanged += (_, theme) => Dispatcher.UIThread.Post(() => ThemeManager.Apply(theme));
            };

            // 让 splash 先完成首帧渲染（Background 优先级排在渲染之后），再阻塞 UI 线程构造主窗口
            Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    var main = new MainWindow();
                    desktop.MainWindow = main;
                    main.Show();
                    // 至少展示一个完整弹跳周期，避免一闪而过（PCL-CE 的 splash 也驻留片刻）
                    main.Opened += async (_, _) =>
                    {
                        await Task.Delay(700);
                        splash.Close();
                    };
                    // 主窗口异常关闭时保证 splash 一并退出
                    main.Closed += (_, _) => splash.Close();
                },
                DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>把 Core 的调度抽象接到 Avalonia UI 线程。</summary>
internal sealed class AvaloniaDispatcher : IAppDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
