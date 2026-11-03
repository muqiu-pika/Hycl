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
