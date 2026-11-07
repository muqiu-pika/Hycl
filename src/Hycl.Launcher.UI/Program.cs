using Avalonia;
using Hycl.Launcher.Core.Services;

namespace Hycl.Launcher.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        PerfTrace.Mark("process_main");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}
