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
        PerfTrace.Mark("app_init_begin");
        AvaloniaXamlLoader.Load(this);
        PerfTrace.Mark("app_styles_loaded");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        PerfTrace.Mark("framework_init");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 单实例：第二个进程进入时把主窗口呼起到最前（窗口此刻可能尚未创建，回调里判空）
            SingleInstance.StartListening(() => desktop.MainWindow);

            // 先订阅再构造主窗口：构造完成即应用持久化主题（light/dark + 主题色）
            MainWindow.ViewModelReady += viewModel =>
            {
                ThemeManager.Apply(viewModel.Settings.CurrentTheme, viewModel.Settings.CurrentAccent);
                viewModel.Settings.ThemeChanged += (_, theme) => Dispatcher.UIThread.Post(
                    () => ThemeManager.Apply(theme, viewModel.Settings.CurrentAccent));
                viewModel.Settings.AccentChanged += (_, accent) => Dispatcher.UIThread.Post(
                    () => ThemeManager.ApplyAccent(accent));
            };

            // 启动即为主窗口本身：不再弹独立加载窗，启动动画在主窗口内铺满播放
            Dispatcher.UIThread.InvokeAsync(
                async () =>
                {
                    PerfTrace.Mark("window_create_begin");
                    var main = new MainWindow();
                    desktop.MainWindow = main;
                    // 关闭启动动画时没有启动层遮挡，若先出帧会看到空白内容区；
                    // 这条路径保持优化前的观感：先备好服务/页面并在 Show 前构建首屏。
                    if (!main.StartupAnimationDisabled)
                    {
                        PerfTrace.Mark("window_show_begin");
                        main.Show();
                        PerfTrace.Mark("window_show_returned");
                        _ = AttachServicesAsync(main);
                        return;
                    }

                    await AttachServicesAsync(main).ConfigureAwait(true);
                    PerfTrace.Mark("window_show_begin");
                    main.Show();
                    PerfTrace.Mark("window_show_returned");
                },
                DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>窗口先出首帧，再异步创建 Core 服务并接入页面 ViewModel（落在启动层展示期内）。</summary>
    private static async Task AttachServicesAsync(MainWindow main)
    {
        var services = await Core.LauncherServices
            .CreateAsync(new AvaloniaDispatcher())
            .ConfigureAwait(true);
        PerfTrace.Mark("services_ready");
        main.AttachServices(services);
    }
}

/// <summary>把 Core 的调度抽象接到 Avalonia UI 线程。</summary>
internal sealed class AvaloniaDispatcher : IAppDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}
