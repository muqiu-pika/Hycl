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
            // 先订阅再构造主窗口：构造完成即应用持久化主题（light/dark；对应 PCL-CE 个性化-浅色/深色）
            MainWindow.ViewModelReady += viewModel =>
            {
                ThemeManager.Apply(viewModel.Settings.CurrentTheme);
                viewModel.Settings.ThemeChanged += (_, theme) => Dispatcher.UIThread.Post(() => ThemeManager.Apply(theme));
            };

            // 启动即为主窗口本身：不再弹独立加载窗，启动动画在主窗口内铺满播放
            Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    PerfTrace.Mark("window_create_begin");
                    var main = new MainWindow();
                    desktop.MainWindow = main;
                    PerfTrace.Mark("window_show_begin");
                    main.Show();
                    PerfTrace.Mark("window_show_returned");
                    // 服务创建放在首帧之后：配置/账号读取与页面 VM 构造都在启动层展示期内完成。
                    AttachServicesAsync(main);
                },
                DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>窗口先出首帧，再异步创建 Core 服务并接入页面 ViewModel（落在启动层展示期内）。</summary>
    private static async void AttachServicesAsync(MainWindow main)
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
