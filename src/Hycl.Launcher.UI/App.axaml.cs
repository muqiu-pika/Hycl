using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.UI;

public partial class App : Application
{
    private bool _splashFading;

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

                    // 关闭挂钩必须先于 Show() 注册：Avalonia 在 Show() 内部同步触发 Opened，
                    // 之后再订阅会永远错过（splash 将一直悬浮在主窗口之上）
                    EventHandler closeSplash = (_, _) => FadeOutAndClose(splash);
                    main.Opened += closeSplash;
                    main.Closed += closeSplash;

                    // 兜底：无论事件是否如期触发，15 秒后强制关闭 splash
                    var failsafe = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                    failsafe.Tick += (_, _) =>
                    {
                        failsafe.Stop();
                        FadeOutAndClose(splash);
                    };
                    failsafe.Start();

                    main.Show();
                },
                DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>主窗口就绪后启动画面淡出再关闭（逐帧驱动，沿用 splash 现有的手动动画方案）。</summary>
    private void FadeOutAndClose(SplashWindow splash)
    {
        if (_splashFading)
        {
            return;
        }

        _splashFading = true;
        var start = Stopwatch.GetTimestamp();
        const double durationMs = 220;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
            var progress = Math.Clamp(elapsedMs / durationMs, 0d, 1d);
            splash.Opacity = 1d - progress;
            if (progress < 1d)
            {
                return;
            }

            timer.Stop();
            splash.Close();
        };
        timer.Start();
    }
}

/// <summary>把 Core 的调度抽象接到 Avalonia UI 线程。</summary>
internal sealed class AvaloniaDispatcher : IAppDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
