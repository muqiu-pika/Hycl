using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace Hycl.Launcher.UI;

/// <summary>
/// 启动加载页（对应 PCL-CE 的图标弹跳加载画面）：
/// 在主窗口构造（服务初始化/版本清单预取）期间显示，主窗口 Opened 后关闭。
/// 弹跳用 DispatcherTimer 逐帧驱动——Avalonia 12 的 XAML Animation 不支持内联
/// RenderTransform 动画器，且本项目实测 Animation.RunAsync 不产生中间帧，
/// 与 MainWindow 页面切换过渡采用同一套手动驱动方案。
/// </summary>
public partial class SplashWindow : Window
{
    private bool _closed;

    public SplashWindow()
    {
        InitializeComponent();
        Closed += (_, _) => _closed = true;
        Opened += (_, _) => RunBounceLoop();
    }

    private void RunBounceLoop()
    {
        var logo = this.FindControl<Image>("SplashLogo");
        var shadow = this.FindControl<Ellipse>("SplashShadow");
        if (logo is null)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();
        const double periodMs = 720; // 一个弹跳周期

        _ = Task.Run(async () =>
        {
            while (!_closed)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                var phase = elapsedMs % periodMs / periodMs; // 0..1
                var bounce = Math.Sin(phase * Math.PI); // 0→1→0
                var lift = bounce * 20;
                var squash = 1 - bounce * 0.05;

                Dispatcher.UIThread.Post(() =>
                {
                    if (_closed)
                    {
                        return;
                    }

                    logo.RenderTransform = TransformOperations.Parse(
                        $"translateY({-lift:0.#}px) scale({1 + bounce * 0.04:0.###}, {squash:0.###})");
                    if (shadow is not null)
                    {
                        shadow.RenderTransform = TransformOperations.Parse(
                            $"scale({1 - bounce * 0.28:0.###}, {1 - bounce * 0.28:0.###})");
                        shadow.Opacity = 0.35 - bounce * 0.15;
                    }
                });

                await Task.Delay(16).ConfigureAwait(false);
            }
        });
    }
}
